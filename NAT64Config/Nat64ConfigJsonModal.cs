namespace NAT64Config;


public class Config
{
    public int ObjectPoolInitialCount { get; init; } = 10;
    public required PortForwardConfig[] PortForwardConfigs { get; init; }
}


public class PortForwardConfig
{
    public required string ListenIpAddress { get; init; }
    public required int ListenPort { get; init; }
    public required string ForwardIpAddress { get; init; }
    public required int ForwardPort { get; init; }
    public int UdpSessionTimeout { get; init; } = 120;
    public int TcpConnectHandleThreadCount { get; init; } = 2;
    public int UdpClientCleanInterval { get; init; } = 60;
}



