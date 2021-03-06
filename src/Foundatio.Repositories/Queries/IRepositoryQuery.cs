﻿using System;
using Foundatio.Repositories.Options;

namespace Foundatio.Repositories {
    /// <summary>
    /// Query options that control the result of the repository query operations
    /// </summary>
    public interface IRepositoryQuery : IOptions {
        Type GetDocumentType();
    }
    
    public interface IRepositoryQuery<T> : IRepositoryQuery where T: class { }

    public interface ISetDocumentType {
        void SetDocumentType(Type documentType);
    }

    public class RepositoryQuery : OptionsBase, IRepositoryQuery, ISystemFilter, ISetDocumentType {
        protected Type _documentType;

        public RepositoryQuery() {
            _documentType = typeof(object);
        }

        public RepositoryQuery(Type documentType) {
            _documentType = documentType ?? typeof(object);
        }
        
        IRepositoryQuery ISystemFilter.GetQuery() {
            return this;
        }

        public Type GetDocumentType() {
            return _documentType;
        }

        void ISetDocumentType.SetDocumentType(Type documentType) {
            _documentType = documentType;
        }
    }

    public class RepositoryQuery<T> : RepositoryQuery, ISetDocumentType, IRepositoryQuery<T> where T : class {
        public RepositoryQuery() {
            _documentType = typeof(T);
        }

        void ISetDocumentType.SetDocumentType(Type documentType) {}
    }

    public static class RepositoryQueryExtensions {
        public static T DocumentType<T>(this T query, Type documentType) where T : IRepositoryQuery {
            if (query is ISetDocumentType setDocumentType)
                setDocumentType.SetDocumentType(documentType);

            return query;
        }
    }
}
