﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Parsers.ElasticQueries.Extensions;
using Foundatio.Repositories.Elasticsearch.Configuration;
using Foundatio.Repositories.Elasticsearch.Extensions;
using Foundatio.Repositories.Elasticsearch.Queries.Builders;
using Foundatio.Repositories.Extensions;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Options;
using Foundatio.Repositories.Queries;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Nest;

namespace Foundatio.Repositories.Elasticsearch {
    public abstract class ElasticReadOnlyRepositoryBase<T> : IElasticReadOnlyRepository<T> where T : class, new() {
        protected static readonly bool HasIdentity = typeof(IIdentity).IsAssignableFrom(typeof(T));
        protected static readonly bool HasDates = typeof(IHaveDates).IsAssignableFrom(typeof(T));
        protected static readonly bool HasCreatedDate = typeof(IHaveCreatedDate).IsAssignableFrom(typeof(T));
        protected static readonly bool SupportsSoftDeletes = typeof(ISupportSoftDeletes).IsAssignableFrom(typeof(T));
        protected static readonly bool HasVersion = typeof(IVersioned).IsAssignableFrom(typeof(T));
        protected static readonly string EntityTypeName = typeof(T).Name;
        protected static readonly IReadOnlyCollection<T> EmptyList = new List<T>(0).AsReadOnly();
        protected readonly string _idField;

        protected readonly ILogger _logger;
        protected readonly IElasticClient _client;

        private ScopedCacheClient _scopedCacheClient;

        protected ElasticReadOnlyRepositoryBase(IIndex<T> index) {
            ElasticIndex = index;
            if (HasIdentity)
                _idField = index.GetFieldName(doc => ((IIdentity)doc).Id) ?? "id";
            _client = index.Configuration.Client;
            SetCache(index.Configuration.Cache);
            _logger = index.Configuration.LoggerFactory.CreateLogger(GetType());
        }

        public virtual Task<FindResults<T>> FindAsync(RepositoryQueryDescriptor<T> query, CommandOptionsDescriptor<T> options = null) {
            return FindAsAsync<T>(query.Configure(), options.Configure());
        }

        public virtual Task<FindResults<T>> FindAsync(IRepositoryQuery query, ICommandOptions options = null) {
            return FindAsAsync<T>(query, options);
        }

        protected ICollection<Field> DefaultExcludes { get; } = new List<Field>();

        public virtual Task<FindResults<TResult>> FindAsAsync<TResult>(RepositoryQueryDescriptor<T> query, CommandOptionsDescriptor<T> options = null) where TResult : class, new() {
            return FindAsAsync<TResult>(query.Configure(), options.Configure());
        }

        public virtual async Task<FindResults<TResult>> FindAsAsync<TResult>(IRepositoryQuery query, ICommandOptions options = null) where TResult : class, new() {
            if (query == null)
                query = new RepositoryQuery();

            options = ConfigureOptions(options);
            bool useSnapshotPaging = options.ShouldUseSnapshotPaging();
            // don't use caching with snapshot paging.
            bool allowCaching = IsCacheEnabled && useSnapshotPaging == false;

            await OnBeforeQueryAsync(query, options, typeof(TResult)).AnyContext();

            async Task<FindResults<TResult>> GetNextPageFunc(FindResults<TResult> r) {
                var previousResults = r;
                if (previousResults == null)
                    throw new ArgumentException(nameof(r));

                string scrollId = previousResults.GetScrollId();
                if (!String.IsNullOrEmpty(scrollId)) {
                    var scrollResponse = await _client.ScrollAsync<TResult>(options.GetSnapshotLifetime(), scrollId).AnyContext();
                    if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                        _logger.LogTrace(scrollResponse.GetRequest());

                    var results = scrollResponse.ToFindResults();
                    results.Page = previousResults.Page + 1;
                    results.HasMore = scrollResponse.Hits.Count >= options.GetLimit();
                    return results;
                }

                if (options.ShouldUseSearchAfterPaging()) {
                    var lastDocument = previousResults.Documents.LastOrDefault();
                    if (lastDocument != null) {
                        var searchAfterValues = new List<object>();
                        var sorts = query.GetSorts();
                        if (sorts.Count > 0) {
                            foreach (var sort in query.GetSorts()) {
                                if (sort.SortKey.Property?.DeclaringType == lastDocument.GetType()) {
                                    searchAfterValues.Add(sort.SortKey.Property.GetValue(lastDocument));
                                } else if (typeof(TResult) == typeof(T) && sort.SortKey.Expression is Expression<Func<T, object>> valueGetterExpression) {
                                    var valueGetter = valueGetterExpression.Compile();
                                    var typedLastDocument = lastDocument as T;
                                    if (typedLastDocument != null) {
                                        var value = valueGetter.Invoke(typedLastDocument);
                                        searchAfterValues.Add(value);
                                    }
                                } else if (sort.SortKey.Name != null) {
                                    var propertyInfo = lastDocument.GetType().GetProperty(sort.SortKey.Name);
                                    if (propertyInfo != null)
                                        searchAfterValues.Add(propertyInfo.GetValue(lastDocument));
                                } else {
                                    // TODO: going to to need to take the Expression and pull the string name from it
                                }
                            }
                        } else if (lastDocument is IIdentity lastDocumentId) {
                            searchAfterValues.Add(lastDocumentId.Id);
                        }

                        if (searchAfterValues.Count > 0)
                            options.SearchAfter(searchAfterValues.ToArray());
                        else
                            throw new ArgumentException("Unable to automatically calculate values for SearchAfterPaging.");
                    }
                }

                if (options == null)
                    return new FindResults<TResult>();

                options?.PageNumber(!options.HasPageNumber() ? 2 : options.GetPage() + 1);
                return await FindAsAsync<TResult>(query, options).AnyContext();
            }

            string cacheSuffix = options?.HasPageLimit() == true ? String.Concat(options.GetPage().ToString(), ":", options.GetLimit().ToString()) : null;

            FindResults<TResult> result;
            if (allowCaching) {
                result = await GetCachedQueryResultAsync<FindResults<TResult>>(options, cacheSuffix: cacheSuffix).AnyContext();
                if (result != null) {
                    ((IGetNextPage<TResult>)result).GetNextPageFunc = async r => await GetNextPageFunc(r).AnyContext();
                    return result;
                }
            }

            ISearchResponse<TResult> response;
            if (useSnapshotPaging == false || !options.HasSnapshotScrollId()) {
                var searchDescriptor = await CreateSearchDescriptorAsync(query, options).AnyContext();
                if (useSnapshotPaging)
                    searchDescriptor.Scroll(options.GetSnapshotLifetime());

                if (query.ShouldOnlyHaveIds())
                    searchDescriptor.Source(false);

                response = await _client.SearchAsync<TResult>(searchDescriptor).AnyContext();
            } else {
                response = await _client.ScrollAsync<TResult>(options.GetSnapshotLifetime(), options.GetSnapshotScrollId()).AnyContext();
            }

            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                _logger.LogTrace(response.GetRequest());
            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return new FindResults<TResult>();

                string message = response.GetErrorMessage();
                _logger.LogError(response.OriginalException, message);
                throw new ApplicationException(message, response.OriginalException);
            }

            if (useSnapshotPaging) {
                result = response.ToFindResults();
                result.HasMore = response.Hits.Count >= options.GetLimit();
                ((IGetNextPage<TResult>)result).GetNextPageFunc = GetNextPageFunc;
            } else if (options.HasPageLimit()) {
                int limit = options.GetLimit();
                result = response.ToFindResults(limit);
                result.HasMore = response.Hits.Count > limit;
                ((IGetNextPage<TResult>)result).GetNextPageFunc = GetNextPageFunc;
            } else {
                result = response.ToFindResults();
            }

            result.Page = options.GetPage();

            if (!allowCaching)
                return result;

            var nextPageFunc = ((IGetNextPage<TResult>)result).GetNextPageFunc;
            ((IGetNextPage<TResult>)result).GetNextPageFunc = null;
            await SetCachedQueryResultAsync(options, result, cacheSuffix: cacheSuffix).AnyContext();
            ((IGetNextPage<TResult>)result).GetNextPageFunc = nextPageFunc;

            return result;
        }

        public virtual Task<FindHit<T>> FindOneAsync(RepositoryQueryDescriptor<T> query, CommandOptionsDescriptor<T> options = null) {
            return FindOneAsync(query.Configure(), options.Configure());
        }

        public virtual async Task<FindHit<T>> FindOneAsync(IRepositoryQuery query, ICommandOptions options = null) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            options = ConfigureOptions(options);
            var result = IsCacheEnabled ? await GetCachedQueryResultAsync<FindHit<T>>(options).AnyContext() : null;
            if (result != null)
                return result;

            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = (await CreateSearchDescriptorAsync(query, options).AnyContext()).Size(1);
            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                _logger.LogTrace(response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return FindHit<T>.Empty;

                string message = response.GetErrorMessage();
                _logger.LogError(response.OriginalException, message);
                throw new ApplicationException(message, response.OriginalException);
            }

            result = response.Hits.FirstOrDefault()?.ToFindHit();
            if (IsCacheEnabled)
                await SetCachedQueryResultAsync(options, result).AnyContext();

            return result;
        }       

        public virtual Task<FindResults<T>> SearchAsync(ISystemFilter systemFilter, string filter = null, string criteria = null, string sort = null, string aggregations = null, ICommandOptions options = null) {
            var search = NewQuery()
                .MergeFrom(systemFilter?.GetQuery())
                .FilterExpression(filter)
                .SearchExpression(criteria)
                .AggregationsExpression(aggregations)
                .SortExpression(sort);

            return FindAsync(search, options);
        }

        public virtual async Task<T> GetByIdAsync(Id id, ICommandOptions options = null) {
            if (String.IsNullOrEmpty(id.Value))
                return null;

            options = ConfigureOptions(options);

            CacheValue<T> hit = null;
            if (IsCacheEnabled && options.ShouldReadCache())
                hit = await Cache.GetAsync<T>(id).AnyContext();

            bool isTraceLogLevelEnabled = _logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace);
            if (hit != null && hit.HasValue) {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Cache hit: type={ElasticType} key={Id}", ElasticIndex.Name, id);
                return hit.Value;
            }

            T document = null;
            if (!HasParent || id.Routing != null) {
                var request = new GetRequest(GetIndexById(id), "_doc", id.Value);
                if (id.Routing != null)
                    request.Routing = id.Routing;
                var response = await _client.GetAsync<T>(request).AnyContext();
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace(response.GetRequest());

                document = response.Found ? response.ToFindHit().Document : null;
            } else {
                // we don't have the parent id so we have to do a query
                // TODO: Ensure this is find one query is not cached.
                var findResult = await FindOneAsync(NewQuery().Id(id)).AnyContext();
                if (findResult != null)
                    document = findResult.Document;
            }

            if (IsCacheEnabled && options.ShouldUseCache())
                await Cache.SetAsync(id, document, options.GetExpiresIn()).AnyContext();

            return document;
        }

        public virtual async Task<IReadOnlyCollection<T>> GetByIdsAsync(Ids ids, ICommandOptions options = null) {
            var idList = ids?.Distinct().Where(i => !String.IsNullOrEmpty(i)).ToList();
            if (idList == null || idList.Count == 0)
                return EmptyList;

            if (!HasIdentity)
                throw new NotSupportedException("Model type must implement IIdentity.");

            options = ConfigureOptions(options);

            var hits = new List<T>();
            if (IsCacheEnabled && options.ShouldReadCache()) {
                var cacheHits = await Cache.GetAllAsync<T>(idList.Select(id => id.Value)).AnyContext();
                hits.AddRange(cacheHits.Where(kvp => kvp.Value.HasValue).Select(kvp => kvp.Value.Value));
            }

            var itemsToFind = idList.Except(hits.OfType<IIdentity>().Select(i => (Id)i.Id)).ToList();
            if (itemsToFind.Count == 0)
                return hits.Where(h => h != null).ToList().AsReadOnly();

            var multiGet = new MultiGetDescriptor();
            foreach (var id in itemsToFind.Where(i => i.Routing != null || !HasParent)) {
                multiGet.Get<T>((Func<MultiGetOperationDescriptor<T>, IMultiGetOperation>)((MultiGetOperationDescriptor<T> f) => {
                    f.Id(id.Value).Index(GetIndexById(id)).Type("_doc");
                    if (id.Routing != null)
                        f.Routing(id.Routing);

                    return f;
                }));
            }

            var multiGetResults = await _client.MultiGetAsync(multiGet).AnyContext();
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                _logger.LogTrace(multiGetResults.GetRequest());

            foreach (var doc in multiGetResults.Documents) {
                hits.Add(((IMultiGetHit<T>)doc).ToFindHit().Document);
                itemsToFind.Remove(new Id(doc.Id, doc.Routing));
            }

            // fallback to doing a find
            if (itemsToFind.Count > 0 && (HasParent || HasMultipleIndexes)) {
                var response = await FindAsync(q => q.Id(itemsToFind.Select(id => id.Value)), o => o.PageLimit(1000)).AnyContext();
                do {
                    if (response.Hits.Count > 0)
                        hits.AddRange(response.Hits.Where(h => h.Document != null).Select(h => h.Document));
                } while (await response.NextPageAsync().AnyContext());
            }

            if (IsCacheEnabled && options.ShouldUseCache()) {
                var expiresIn = options.GetExpiresIn();
                await Cache.SetAllAsync(idList.ToDictionary(id => id.Value, id => hits.OfType<IIdentity>().FirstOrDefault(h => h.Id == id.Value))).AnyContext();
            }

            return hits.Where(h => h != null).ToList().AsReadOnly();
        }

        public virtual Task<FindResults<T>> GetAllAsync(ICommandOptions options = null) {
            return FindAsync(null, options);
        }

        public virtual async Task<bool> ExistsAsync(Id id) {
            if (String.IsNullOrEmpty(id.Value))
                return false;

            if (!HasParent || id.Routing != null) {
                var response = await _client.DocumentExistsAsync(new DocumentPath<T>(id.Value), d => {
                    d.Index(GetIndexById(id));
                    if (id.Routing != null)
                        d.Routing(id.Routing);

                    return d;
                }).AnyContext();
                if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                    _logger.LogTrace(response.GetRequest());

                return response.Exists;
            }

            return await ExistsAsync(q => q.Id(id)).AnyContext();
        }

        public virtual Task<bool> ExistsAsync(RepositoryQueryDescriptor<T> query) {
            return ExistsAsync(query.Configure());
        }

        public virtual async Task<bool> ExistsAsync(IRepositoryQuery query) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var options = ConfigureOptions(null);
            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = (await CreateSearchDescriptorAsync(query, options).AnyContext()).Size(1);
            searchDescriptor.DocvalueFields(_idField);
            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                _logger.LogTrace(response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return false;

                string message = response.GetErrorMessage();
                _logger.LogError(response.OriginalException, message);
                throw new ApplicationException(message, response.OriginalException);
            }

            return response.HitsMetaData.Total > 0;
        }

        public virtual Task<CountResult> CountAsync(RepositoryQueryDescriptor<T> query, CommandOptionsDescriptor<T> options = null) {
            return CountAsync(query.Configure(), options.Configure());
        }

        public virtual async Task<CountResult> CountAsync(IRepositoryQuery query, ICommandOptions options = null) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            options = ConfigureOptions(options);
            var result = await GetCachedQueryResultAsync<CountResult>(options, "count").AnyContext();
            if (result != null)
                return result;

            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = await CreateSearchDescriptorAsync(query, options).AnyContext();
            searchDescriptor.Size(0);

            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                _logger.LogTrace(response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return new CountResult();

                string message = response.GetErrorMessage();
                _logger.LogError(response.OriginalException, message);
                throw new ApplicationException(message, response.OriginalException);
            }

            result = new CountResult(response.Total, response.ToAggregations());
            await SetCachedQueryResultAsync(options, result, "count").AnyContext();
            return result;
        }

        public virtual async Task<long> CountAsync(ICommandOptions options = null) {
            options = ConfigureOptions(options);

            var response = await _client.CountAsync<T>(c => c.Query(q => q.MatchAll()).Index(String.Join(",", GetIndexesByQuery(null))).Type("_doc")).AnyContext();
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                _logger.LogTrace(response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return 0;

                string message = response.GetErrorMessage();
                _logger.LogError(response.OriginalException, message);
                throw new ApplicationException(message, response.OriginalException);
            }

            return response.Count;
        }

        public virtual Task<CountResult> CountBySearchAsync(ISystemFilter systemFilter, string filter = null, string aggregations = null, ICommandOptions options = null) {
            var search = NewQuery()
                .MergeFrom(systemFilter?.GetQuery())
                .FilterExpression(filter)
                .AggregationsExpression(aggregations);

            return CountAsync(search, options);
        }

        protected virtual IRepositoryQuery<T> NewQuery() {
            return new RepositoryQuery<T>();
        }

        protected virtual IRepositoryQuery ConfigureQuery(IRepositoryQuery query) {
            if (query == null)
                query = new RepositoryQuery<T>();

            if (DefaultExcludes.Count > 0 && query.GetExcludes().Count == 0)
                query.Exclude(DefaultExcludes);

            return query;
        }

        public bool IsCacheEnabled { get; private set; } = true;
        protected ScopedCacheClient Cache => _scopedCacheClient ?? new ScopedCacheClient(new NullCacheClient());

        private void SetCache(ICacheClient cache) {
            IsCacheEnabled = cache != null;
            _scopedCacheClient = new ScopedCacheClient(cache ?? new NullCacheClient(), EntityTypeName);
        }

        protected void DisableCache() {
            IsCacheEnabled = false;
            _scopedCacheClient = new ScopedCacheClient(new NullCacheClient(), EntityTypeName);
        }

        protected virtual async Task InvalidateCacheAsync(IReadOnlyCollection<ModifiedDocument<T>> documents, ICommandOptions options) {
            if (!IsCacheEnabled)
                return;

            if (documents != null && documents.Count > 0 && HasIdentity) {
                var keys = documents.Select(d => ((IIdentity)d.Value).Id).ToList();

                if (keys.Count > 0)
                    await Cache.RemoveAllAsync(keys).AnyContext();
            }
        }

        public virtual Task InvalidateCacheAsync(T document, ICommandOptions options = null) {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            if (!IsCacheEnabled)
                return Task.CompletedTask;

            return InvalidateCacheAsync(new[] { document }, options);
        }

        public virtual Task InvalidateCacheAsync(IEnumerable<T> documents, ICommandOptions options = null) {
            var docs = documents?.ToList();
            if (docs == null || docs.Any(d => d == null))
                throw new ArgumentNullException(nameof(documents));

            if (!IsCacheEnabled)
                return Task.CompletedTask;

            return InvalidateCacheAsync(docs.Select(d => new ModifiedDocument<T>(d, null)).ToList(), options);
        }

        protected virtual Task<SearchDescriptor<T>> CreateSearchDescriptorAsync(IRepositoryQuery query, ICommandOptions options) {
            return ConfigureSearchDescriptorAsync(null, query, options);
        }

        protected virtual async Task<SearchDescriptor<T>> ConfigureSearchDescriptorAsync(SearchDescriptor<T> search, IRepositoryQuery query, ICommandOptions options) {
            if (search == null)
                search = new SearchDescriptor<T>();

            query = ConfigureQuery(query);
            search.Type("_doc");
            var indices = GetIndexesByQuery(query);
            if (indices?.Length > 0)
                search.Index(String.Join(",", indices));
            if (HasVersion)
                search.Version(HasVersion);

            search.IgnoreUnavailable();

            await ElasticIndex.QueryBuilder.ConfigureSearchAsync(query, options, search).AnyContext();

            return search;
        }

        protected virtual ICommandOptions ConfigureOptions(ICommandOptions options) {
            if (options == null)
                options = new CommandOptions<T>();

            options.ElasticIndex(ElasticIndex);

            return options;
        }

        protected virtual string[] GetIndexesByQuery(IRepositoryQuery query, ICommandOptions options = null) {
            return HasMultipleIndexes ? TimeSeriesIndex.GetIndexesByQuery(query) : new[] { ElasticIndex.Name };
        }

        protected virtual string GetIndexById(Id id) {
            return HasMultipleIndexes ? TimeSeriesIndex.GetIndexById(id) : ElasticIndex.Name;
        }

        protected Func<T, string> GetParentIdFunc => null; //HasParent ? d => ChildType.GetParentId(d) : (Func<T, string>)null;
        protected Func<T, string> GetDocumentIndexFunc => HasMultipleIndexes ? d => TimeSeriesIndex.GetDocumentIndex(d) : (Func<T, string>)(d => ElasticIndex.Name);

        protected async Task<TResult> GetCachedQueryResultAsync<TResult>(ICommandOptions options, string cachePrefix = null, string cacheSuffix = null) {
            if (!IsCacheEnabled || options == null || !options.ShouldReadCache() || !options.HasCacheKey())
                return default;

            string cacheKey = cachePrefix != null ? cachePrefix + ":" + options.GetCacheKey() : options.GetCacheKey();
            if (!String.IsNullOrEmpty(cacheSuffix))
                cacheKey += ":" + cacheSuffix;

            var result = await Cache.GetAsync<TResult>(cacheKey, default).AnyContext();
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                _logger.LogTrace("Cache {HitOrMiss}: type={ElasticType} key={CacheKey}", (result != null ? "hit" : "miss"), ElasticIndex.Name, cacheKey);

            return result;
        }

        protected async Task SetCachedQueryResultAsync<TResult>(ICommandOptions options, TResult result, string cachePrefix = null, string cacheSuffix = null) {
            if (!IsCacheEnabled || result == null || options == null || !options.ShouldUseCache() || !options.HasCacheKey())
                return;

            string cacheKey = cachePrefix != null ? cachePrefix + ":" + options.GetCacheKey() : options.GetCacheKey();
            if (!String.IsNullOrEmpty(cacheSuffix))
                cacheKey += ":" + cacheSuffix;

            await Cache.SetAsync(cacheKey, result, options.GetExpiresIn()).AnyContext();
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                _logger.LogTrace("Set cache: type={ElasticType} key={CacheKey}", ElasticIndex.Name, cacheKey);
        }

        #region Elastic Type Configuration

        private IIndex<T> _elasticIndex;

        protected IIndex<T> ElasticIndex {
            get { return _elasticIndex; }
            private set {
                _elasticIndex = value;

                // if (_elasticIndex is IChildIndexType<T>) {
                //     HasParent = true;
                //     ChildType = _elasticType as IChildIndexType<T>;
                // } else {
                //     HasParent = false;
                //     ChildType = null;
                // }

                if (_elasticIndex is ITimeSeriesIndex) {
                    HasMultipleIndexes = true;
                    TimeSeriesIndex = _elasticIndex as ITimeSeriesIndex<T>;
                } else {
                    HasMultipleIndexes = false;
                    TimeSeriesIndex = null;
                }
            }
        }

        protected bool HasParent { get; private set; }
        protected bool HasMultipleIndexes { get; private set; }
        protected ITimeSeriesIndex<T> TimeSeriesIndex { get; private set; }

        #endregion

        #region Events

        public AsyncEvent<BeforeQueryEventArgs<T>> BeforeQuery { get; } = new AsyncEvent<BeforeQueryEventArgs<T>>();

        private async Task OnBeforeQueryAsync(IRepositoryQuery query, ICommandOptions options, Type resultType) {
            if (SupportsSoftDeletes && IsCacheEnabled && query.GetSoftDeleteMode() == SoftDeleteQueryMode.ActiveOnly) {
                var deletedIds = await Cache.GetSetAsync<string>("deleted").AnyContext();
                if (deletedIds.HasValue)
                    query.ExcludedId(deletedIds.Value);
            }

            if (BeforeQuery == null)
                return;

            await BeforeQuery.InvokeAsync(this, new BeforeQueryEventArgs<T>(query, options, this, resultType)).AnyContext();
        }

        #endregion
    }
}
