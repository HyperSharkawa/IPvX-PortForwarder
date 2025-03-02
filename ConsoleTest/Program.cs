using System.Net;
using System.Net.Sockets;
// ReSharper disable FunctionNeverReturns

namespace ConsoleTest;

internal class Program
{
    private static readonly Socket ListenSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private static readonly Socket ForwardSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private static readonly IPEndPoint ListenEndPoint = new(IPAddress.Loopback, 12000);
    private static readonly IPEndPoint ForwardEndPoint = new(IPAddress.Loopback, 11001);
    private static readonly IPEndPoint TargetEndPoint = new(IPAddress.Loopback, 11000);
    private static EndPoint _clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
    private const int SioUdpConnreset = -1744830452; // SIO_UDP_CONNRESET的常量值
    private static readonly ObjectPool<byte[]> BufferPool = new(() => new byte[81920]);
    private static readonly SemaphoreQueue<(byte[], int)> ListenQueue = new();
    private static readonly SemaphoreQueue<(byte[], int)> ForwardQueue = new();

    private static void Main(string[] _)
    {
        ListenSocket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null); // 忽略UDP连接重置错误
        ForwardSocket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null); // 忽略UDP连接重置错误
        try
        {
            Task[] tasks = [
                Task.Run(ListenUdp),
                Task.Run(ForwardUdp),
                Task.Run(ListenQueueHandler),
                Task.Run(ForwardQueueHandler)
            ];
            Task.WaitAll(tasks);
        }
        finally
        {
            ListenSocket.Close();
            ForwardSocket.Close();
        }
        Console.WriteLine("按任意键退出");
        Console.ReadKey();
    }

    private static void ListenUdp()
    {
        try
        {
            Span<byte> buffer = new byte[81920];

            // 监听 12000 端口，转发到 11000 端口
            ListenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 12000));
            int length = ListenSocket.ReceiveFrom(buffer, ref _clientEndPoint);
            //int length = await ListenSocket.ReceiveFromAsync(buffer, SocketFlags.None, _clientEndPoint);
            Console.WriteLine($"ClientEndPoint: {_clientEndPoint}");
            ForwardSocket.SendTo(buffer[..length], TargetEndPoint);
            while (true)
            {
                length = ListenSocket.ReceiveFrom(buffer, ref _clientEndPoint);
                byte[] bufferCopy = BufferPool.Get();
                buffer[..length].CopyTo(bufferCopy);
                ListenQueue.Enqueue((bufferCopy, length));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private static void ListenQueueHandler()
    {
        while (true)
        {
            (byte[] buffer, int length) = ListenQueue.Dequeue();
            ForwardSocket.SendTo(buffer.AsSpan()[..length], TargetEndPoint);
            BufferPool.Return(buffer);
        }
    }

    private static void ForwardQueueHandler()
    {
        while (true)
        {
            (byte[] buffer, int length) = ForwardQueue.Dequeue();
            ListenSocket.SendTo(buffer.AsSpan()[..length], _clientEndPoint);
            BufferPool.Return(buffer);
        }
    }

    private static void ForwardUdp()
    {
        try
        {
            // 监听 11001 端口，转发到客户端的端口
            ForwardSocket.Bind(new IPEndPoint(IPAddress.Loopback, 11001));
            Span<byte> buffer = new byte[81920];
            EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                int length = ForwardSocket.ReceiveFrom(buffer, ref remoteEndPoint);
                byte[] bufferCopy = BufferPool.Get();
                buffer[..length].CopyTo(bufferCopy);
                ForwardQueue.Enqueue((bufferCopy, length));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
}
