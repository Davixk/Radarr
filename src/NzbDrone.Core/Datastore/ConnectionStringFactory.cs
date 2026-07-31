using System;
using System.Data.SQLite;
using Npgsql;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Datastore
{
    public interface IConnectionStringFactory
    {
        DatabaseConnectionInfo MainDbConnection { get; }
        DatabaseConnectionInfo LogDbConnection { get; }
        string GetDatabasePath(string connectionString);
    }

    public class ConnectionStringFactory : IConnectionStringFactory
    {
        private readonly IConfigFileProvider _configFileProvider;

        public ConnectionStringFactory(IAppFolderInfo appFolderInfo, IConfigFileProvider configFileProvider)
        {
            _configFileProvider = configFileProvider;

            var connectionStringType = GetConnectionStringType();

            switch (connectionStringType)
            {
                case ConnectionStringType.PostgreSqlVars:
                    MainDbConnection = GetPostgresConnectionString(_configFileProvider.PostgresMainDb);
                    LogDbConnection = GetPostgresConnectionString(_configFileProvider.PostgresLogDb);
                    break;
                case ConnectionStringType.PostgreSqlConnectionString:
                    MainDbConnection = GetPostgresConnectionInfoFromConnectionString(_configFileProvider.PostgresMainDbConnectionString);
                    LogDbConnection = GetPostgresConnectionInfoFromConnectionString(_configFileProvider.PostgresLogDbConnectionString);
                    break;
                case ConnectionStringType.Sqlite:
                    MainDbConnection = GetConnectionString(appFolderInfo.GetDatabase());
                    LogDbConnection = GetConnectionString(appFolderInfo.GetLogDatabase());
                    break;
                default:
                    throw new RadarrStartupException("Unable to determine database connection string for type {0}.", connectionStringType.ToString());
            }
        }

        public DatabaseConnectionInfo MainDbConnection { get; private set; }
        public DatabaseConnectionInfo LogDbConnection { get; private set; }

        public string GetDatabasePath(string connectionString)
        {
            var connectionBuilder = new SQLiteConnectionStringBuilder(connectionString);

            return connectionBuilder.DataSource;
        }

        private static DatabaseConnectionInfo GetConnectionString(string dbPath)
        {
            var connectionBuilder = new SQLiteConnectionStringBuilder
            {
                DataSource = dbPath,
                CacheSize = (int)-20000,
                DateTimeKind = DateTimeKind.Utc,
                JournalMode = OsInfo.IsOsx ? SQLiteJournalModeEnum.Truncate : SQLiteJournalModeEnum.Wal,
                Pooling = true,
                Version = 3,
                BusyTimeout = GetBusyTimeout()
            };

            if (OsInfo.IsOsx)
            {
                connectionBuilder.Add("Full FSync", true);
            }

            return new DatabaseConnectionInfo(DatabaseType.SQLite, connectionBuilder.ConnectionString);
        }

        private DatabaseConnectionInfo GetPostgresConnectionString(string dbName)
        {
            var connectionBuilder = new NpgsqlConnectionStringBuilder
            {
                Database = dbName,
                Host = _configFileProvider.PostgresHost,
                Username = _configFileProvider.PostgresUser,
                Password = _configFileProvider.PostgresPassword,
                Port = _configFileProvider.PostgresPort,
                Enlist = false
            };

            return new DatabaseConnectionInfo(DatabaseType.PostgreSQL, connectionBuilder.ConnectionString);
        }

        private DatabaseConnectionInfo GetPostgresConnectionInfoFromConnectionString(string connectionString)
        {
            var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Enlist = false
            };

            return new DatabaseConnectionInfo(DatabaseType.PostgreSQL, connectionBuilder.ConnectionString);
        }

        // fork4: SQLITE_BUSY_TIMEOUT (ms). Default 1000 (matches upstream Radarr and Sonarr v5); clamped
        // >= 100 so it never drops below the old Radarr floor. Tunable higher for a large DB under heavy
        // write load. internal so the value can be asserted directly and echoed by the startup config log.
        internal static int GetBusyTimeout()
        {
            var raw = Environment.GetEnvironmentVariable("SQLITE_BUSY_TIMEOUT");

            if (int.TryParse(raw, out var ms))
            {
                return Math.Max(100, ms);
            }

            return 1000;
        }

        private ConnectionStringType GetConnectionStringType()
        {
            var isMainDBConnectionStringSet = !_configFileProvider.PostgresMainDbConnectionString.IsNullOrWhiteSpace();
            var isLogDBConnectionStringSet = !_configFileProvider.PostgresLogDbConnectionString.IsNullOrWhiteSpace();
            var isHostSet = !_configFileProvider.PostgresHost.IsNullOrWhiteSpace();

            if (!isHostSet && !isMainDBConnectionStringSet && !isLogDBConnectionStringSet)
            {
                // No Postgres settings are set, so nothing to validate
                return ConnectionStringType.Sqlite;
            }

            if (_configFileProvider.LogDbEnabled)
            {
                if (!isMainDBConnectionStringSet && isLogDBConnectionStringSet)
                {
                    throw new RadarrStartupException("Postgres MainDbConnectionString is set but LogDbConnectionString is not. Both must be set or neither.");
                }

                if (isLogDBConnectionStringSet && !isMainDBConnectionStringSet)
                {
                    throw new RadarrStartupException("Postgres LogDbConnectionString is set but MainDbConnectionString is not. Both must be set or neither.");
                }
            }

            if (isMainDBConnectionStringSet && _configFileProvider.PostgresHost.IsNotNullOrWhiteSpace())
            {
                throw new RadarrStartupException($"Either both Postgres connection strings must be set, or the other Postgres settings must be set, but not both.");
            }

            return isMainDBConnectionStringSet ? ConnectionStringType.PostgreSqlConnectionString : ConnectionStringType.PostgreSqlVars;
        }

        private enum ConnectionStringType
        {
            Sqlite,
            PostgreSqlVars,
            PostgreSqlConnectionString
        }
    }
}
