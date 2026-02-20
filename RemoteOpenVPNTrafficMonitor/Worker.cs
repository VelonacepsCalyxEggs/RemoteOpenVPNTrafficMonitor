using Npgsql;
using Renci.SshNet;
using System.Collections.Concurrent;

namespace RemoteOpenVPNTrafficMonitor
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly DatabaseManager _dbManager;
        private readonly List<VPNServerConfig> _serverConfigs;

        private readonly ConcurrentDictionary<string, ServerMonitoringState> _serverStates = new();

        public Worker(ILogger<Worker> logger, DatabaseManager dbManager, List<VPNServerConfig> serverConfigs)
        {
            _logger = logger;
            _dbManager = dbManager;
            _serverConfigs = serverConfigs;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Starting VPN Server Monitors...");

                foreach (var config in _serverConfigs)
                {
                    await InitializeServer(config);
                }

                _logger.LogInformation("All VPN server monitors started.");

                // Start monitoring tasks for all servers
                var monitoringTasks = _serverConfigs
                    .Select(config => MonitorServerAsync(config, stoppingToken))
                    .ToList();

                // wait for all monitoring tasks to complete
                await Task.WhenAll(monitoringTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in the main loop");
            }
            finally
            {
                foreach (var state in _serverStates.Values)
                {
                    state.SshClient?.Disconnect();
                    state.SshClient?.Dispose();
                }
            }
        }

        private async Task InitializeServer(VPNServerConfig config)
        {
            try
            {
                _logger.LogInformation("Initializing monitor for server {ServerName}...", config.Name);

                ValidateServerConfig(config);

                // store server state
                var state = new ServerMonitoringState
                {
                    Config = config,
                    PreviousReadings = new ConcurrentDictionary<string, ClientTrafficData>()
                };

                SetupSSHConnection(state);
                await CreateTableForDB(config);

                _serverStates[config.Name] = state;
                _logger.LogInformation("Monitor for server {ServerName} initialized successfully.", config.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize server {ServerName}", config.Name);
            }
        }

        private async Task MonitorServerAsync(VPNServerConfig config, CancellationToken stoppingToken)
        {
            var serverName = config.Name;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_serverStates.TryGetValue(serverName, out var state))
                    {
                        _logger.LogWarning("No state found for server {ServerName}. Reinitializing...", serverName);
                        await InitializeServer(config);
                        await Task.Delay(5000, stoppingToken);
                        continue;
                    }

                    if (state.SshClient == null || !state.SshClient.IsConnected)
                    {
                        _logger.LogWarning("SSH client for {ServerName} not connected. Attempting to reconnect...", serverName);
                        SetupSSHConnection(state);
                    }

                    if (state.SshClient != null && state.SshClient.IsConnected)
                    {
                        var statusOutput = state.Config.Type switch
                        {
                            VPNServerType.OPENVPN => GetOpenVpnStatus(state.SshClient),
                            VPNServerType.WIREGUARD => GetWireGuardStatus(state.SshClient),
                            _ => throw new ArgumentException(nameof(state.Config.Type))
                        };
                        var clientThroughput = state.Config.Type switch
                        {
                            VPNServerType.OPENVPN => ParseStatusAndCalculateThroughputOVPN(statusOutput, state),
                            VPNServerType.WIREGUARD => ParseStatusAndCalculateThroughputWRG(statusOutput, state),
                            _ => throw new ArgumentException(nameof(state.Config.Type))
                        };
                        await InsertDataToDb(clientThroughput, config);
                    }
                    else
                    {
                        _logger.LogError("SSH client for {ServerName} not connected after reconnect attempt.", serverName);
                    }

                    await Task.Delay(config.PollingIntervalSeconds * 1000, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error monitoring server {ServerName}", serverName);
                    await Task.Delay(30000, stoppingToken);
                }
            }
        }
        private string GetOpenVpnStatus(SshClient sshClient)
        {
            using var command = sshClient.RunCommand("cat /var/log/openvpn-status.log");
            return command.Result;
        }

        private string GetWireGuardStatus(SshClient sshClient)
        {
            using var command = sshClient.RunCommand("sudo wg show all dump | tail -n +2");
            return command.Result;
        }

        private Dictionary<string, (string ipAddr, double throughputIn, double throughputOut)> ParseStatusAndCalculateThroughputWRG(string statusOutput, ServerMonitoringState state)
        {
            var results = new Dictionary<string, (string, double, double)>();
            var currentTime = DateTime.UtcNow;

            foreach (string line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                string clientName = parts[1];
                string ipAddr = parts[3].Split(':')[0];
                if (ipAddr == "(none)")
                {
                    continue;
                }
                if (long.TryParse(parts[6], out long bytesIn) &&
                                            long.TryParse(parts[7], out long bytesOut))
                {
                    string clientKey = clientName;

                    if (state.PreviousReadings.TryGetValue(clientKey, out ClientTrafficData previous))
                    {
                        double timeDiff = (currentTime - previous.Timestamp).TotalSeconds;

                        if (timeDiff > 0)
                        {
                            long bytesInDiff = bytesIn;
                            long bytesOutDiff = bytesOut;

                            if (bytesIn < previous.BytesIn || bytesOut < previous.BytesOut)
                            {
                                _logger.LogWarning($"Counter reset detected for {clientName} on server {state.Config.Name}. Using absolute values.");
                            }
                            else
                            {
                                bytesInDiff = bytesIn - previous.BytesIn;
                                bytesOutDiff = bytesOut - previous.BytesOut;
                            }

                            double throughputIn = bytesInDiff / timeDiff;
                            double throughputOut = bytesOutDiff / timeDiff;

                            results[clientName] = (ipAddr, throughputIn, throughputOut);
                        }
                    }
                    state.PreviousReadings[clientKey] = new ClientTrafficData
                    {
                        BytesIn = bytesIn,
                        BytesOut = bytesOut,
                        Timestamp = currentTime,
                        IpAddress = ipAddr
                    };
                }
            }
            CleanupOldEntries(state);

            return results;
        }

        private Dictionary<string, (string ipAddr, double throughputIn, double throughputOut)>
            ParseStatusAndCalculateThroughputOVPN(string statusOutput, ServerMonitoringState state)
        {
            //_logger.LogInformation(statusOutput);
            var results = new Dictionary<string, (string, double, double)>();
            var currentTime = DateTime.UtcNow;

            foreach (string line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("CLIENT_LIST"))
                {
                    string[] parts = line.Split(',');

                    if (parts.Length >= 7)
                    {
                        string clientName = parts[1];
                        string ipAddr = parts[2].Split(':')[0];

                        if (long.TryParse(parts[5], out long bytesIn) &&
                            long.TryParse(parts[6], out long bytesOut))
                        {
                            string clientKey = clientName;

                            if (state.PreviousReadings.TryGetValue(clientKey, out ClientTrafficData previous))
                            {
                                double timeDiff = (currentTime - previous.Timestamp).TotalSeconds;

                                if (timeDiff > 0)
                                {
                                    long bytesInDiff = bytesIn;
                                    long bytesOutDiff = bytesOut;

                                    if (bytesIn < previous.BytesIn || bytesOut < previous.BytesOut)
                                    {
                                        _logger.LogWarning($"Counter reset detected for {clientName} on server {state.Config.Name}. Using absolute values.");
                                    }
                                    else
                                    {
                                        bytesInDiff = bytesIn - previous.BytesIn;
                                        bytesOutDiff = bytesOut - previous.BytesOut;
                                    }

                                    double throughputIn = bytesInDiff / timeDiff;
                                    double throughputOut = bytesOutDiff / timeDiff;

                                    results[clientName] = (ipAddr, throughputIn, throughputOut);
                                }
                            }

                            state.PreviousReadings[clientKey] = new ClientTrafficData
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

            CleanupOldEntries(state);

            return results;
        }

        private void CleanupOldEntries(ServerMonitoringState state)
        {
            var cutoffTime = DateTime.UtcNow.AddHours(-1);
            var oldKeys = state.PreviousReadings
                .Where(kv => kv.Value.Timestamp < cutoffTime)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldKeys)
            {
                state.PreviousReadings.TryRemove(key, out _);
            }
        }

        private async Task InsertDataToDb(
            Dictionary<string, (string ipAddr, double throughputIn, double throughputOut)> results,
            VPNServerConfig config)
        {
            using NpgsqlConnection conn = await _dbManager.GetConnection();
            try
            {
                using var batch = new NpgsqlBatch(conn);

                foreach (var client in results)
                {
                    var cmd = new NpgsqlBatchCommand(
                        $"INSERT INTO {config.Name} (client_name, ip_addr, client_upload, client_download) VALUES ($1, $2, $3, $4)")
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

        private async Task CreateTableForDB(VPNServerConfig config)
        {
            using var conn = await _dbManager.GetConnection();
            await using var createTableCommand = new NpgsqlCommand($@"
                CREATE TABLE IF NOT EXISTS {config.Name} (
                    client_name VARCHAR(255) NOT NULL,
                    ip_addr VARCHAR(15) NOT NULL,
                    client_upload BIGINT NOT NULL CHECK (client_upload >= 0),
                    client_download BIGINT NOT NULL CHECK (client_download >= 0),
                    measured_at TIMESTAMP DEFAULT NOW()
                )", conn);

            await createTableCommand.ExecuteNonQueryAsync();
        }

        private void ValidateServerConfig(VPNServerConfig config)
        {
            if (config.PollingIntervalSeconds < 1)
                throw new ArgumentException($"PollingInterval must be at least 1 second for server {config.Name}");
            if (string.IsNullOrEmpty(config.Address))
                throw new ArgumentException($"Hostname is not set for server {config.Name}!");
            if (config.Port < 1 || config.Port > 65535)
                throw new ArgumentException($"Port must be a value between 1-65535 for server {config.Name}");
            if (string.IsNullOrEmpty(config.Username))
                throw new ArgumentException($"Username must be set for server {config.Name}.");
            if (string.IsNullOrEmpty(config.Password))
                throw new ArgumentException($"Password must be set for server {config.Name}.");
        }

        private void SetupSSHConnection(ServerMonitoringState state)
        {
            var config = state.Config;

            state.SshClient?.Disconnect();
            state.SshClient?.Dispose();

            _logger.LogInformation("Setting up SSH connection to {ServerName} at {Hostname}:{Port}...",
                config.Name, config.Address, config.Port);

            state.SshClient = new SshClient(config.Address, config.Port, config.Username, config.Password);

            try
            {
                state.SshClient.Connect();
                _logger.LogInformation("Connected to {ServerName}. Server version: {Version}",
                    config.Name, state.SshClient.ConnectionInfo.ServerVersion);

                // Test connection
                using SshCommand command = state.SshClient.RunCommand("whoami");
                _logger.LogInformation("Logged into {ServerName} as: {Username}", config.Name, command.Result.Trim());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH Setup error occurred for server {ServerName}", config.Name);
                state.SshClient?.Dispose();
                state.SshClient = null;
            }
        }

        // Helper classes for server state management
        private class ServerMonitoringState
        {
            public VPNServerConfig Config { get; set; } = null!;
            public SshClient? SshClient { get; set; }
            public ConcurrentDictionary<string, ClientTrafficData> PreviousReadings { get; set; } = null!;
        }

        private struct ClientTrafficData
        {
            public long BytesIn;
            public long BytesOut;
            public DateTime Timestamp;
            public string IpAddress;
        }
    }
}