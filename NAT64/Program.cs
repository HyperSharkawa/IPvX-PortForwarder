using NAT64Config;
using NAT64Lib;
using System.Net;
using System.Text.Json;

namespace NAT64;

internal class Program
{
    private static void Main(string[] _)
    {
        if (!File.Exists("config.json"))
        {
            Console.WriteLine("未找到配置文件config.json");
            IPAddress address = Dns.GetHostAddresses("github.com")[0];
            Config config = new()
            {
                PortForwardConfigs =
                [
                    new PortForwardConfig{
                        ListenIpAddress = "127.0.0.1",
                        ListenPort = 12001,
                        ForwardIpAddress = "127.0.0.1",
                        ForwardPort = 12000,
                        },
                    new PortForwardConfig{
                        ListenIpAddress = "[::1]",
                        ListenPort = 443,
                        ForwardIpAddress = address.ToString(),
                        ForwardPort = 443,
                        },

                ]
            };
            string testConfigText = JsonSerializer.Serialize(config, Nat64JsonContext.Default.Config);
            File.WriteAllText("config.json", testConfigText);
            Console.WriteLine("配置项模板已写出 按任意键继续运行 退出请直接关闭窗口");
            Console.ReadKey();
            Console.WriteLine("github.com已被转发到本地IPv6回环地址，请访问：https://[::1]/");
        }
        string configText = File.ReadAllText("config.json");
        Config configJson = JsonSerializer.Deserialize(configText, Nat64JsonContext.Default.Config) ?? throw new Exception("配置文件解析失败");
        PortForwarder.ExpandObjectPool(configJson.ObjectPoolInitialCount);
        try
        {
            PortForwarder.Config[] configs = [.. configJson.PortForwardConfigs.Select(config => config.AsPortForwarderConfig())];
            List<PortForwarder> portForwarders = [.. configs.Select(config => new PortForwarder(config))];
            portForwarders.ForEach(portForwarder => portForwarder.Start());
            Console.WriteLine("按任意键停止监听");
            Console.ReadKey();
            Task[] tasks = [.. portForwarders.Select(portForwarder => Task.Run(portForwarder.Stop))];
            Task.WaitAll(tasks);
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"错误: {e}");
        }
        Console.WriteLine("按任意键退出..");
        Console.ReadKey();
    }
}


internal static class Extensions
{
    public static PortForwarder.Config AsPortForwarderConfig(this PortForwardConfig config)
    {

        bool flag = IPAddress.TryParse(config.ListenIpAddress, out IPAddress? listenIpAddress);
        if (!flag || listenIpAddress is null) throw new FormatException($"无效的IP地址:{config.ListenIpAddress}");
        flag = IPAddress.TryParse(config.ForwardIpAddress, out IPAddress? forwardIpAddress);
        if (!flag || forwardIpAddress is null) throw new FormatException($"无效的IP地址:{config.ForwardIpAddress}");
        return new PortForwarder.Config
        {
            ListenEndPoint = new IPEndPoint(listenIpAddress, config.ListenPort),
            ForwardEndPoint = new IPEndPoint(forwardIpAddress, config.ForwardPort),
            UdpSessionTimeout = config.UdpSessionTimeout,
            TcpConnectHandleThreadCount = config.TcpConnectHandleThreadCount,
            UdpClientCleanInterval = config.UdpClientCleanInterval
        };
    }
}



