using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Repositories.Models;
using Foundatio.Utility;

namespace Foundatio.Repositories.Elasticsearch.Extensions {
    public static class ElasticIndexExtensions {
        public static FindResults<T> ToFindResults<T>(this Nest.ISearchResponse<T> response, int? limit = null) where T : class, new() {
            var docs = response.Hits.Take(limit ?? Int32.MaxValue).ToFindHits().ToList();
            var data = response.ScrollId != null ? new DataDictionary { { ElasticDataKeys.ScrollId, response.ScrollId } } : null;
            return new FindResults<T>(docs, response.Total, response.ToAggregations(), null, data);
        }

        public static IEnumerable<FindHit<T>> ToFindHits<T>(this IEnumerable<Nest.IHit<T>> hits) where T : class {
            return hits.Select(h => h.ToFindHit());
        }

        public static FindHit<T> ToFindHit<T>(this Nest.IGetResponse<T> hit) where T : class {
            var versionedDoc = hit.Source as IVersioned;
            if (versionedDoc != null)
                versionedDoc.Version = hit.Version;

            var data = new DataDictionary { { ElasticDataKeys.Index, hit.Index }, { ElasticDataKeys.IndexType, hit.Type } };
            return new FindHit<T>(hit.Id, hit.Source, 0, versionedDoc?.Version ?? null, data);
        }

        public static FindHit<T> ToFindHit<T>(this Nest.IHit<T> hit) where T : class {
            var versionedDoc = hit.Source as IVersioned;
            if (versionedDoc != null && hit.Version.HasValue)
                versionedDoc.Version = hit.Version.Value;

            var data = new DataDictionary { { ElasticDataKeys.Index, hit.Index }, { ElasticDataKeys.IndexType, hit.Type } };
            return new FindHit<T>(hit.Id, hit.Source, hit.Score.GetValueOrDefault(), versionedDoc?.Version ?? null, data);
        }

        public static FindHit<T> ToFindHit<T>(this Nest.IMultiGetHit<T> hit) where T : class {
            var versionedDoc = hit.Source as IVersioned;
            if (versionedDoc != null)
                versionedDoc.Version = hit.Version;

            var data = new DataDictionary { { ElasticDataKeys.Index, hit.Index }, { ElasticDataKeys.IndexType, hit.Type } };
            return new FindHit<T>(hit.Id, hit.Source, 0, versionedDoc?.Version ?? null, data);
        }

        public static IAggregate ToAggregate(this Nest.IAggregate aggregate) {
            var valueAggregate = aggregate as Nest.ValueAggregate;
            if (valueAggregate != null)
                return new ValueAggregate { Value = valueAggregate.Value, Data = valueAggregate.Meta };

            var scriptedAggregate = aggregate as Nest.ScriptedMetricAggregate;
            if (scriptedAggregate != null)
                return new ObjectValueAggregate { Value = scriptedAggregate.Value<object>(), Data = scriptedAggregate.Meta };

            var statsAggregate = aggregate as Nest.StatsAggregate;
            if (statsAggregate != null)
                return new StatsAggregate {
                    Count = statsAggregate.Count,
                    Min = statsAggregate.Min,
                    Max = statsAggregate.Max,
                    Average = statsAggregate.Average,
                    Sum = statsAggregate.Sum,
                    Data = statsAggregate.Meta
                };

            var extendedStatsAggregate = aggregate as Nest.ExtendedStatsAggregate;
            if (extendedStatsAggregate != null)
                return new ExtendedStatsAggregate {
                    Count = extendedStatsAggregate.Count,
                    Min = extendedStatsAggregate.Min,
                    Max = extendedStatsAggregate.Max,
                    Average = extendedStatsAggregate.Average,
                    Sum = extendedStatsAggregate.Sum,
                    StdDeviation = extendedStatsAggregate.StdDeviation,
                    StdDeviationBounds = new StandardDeviationBounds {
                        Lower = extendedStatsAggregate.StdDeviationBounds.Lower,
                        Upper = extendedStatsAggregate.StdDeviationBounds.Upper
                    },
                    SumOfSquares = extendedStatsAggregate.SumOfSquares,
                    Variance = extendedStatsAggregate.Variance,
                    Data = extendedStatsAggregate.Meta
                };

            var percentilesAggregate = aggregate as Nest.PercentilesAggregate;
            if (percentilesAggregate != null)
                return new PercentilesAggregate(percentilesAggregate.Items.Select(i => new PercentileItem { Percentile = i.Percentile, Value = i.Value } )) {
                    Data = percentilesAggregate.Meta
                };

            var singleBucketAggregate = aggregate as Nest.SingleBucketAggregate;
            if (singleBucketAggregate != null)
                return new SingleBucketAggregate {
                    Data = singleBucketAggregate.Meta,
                    DocCount = singleBucketAggregate.DocCount
                };

            var bucketAggregation = aggregate as Nest.BucketAggregate;
            if (bucketAggregation != null)
                return new BucketAggregate {
                    Items = bucketAggregation.Items.Select(i => i.ToBucket()).ToList(),
                    DocCountErrorUpperBound = bucketAggregation.DocCountErrorUpperBound,
                    SumOtherDocCount = bucketAggregation.SumOtherDocCount,
                    Data = bucketAggregation.Meta,
                    DocCount = bucketAggregation.DocCount
                };
            
            return null;
        }

        public static IBucket ToBucket(this Nest.IBucket bucket) {
            var dateHistogramBucket = bucket as Nest.DateHistogramBucket;
            if (dateHistogramBucket != null)
                return new DateHistogramBucket(dateHistogramBucket.Aggregations.ToAggregations()) {
                    DocCount = dateHistogramBucket.DocCount,
                    Key = dateHistogramBucket.Key,
                    KeyAsString = dateHistogramBucket.KeyAsString
                };

            var stringKeyedBucket = bucket as Nest.KeyedBucket<string>;
            if (stringKeyedBucket != null)
                return new KeyedBucket<string>(stringKeyedBucket.Aggregations.ToAggregations()) {
                    DocCount = stringKeyedBucket.DocCount,
                    Key = stringKeyedBucket.Key,
                    KeyAsString = stringKeyedBucket.KeyAsString
                };

            var doubleKeyedBucket = bucket as Nest.KeyedBucket<double>;
            if (doubleKeyedBucket != null)
                return new KeyedBucket<double>(doubleKeyedBucket.Aggregations.ToAggregations()) {
                    DocCount = doubleKeyedBucket.DocCount,
                    Key = doubleKeyedBucket.Key,
                    KeyAsString = doubleKeyedBucket.KeyAsString
                };

            var objectKeyedBucket = bucket as Nest.KeyedBucket<object>;
            if (objectKeyedBucket != null)
                return new KeyedBucket<object>(objectKeyedBucket.Aggregations.ToAggregations()) {
                    DocCount = objectKeyedBucket.DocCount,
                    Key = objectKeyedBucket.Key,
                    KeyAsString = objectKeyedBucket.KeyAsString
                };
            
            return null;
        }

        public static IDictionary<string, IAggregate> ToAggregations(this IReadOnlyDictionary<string, Nest.IAggregate> aggregations) {
            if (aggregations == null)
                return null;

            return aggregations.ToDictionary(a => a.Key, a => a.Value.ToAggregate());
        }

        public static IDictionary<string, IAggregate> ToAggregations<T>(this Nest.ISearchResponse<T> res) where T : class {
            return res.Aggregations.ToAggregations();
        }

        public static Nest.PropertiesDescriptor<T> SetupDefaults<T>(this Nest.PropertiesDescriptor<T> pd) where T : class {
            var hasIdentity = typeof(IIdentity).IsAssignableFrom(typeof(T));
            var hasDates = typeof(IHaveDates).IsAssignableFrom(typeof(T));
            var hasCreatedDate = typeof(IHaveCreatedDate).IsAssignableFrom(typeof(T));
            var supportsSoftDeletes = typeof(ISupportSoftDeletes).IsAssignableFrom(typeof(T));

            if (hasIdentity)
                pd.Keyword(p => p.Name(d => (d as IIdentity).Id));

            if (supportsSoftDeletes)
                pd.Boolean(p => p.Name(d => (d as ISupportSoftDeletes).IsDeleted));

            if (hasCreatedDate)
                pd.Date(p => p.Name(d => (d as IHaveCreatedDate).CreatedUtc));

            if (hasDates)
                pd.Date(p => p.Name(d => (d as IHaveDates).UpdatedUtc));

            return pd;
        }
    }
}