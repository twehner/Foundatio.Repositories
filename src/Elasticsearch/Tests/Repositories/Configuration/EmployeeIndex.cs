﻿using System;
using Foundatio.Repositories.Elasticsearch.Configuration;
using Foundatio.Repositories.Elasticsearch.Tests.Models;

namespace Foundatio.Repositories.Elasticsearch.Tests.Configuration {
    public class EmployeeIndex : ElasticIndex {
        public EmployeeIndex(): base("employees", 1) {
            AddIndexType<Employee>(new ElasticIndexType<Employee>("employee", this));
        }
    }
}