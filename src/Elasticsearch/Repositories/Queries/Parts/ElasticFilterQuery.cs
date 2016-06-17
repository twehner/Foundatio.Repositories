﻿using System;
using Nest;

namespace Foundatio.Repositories.Elasticsearch.Queries {
    public interface IElasticFilterQuery {
        FilterContainer ElasticFilter { get; set; }
    }

    public static class ElasticFilterQueryExtensions {
        public static T WithElasticFilter<T>(this T query, FilterContainer filter) where T : IElasticFilterQuery {
            query.ElasticFilter = filter;
            return query;
        }
    }
}