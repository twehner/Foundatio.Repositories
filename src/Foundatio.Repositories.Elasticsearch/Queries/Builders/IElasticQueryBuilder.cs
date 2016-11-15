using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Parsers.ElasticQueries.Visitors;
using Foundatio.Parsers.LuceneQueries.Visitors;
using Foundatio.Repositories.Elasticsearch.Queries.Options;
using Foundatio.Repositories.Extensions;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Repositories.Elasticsearch.Queries.Builders {
    public interface IElasticQueryBuilder {
        Task BuildAsync<T>(QueryBuilderContext<T> ctx) where T : class, new();
    }

    public class QueryBuilderContext<T> : IElasticQueryVisitorContext, IQueryVisitorContextWithAliasResolver where T : class, new() {
        public QueryBuilderContext(IRepositoryQuery source, IQueryOptions options, SearchDescriptor<T> search = null) {
            Source = source;
            Options = options;
            Search = search ?? new SearchDescriptor<T>();
            var elasticQueryOptions = options as IElasticQueryOptions;
            if (elasticQueryOptions != null)
                ((IQueryVisitorContextWithAliasResolver)this).RootAliasResolver = elasticQueryOptions.RootAliasResolver;
        }

        public IRepositoryQuery Source { get; }
        public IQueryOptions Options { get; }
        public QueryContainer Query { get; set; }
        public QueryContainer Filter { get; set; }
        public SearchDescriptor<T> Search { get; }
        public IDictionary<string, object> Data { get; } = new Dictionary<string, object>();

        AliasResolver IQueryVisitorContextWithAliasResolver.RootAliasResolver { get; set; }

        Operator IElasticQueryVisitorContext.DefaultOperator { get; set; }
        bool IElasticQueryVisitorContext.UseScoring { get; set; }
        string IElasticQueryVisitorContext.DefaultField { get; set; }
        Func<string, IProperty> IElasticQueryVisitorContext.GetPropertyMappingFunc { get; set; }

        public TQuery GetSourceAs<TQuery>() where TQuery : class {
            return Source as TQuery;
        }

        public TOptions GetOptionsAs<TOptions>() where TOptions : class {
            return Options as TOptions;
        }
    }

    public static class ElasticQueryBuilderExtensions {
        public static async Task<QueryContainer> BuildQueryAsync<T>(this IElasticQueryBuilder builder, IRepositoryQuery query, IQueryOptions options, SearchDescriptor<T> search) where T : class, new() {
            var ctx = new QueryBuilderContext<T>(query, options, search);
            await builder.BuildAsync(ctx).AnyContext();

            return new BoolQuery {
                Must = new[] { ctx.Query },
                Filter = new[] { ctx.Filter }
            };
        }

        public static async Task ConfigureSearchAsync<T>(this IElasticQueryBuilder builder, IRepositoryQuery query, IQueryOptions options, SearchDescriptor<T> search) where T : class, new() {
            if (search == null)
                throw new ArgumentNullException(nameof(search));

            var q = await builder.BuildQueryAsync(query, options, search).AnyContext();
            search.Query(d => q);
        }
    }
}