using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        public string? XUIBaseUrl {  get; init; }
        public int PollingIntervalSeconds { get; init; } = 10;
    }

    public enum VPNServerType
    {
        OPENVPN, WIREGUARD, XUI
    }

    public class XuiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("msg")]
        public string Msg { get; set; }

        [JsonPropertyName("obj")]
        public List<Inbound> Obj { get; set; }
    }

    public class Inbound
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("up")]
        public long Up { get; set; }

        [JsonPropertyName("down")]
        public long Down { get; set; }

        [JsonPropertyName("total")]
        public long Total { get; set; }

        [JsonPropertyName("allTime")]
        public long AllTime { get; set; }

        [JsonPropertyName("remark")]
        public string Remark { get; set; }

        [JsonPropertyName("enable")]
        public bool Enable { get; set; }

        [JsonPropertyName("expiryTime")]
        public long ExpiryTime { get; set; }

        [JsonPropertyName("trafficReset")]
        public string TrafficReset { get; set; }

        [JsonPropertyName("lastTrafficResetTime")]
        public long LastTrafficResetTime { get; set; }

        [JsonPropertyName("clientStats")]
        public List<ClientStat> ClientStats { get; set; }

        [JsonPropertyName("listen")]
        public string Listen { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; }

        [JsonPropertyName("settings")]
        public string Settings { get; set; }

        [JsonPropertyName("streamSettings")]
        public string StreamSettings { get; set; }

        [JsonPropertyName("sniffing")]
        public string Sniffing { get; set; }

        [JsonPropertyName("tag")]
        public string Tag { get; set; }
    }

    public class ClientStat
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("inboundId")]
        public int InboundId { get; set; }

        [JsonPropertyName("enable")]
        public bool Enable { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; }

        [JsonPropertyName("subId")]
        public string SubId { get; set; }

        [JsonPropertyName("up")]
        public long Up { get; set; }

        [JsonPropertyName("down")]
        public long Down { get; set; }

        [JsonPropertyName("allTime")]
        public long AllTime { get; set; }

        [JsonPropertyName("expiryTime")]
        public long ExpiryTime { get; set; }

        [JsonPropertyName("total")]
        public long Total { get; set; }

        [JsonPropertyName("reset")]
        public int Reset { get; set; }

        [JsonPropertyName("lastOnline")]
        public long LastOnline { get; set; }
    }

}
