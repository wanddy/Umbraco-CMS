using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Common;
using System.Linq;
using System.Threading;
using NPoco;
using NPoco.FluentMappings;
using Umbraco.Core.Configuration;
using Umbraco.Core.Exceptions;
using Umbraco.Core.Logging;
using Umbraco.Core.Persistence.FaultHandling;
using Umbraco.Core.Persistence.Mappers;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Persistence
{
    /// <summary>
    /// Default implementation of <see cref="IUmbracoDatabaseFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>This factory implementation creates and manages an "ambient" database connection. When running
    /// within an Http context, "ambient" means "associated with that context". Otherwise, it means "static to
    /// the current thread". In this latter case, note that the database connection object is not thread safe.</para>
    /// <para>It wraps an NPoco UmbracoDatabaseFactory which is initializes with a proper IPocoDataFactory to ensure
    /// that NPoco's plumbing is cached appropriately for the whole application.</para>
    /// </remarks>
    internal class UmbracoDatabaseFactory : DisposableObject, IUmbracoDatabaseFactory
    {
        private readonly IDatabaseScopeAccessor _databaseScopeAccessor;
        private readonly ISqlSyntaxProvider[] _sqlSyntaxProviders;
        private readonly IMapperCollection _mappers;
        private readonly ILogger _logger;

        private DatabaseFactory _npocoDatabaseFactory;
        private IPocoDataFactory _pocoDataFactory;
        private string _connectionString;
        private string _providerName;
        private DbProviderFactory _dbProviderFactory;
        private DatabaseType _databaseType;
        private ISqlSyntaxProvider _sqlSyntax;
        private SqlContext _sqlContext;
        private RetryPolicy _connectionRetryPolicy;
        private RetryPolicy _commandRetryPolicy;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        #region Ctor

        /// <summary>
        /// Initializes a new instance of the <see cref="UmbracoDatabaseFactory"/>.
        /// </summary>
        /// <remarks>Used by LightInject.</remarks>
        public UmbracoDatabaseFactory(IEnumerable<ISqlSyntaxProvider> sqlSyntaxProviders, ILogger logger, IDatabaseScopeAccessor databaseScopeAccessor, IMapperCollection mappers)
            : this(GlobalSettings.UmbracoConnectionName, sqlSyntaxProviders, logger, databaseScopeAccessor, mappers)
        {
            if (Configured == false)
                DatabaseBuilder.GiveLegacyAChance(this, logger);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UmbracoDatabaseFactory"/>.
        /// </summary>
        /// <remarks>Used by the other ctor and in tests.</remarks>
        public UmbracoDatabaseFactory(string connectionStringName, IEnumerable<ISqlSyntaxProvider> sqlSyntaxProviders, ILogger logger, IDatabaseScopeAccessor databaseScopeAccessor, IMapperCollection mappers)
        {
            if (sqlSyntaxProviders == null) throw new ArgumentNullException(nameof(sqlSyntaxProviders));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (databaseScopeAccessor == null) throw new ArgumentNullException(nameof(databaseScopeAccessor));
            if (string.IsNullOrWhiteSpace(connectionStringName)) throw new ArgumentNullOrEmptyException(nameof(connectionStringName));
            if (mappers == null) throw new ArgumentNullException(nameof(mappers));

            _mappers = mappers;
            _sqlSyntaxProviders = sqlSyntaxProviders.ToArray();
            _logger = logger;
            _databaseScopeAccessor = databaseScopeAccessor;

            var settings = ConfigurationManager.ConnectionStrings[connectionStringName];
            if (settings == null)
                return; // not configured

            // could as well be <add name="umbracoDbDSN" connectionString="" providerName="" />
            // so need to test the values too
            var connectionString = settings.ConnectionString;
            var providerName = settings.ProviderName;
            if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(providerName))
            {
                logger.Debug<UmbracoDatabaseFactory>("Missing connection string or provider name, defer configuration.");
                return; // not configured
            }

            Configure(settings.ConnectionString, settings.ProviderName);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UmbracoDatabaseFactory"/>.
        /// </summary>
        /// <remarks>Used in tests.</remarks>
        public UmbracoDatabaseFactory(string connectionString, string providerName, IEnumerable<ISqlSyntaxProvider> sqlSyntaxProviders, ILogger logger, IDatabaseScopeAccessor databaseScopeAccessor, IMapperCollection mappers)
        {
            if (sqlSyntaxProviders == null) throw new ArgumentNullException(nameof(sqlSyntaxProviders));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (databaseScopeAccessor == null) throw new ArgumentNullException(nameof(databaseScopeAccessor));
            if (mappers == null) throw new ArgumentNullException(nameof(mappers));

            _mappers = mappers;
            _sqlSyntaxProviders = sqlSyntaxProviders.ToArray();
            _logger = logger;
            _databaseScopeAccessor = databaseScopeAccessor;

            if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(providerName))
            {
                logger.Debug<UmbracoDatabaseFactory>("Missing connection string or provider name, defer configuration.");
                return; // not configured
            }

            Configure(connectionString, providerName);
        }

        #endregion

        /// <inheritdoc />
        public bool Configured { get; private set; }

        /// <inheritdoc />
        public bool CanConnect => Configured && DbConnectionExtensions.IsConnectionAvailable(_connectionString, _providerName);

        #region IDatabaseContext

        /// <inheritdoc />
        public ISqlSyntaxProvider SqlSyntax
        {
            get
            {
                EnsureConfigured();
                return _sqlSyntax;
            }
        }

        public IQuery<T> Query<T>()
        {
            EnsureConfigured();
            return new Query<T>(_sqlSyntax, _mappers);
        }

        /// <inheritdoc />
        public Sql<SqlContext> Sql()
        {
            EnsureConfigured();
            return NPoco.Sql.BuilderFor(_sqlContext);
        }

        /// <inheritdoc />
        public Sql<SqlContext> Sql(string sql, params object[] args)
        {
            return Sql().Append(sql, args);
        }

        #endregion

        /// <inheritdoc />
        public void Configure(string connectionString, string providerName)
        {
            using (new WriteLock(_lock))
            {
                _logger.Debug<UmbracoDatabaseFactory>("Configuring.");

                if (Configured) throw new InvalidOperationException("Already configured.");

                if (connectionString.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(connectionString));
                if (providerName.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(providerName));

                _connectionString = connectionString;
                _providerName = providerName;

                _connectionRetryPolicy = RetryPolicyFactory.GetDefaultSqlConnectionRetryPolicyByConnectionString(_connectionString);
                _commandRetryPolicy = RetryPolicyFactory.GetDefaultSqlCommandRetryPolicyByConnectionString(_connectionString);

                _dbProviderFactory = DbProviderFactories.GetFactory(_providerName);
                if (_dbProviderFactory == null)
                    throw new Exception($"Can't find a provider factory for provider name \"{_providerName}\".");
                _databaseType = DatabaseType.Resolve(_dbProviderFactory.GetType().Name, _providerName);
                if (_databaseType == null)
                    throw new Exception($"Can't find an NPoco database type for provider name \"{_providerName}\".");

                _sqlSyntax = GetSqlSyntaxProvider(_providerName);
                if (_sqlSyntax == null)
                    throw new Exception($"Can't find a sql syntax provider for provider name \"{_providerName}\".");

                // ensure we have only 1 set of mappers, and 1 PocoDataFactory, for all database
                // so that everything NPoco is properly cached for the lifetime of the application
                var mappers = new NPoco.MapperCollection { new PocoMapper() };
                var factory = new FluentPocoDataFactory((type, iPocoDataFactory) => new PocoDataBuilder(type, mappers).Init());
                _pocoDataFactory = factory;
                var config = new FluentConfig(xmappers => factory);

                // create the database factory
                _npocoDatabaseFactory = NPoco.DatabaseFactory.Config(x => x
                    .UsingDatabase(CreateDatabaseInstance) // creating UmbracoDatabase instances
                    .WithFluentConfig(config)); // with proper configuration

                if (_npocoDatabaseFactory == null) throw new NullReferenceException("The call to UmbracoDatabaseFactory.Config yielded a null UmbracoDatabaseFactory instance.");

                // these are created here because it is the UmbracoDatabaseFactory that determines
                // the sql syntax, poco data factory, and database type - so it "owns" the context
                // and the query factory
                _sqlContext = new SqlContext(_sqlSyntax, _pocoDataFactory, _databaseType, _mappers);               

                _logger.Debug<UmbracoDatabaseFactory>("Configured.");
                Configured = true;
            }
        }

        /// <inheritdoc />
        public IUmbracoDatabase Database => GetDatabase();

        /// <inheritdoc />
		public IUmbracoDatabase GetDatabase()
        {
            EnsureConfigured();

            var scope = _databaseScopeAccessor.Scope;
            if (scope == null) throw new InvalidOperationException("Out of scope.");
            return scope.Database;
        }

        /// <inheritdoc />
        public IUmbracoDatabase CreateDatabase()
        {
            return (IUmbracoDatabase) _npocoDatabaseFactory.GetDatabase();
        }

        /// <inheritdoc />
        public IDatabaseScope CreateScope(IUmbracoDatabase database = null)
        {
            return new DatabaseScope(_databaseScopeAccessor, this, database);
        }

        // gets the sql syntax provider that corresponds, from attribute
        private ISqlSyntaxProvider GetSqlSyntaxProvider(string providerName)
        {
            var name = providerName.ToLowerInvariant();
            var provider = _sqlSyntaxProviders.FirstOrDefault(x =>
                x.GetType()
                    .FirstAttribute<SqlSyntaxProviderAttribute>()
                    .ProviderName.ToLowerInvariant()
                    .Equals(name));
            if (provider != null) return provider;
            throw new InvalidOperationException($"Unknown provider name \"{providerName}\"");

            // previously we'd try to return SqlServerSyntaxProvider by default but this is bad
            //provider = _syntaxProviders.FirstOrDefault(x => x.GetType() == typeof(SqlServerSyntaxProvider));
        }

        // ensures that the database is configured, else throws
        private void EnsureConfigured()
        {
            using (new ReadLock(_lock))
            {
                if (Configured == false)
                    throw new InvalidOperationException("Not configured.");
            }
        }

        // method used by NPoco's UmbracoDatabaseFactory to actually create the database instance
        private UmbracoDatabase CreateDatabaseInstance()
        {
            return new UmbracoDatabase(_connectionString, _sqlContext, _dbProviderFactory, _logger, _connectionRetryPolicy, _commandRetryPolicy);
        }

        protected override void DisposeResources()
        {
            // this is weird, because hybrid accessors store different databases per
            // thread, so we don't really know what we are disposing here...
            // besides, we don't really want to dispose the factory, which is a singleton...

            // fixme - does not make any sense!
            //var db = _umbracoDatabaseAccessor.UmbracoDatabase;
            //_umbracoDatabaseAccessor.UmbracoDatabase = null;
            //db?.Dispose();
            Configured = false;
        }

        // during tests, the thread static var can leak between tests
        // this method provides a way to force-reset the variable
	    internal void ResetForTests()
	    {
            // fixme - does not make any sense!
            //var db = _umbracoDatabaseAccessor.UmbracoDatabase;
            //_umbracoDatabaseAccessor.UmbracoDatabase = null;
            //db?.Dispose();
	        _databaseScopeAccessor.Scope = null;
	    }
    }
}