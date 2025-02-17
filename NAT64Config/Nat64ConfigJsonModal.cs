using System.Text.Json.Serialization;

namespace NAT64Config;


public class Config
{
    public required PortForwardConfig[] PortForwardConfigs { get; set; }
}


public class PortForwardConfig
{
    public required string ListenIpAddress { get; set; }
    public required int ListenPort { get; set; }
    public required string ForwardIpAddress { get; set; }
    public required int ForwardPort { get; set; }
    public int UdpSessionTimeout { get; set; } = 120;
    public int TcpConnectHandleThreadCount { get; set; } = 2;
    public int UdpClientCleanInterval { get; init; } = 60;
}



