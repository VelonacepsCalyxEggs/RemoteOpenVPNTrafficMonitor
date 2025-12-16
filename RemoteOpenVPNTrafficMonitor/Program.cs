using RemoteOpenVPNTrafficMonitor;

var builder = Host.CreateApplicationBuilder(args);
Console.WriteLine("Starting Remote OpenVPN Traffic Monitor...");
builder.Services.AddSingleton<DatabaseManager>();
var serverConfigs = builder.Configuration.GetSection("vpnServers").Get<List<VPNServerConfig>>() ?? [];

if (serverConfigs.Any())
{
    Console.WriteLine($"Found {serverConfigs.Count} VPN server configurations:");
    foreach (var config in serverConfigs)
    {
        Console.WriteLine($"  - {config.Name} at {config.Address}:{config.Port} of type: {config.Type.ToString()}");
    }

    builder.Services.AddSingleton(serverConfigs);
    builder.Services.AddHostedService<Worker>();
}
else
{
    Console.WriteLine("No VPN server configurations found.");
}
var host = builder.Build();
host.Run();
