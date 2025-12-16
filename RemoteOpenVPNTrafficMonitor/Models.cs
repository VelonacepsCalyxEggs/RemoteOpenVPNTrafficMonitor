using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteOpenVPNTrafficMonitor
{
    public class VPNServerConfig
    {
        public required string Name { get; init; }
        public required VPNServerType Type { get; init; }
        public required string Address { get; init; }
        public required int Port { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
        public int PollingIntervalSeconds { get; init; } = 10;
    }

    public enum VPNServerType
    {
        OPENVPN, WIREGUARD
    }
}
