using Microsoft.Extensions.Configuration;
using Npgsql;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteOpenVPNTrafficMonitor
{
    public class DatabaseManager : BackgroundService
    {
        private readonly ILogger<DatabaseManager> _logger;
        private readonly IConfiguration _configuration;
        private NpgsqlDataSource? _dataSource;
        public DatabaseManager(ILogger<DatabaseManager> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await SetupDatabaseConnection();
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
            _dataSource?.Dispose();
        }
        public async Task<NpgsqlConnection> GetConnection()
        {
            if (_dataSource == null)
            {
                await SetupDatabaseConnection();
            }
            return await _dataSource!.OpenConnectionAsync();
        }
        private async Task SetupDatabaseConnection()
        {
            var connectionString = _configuration.GetValue<string>("dbConnectionString");
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentException("Database connection string is not set!");

            _dataSource?.Dispose();
            _dataSource = NpgsqlDataSource.Create(connectionString);

            // Check if db exists
            await using var checkDbCommand = _dataSource.CreateCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'vpntraffic')");
            bool exists = (bool)(await checkDbCommand.ExecuteScalarAsync() ?? false);

            if (!exists)
            {
                await using var createDbCommand = _dataSource.CreateCommand("CREATE DATABASE vpntraffic");
                await createDbCommand.ExecuteNonQueryAsync();
            }

            // connect to the vpntraffic db
            var dbBuilder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = "vpntraffic"
            };
            _dataSource = NpgsqlDataSource.Create(dbBuilder.ConnectionString);
        }
    }
}
