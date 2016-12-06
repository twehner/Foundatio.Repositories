﻿using System.Collections.Generic;

namespace Foundatio.Repositories.Models {
    public class PercentileItem {
        public double Percentile { get; set; }
        public double Value { get; set; }
    }

    public class PercentilesAggregate : MetricAggregateBase {
        public PercentilesAggregate(IEnumerable<PercentileItem> items) {
            Items = new List<PercentileItem>(items).AsReadOnly();
        }

        public IReadOnlyCollection<PercentileItem> Items { get; internal set; }
    }
}
