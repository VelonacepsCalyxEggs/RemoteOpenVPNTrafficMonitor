using Microsoft.Extensions.Logging;
using Npgsql;
using Renci.SshNet;
using System.Collections.Concurrent;

namespace RemoteOpenVPNTrafficMonitor
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private int _pollingInterval;
        private SshClient? _sshClient;
        private NpgsqlDataSource? _dataSource;

        // Store previous readings for each client
        private ConcurrentDictionary<string, ClientTrafficData> _previousReadings = new();

        private struct ClientTrafficData
        {
            public long BytesIn;
            public long BytesOut;
            public DateTime Timestamp;
            public string IpAddress;
        }

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                Startup();
                await SetupDatabaseConnection();

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (_sshClient == null || !_sshClient.IsConnected)
                    {
                        _logger.LogWarning("SSH client not connected. Attempting to reconnect...");
                        Startup();
                    }

                    if (_sshClient != null && _sshClient.IsConnected)
                    {
                        try
                        {
                            var statusOutput = GetOpenVpnStatus();
                            var clientThroughput = ParseStatusAndCalculateThroughput(statusOutput);

                            foreach (var client in clientThroughput)
                            {
                                _logger.LogInformation(
                                    $"Client: {client.Key}, " +
                                    $"In: {client.Value.throughputIn} byte/s, " +
                                    $"Out: {client.Value.throughputOut} byte/s");
                            }

                            await InsertDataToDb(clientThroughput);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error getting OpenVPN status: {ex.Message}");
                        }
                    }
                    else
                    {
                        _logger.LogError("SSH client not connected after reconnect attempt.");
                    }

                    await Task.Delay(_pollingInterval * 1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in the main loop");
            }
            finally
            {
                _sshClient?.Disconnect();
            }
        }

        private string GetOpenVpnStatus()
        {
            try
            {
                SshCommand command = _sshClient.RunCommand("echo \"status 3\" | nc 127.0.0.1 7505");
                return command.Result;
            }
            catch
            {
                SshCommand command = _sshClient.RunCommand("grep '^CLIENT_LIST' /var/log/openvpn-status.log");
                return command.Result;
            }
        }

        private Dictionary<string, (string ipAddr, double throughputIn, double throughputOut)>
            ParseStatusAndCalculateThroughput(string statusOutput)
        {
            var results = new Dictionary<string, (string, double, double)>();
            var currentTime = DateTime.UtcNow;

            foreach (string line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("CLIENT_LIST"))
                {
                    string[] parts = line.Split('\t'); // add custom delimiter support later

                    if (parts.Length >= 7)
                    {
                        string clientName = parts[1];
                        string ipAddr = parts[2].Split(':')[0]; // Extract IP without port

                        if (long.TryParse(parts[5], out long bytesIn) &&
                            long.TryParse(parts[6], out long bytesOut))
                        {
                            string clientKey = clientName;

                            if (_previousReadings.TryGetValue(clientKey, out ClientTrafficData previous))
                            {
                                double timeDiff = (currentTime - previous.Timestamp).TotalSeconds;

                                if (timeDiff > 0)
                                {
                                    // Handle counter resets (negative values)
                                    long bytesInDiff = bytesIn;
                                    long bytesOutDiff = bytesOut;

                                    if (bytesIn < previous.BytesIn || bytesOut < previous.BytesOut)
                                    {
                                        _logger.LogWarning($"Counter reset detected for {clientName}. Using absolute values.");
                                        // For counter reset, use the current values as the difference
                                    }
                                    else
                                    {
                                        bytesInDiff = bytesIn - previous.BytesIn;
                                        bytesOutDiff = bytesOut - previous.BytesOut;
                                    }

                                    // Calculate throughput in byte/s
                                    double throughputIn = bytesInDiff / timeDiff;
                                    double throughputOut = bytesOutDiff / timeDiff;

                                    results[clientName] = (ipAddr, throughputIn, throughputOut);
                                }
                            }

                            _previousReadings[clientKey] = new ClientTrafficData
                            {
                                BytesIn = bytesIn,
                                BytesOut = bytesOut,
                                Timestamp = currentTime,
                                IpAddress = ipAddr
                            };
                        }
                    }
                }
            }

            // Clean up old entries to prevent memory leaks (scary)
            CleanupOldEntries();

            return results;
        }

        private void CleanupOldEntries()
        {
            var cutoffTime = DateTime.UtcNow.AddHours(-1);
            var oldKeys = _previousReadings
                .Where(kv => kv.Value.Timestamp < cutoffTime)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldKeys)
            {
                _previousReadings.TryRemove(key, out _);
            }
        }

        private async Task InsertDataToDb(Dictionary<string, (string ipAddr, double throughputIn, double throughputOut)> results)
        {
            if (_dataSource == null)
                throw new Exception("Database source is null.");

            await using var conn = await _dataSource.OpenConnectionAsync();

            try
            {
                using var batch = new NpgsqlBatch(conn);

                foreach (var client in results)
                {
                    var cmd = new NpgsqlBatchCommand(
                        "INSERT INTO traffic (client_name, ip_addr, bytes_in, bytes_out) VALUES ($1, $2, $3, $4)")
                    {
                        Parameters =
                        {
                            new() { Value = client.Key },
                            new() { Value = client.Value.ipAddr },
                            new() { Value = Math.Max(0, client.Value.throughputIn) },
                            new() { Value = Math.Max(0, client.Value.throughputOut) }
                        }
                    };
                    batch.BatchCommands.Add(cmd);
                }

                await batch.ExecuteNonQueryAsync();
            }
            finally
            {
                await conn.CloseAsync();
            }
        }

        private void Startup()
        {
            _pollingInterval = _configuration.GetValue<int>("PollingInterval");
            string hostname = _configuration.GetValue<string>("vpnServerHostname");
            int port = _configuration.GetValue<int>("vpnServerPort");
            string username = _configuration.GetValue<string>("sshUsername");
            string password = _configuration.GetValue<string>("sshPassword");

            if (_pollingInterval < 1)
                throw new ArgumentException("PollingInterval must be at least 1 second");
            if (string.IsNullOrEmpty(hostname))
                throw new ArgumentException("Hostname is not set!");
            if (port < 1 || port > 65535)
                throw new ArgumentException("Port must be a value between 1-65535");
            if (string.IsNullOrEmpty(username))
                throw new ArgumentException("Username must be set.");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password must be set.");

            SetupSSHConnection(hostname, port, username, password);
        }

        private void SetupSSHConnection(string hostname, int port, string username, string password)
        {
            _sshClient = new SshClient(hostname, port, username, password);

            try
            {
                _sshClient.Connect();
                _logger.LogInformation($"Connected to {hostname}. Server version: {_sshClient.ConnectionInfo.ServerVersion}");

                // Test conn
                SshCommand command = _sshClient.RunCommand("whoami");
                _logger.LogInformation($"Logged in as: {command.Result}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"SSH Setup error occurred: {ex.GetType().Name} {ex.Message}");
            }
        }

        private async Task SetupDatabaseConnection()
        {
            var connectionString = _configuration.GetValue<string>("connectionString");
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentException("Database connection string is not set!");

            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            _dataSource = dataSource;

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

            await using var createTableCommand = _dataSource.CreateCommand(@"
                CREATE TABLE IF NOT EXISTS traffic (
                    client_name VARCHAR(255) NOT NULL,
                    ip_addr VARCHAR(15) NOT NULL,
                    bytes_in BIGINT NOT NULL CHECK (bytes_in >= 0),
                    bytes_out BIGINT NOT NULL CHECK (bytes_out >= 0),
                    measured_at TIMESTAMP DEFAULT NOW()
                )");
            await createTableCommand.ExecuteNonQueryAsync();
        }
    }
}