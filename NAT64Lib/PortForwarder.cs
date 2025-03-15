using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NAT64Lib;

public class PortForwarder : IDisposable
{
    private readonly TimeSpan _udpSessionTimeout;
    private readonly TimeSpan _udpClientCleanInterval;
    private readonly int _tcpConnectHandleThreadCount;
    private readonly IPEndPoint _listenEndPoint;
    private readonly IPEndPoint _forwardEndPoint;
    private readonly IPAddress _localBindIpAddress;
    private readonly TcpListener _listenTcpListener;
    private CancellationTokenSource? _cancellationTokenSource;
    /// <summary>
    /// 接收UDP数据包的队列 存放的是从监听地址接收到的数据包和客户端的终结点,需要转发到目标地址
    /// </summary>
    private readonly ChannelBasedQueue<(byte[] buffer, int length, EndPoint remoteEndPoint)> _receiveUdpQueue = new();
    /// <summary>
    /// 发送UDP数据包的队列 存放的是从目标地址接收到的数据包和客户端的终结点
    /// </summary>
    private readonly ChannelBasedQueue<(byte[] buffer, int length, EndPoint clientEndPoint)> _sendUdpQueue = new();
    /// <summary>
    /// TCP连接队列
    /// </summary>
    private readonly ChannelBasedQueue<TcpClient> _tcpConnectQueue = new();
    private Task[] _workerTasks = [];
    /// <summary>
    /// 忽略UDP连接重置错误 SIO_UDP_CONNRESET的常量值
    /// </summary>
    private const int SioUdpConnreset = -1744830452;
    /// <summary>
    /// UDP缓冲区的最大大小 64KB
    /// </summary>
    private const int MaxUdpBufferSize = 0x10000;
    /// <summary>
    /// UDP的Socket对象 该Socket用于接受来自监听地址的UDP数据包,以及将目标地址的响应数据包发送到客户端
    /// </summary>
    private readonly Socket _udpSocket;
    /// <summary>
    /// UDP缓冲区对象池
    /// </summary>
    private static readonly ObjectPool<byte[]> UdpSocketBufferPool = new(() => new byte[MaxUdpBufferSize]);
    private static readonly ObjectPool<EndPoint> EndPointPool = new(() => new IPEndPoint(IPAddress.Any, 0));
    private static readonly ObjectPool<EndPoint> EndPointPoolV6 = new(() => new IPEndPoint(IPAddress.IPv6Any, 0));
    private readonly ObjectPool<EndPoint> _listenEndPointPool;
    private readonly ObjectPool<EndPoint> _forwardEndPointPool;
    /// <summary>
    /// UDP会话映射表 用于存放客户端的会话信息 键为客户端的终结点 值为该客户的会话信息
    /// </summary>
    private readonly ConcurrentDictionary<EndPoint, UdpSessionInfo> _udpSessionMapping = [];

    public PortForwarder(Config config)
    {
        this._tcpConnectHandleThreadCount = config.TcpConnectHandleThreadCount;
        this._udpClientCleanInterval = TimeSpan.FromSeconds(config.UdpClientCleanInterval);
        this._udpSessionTimeout = TimeSpan.FromSeconds(config.UdpSessionTimeout);
        this._listenEndPoint = config.ListenEndPoint;
        this._forwardEndPoint = config.ForwardEndPoint;
        // ReSharper disable once SwitchExpressionHandlesSomeKnownEnumValuesWithExceptionInDefault
        this._localBindIpAddress = this._forwardEndPoint.AddressFamily switch
        {
            AddressFamily.InterNetwork => IPAddress.Any,
            AddressFamily.InterNetworkV6 => IPAddress.IPv6Any,
            _ => throw new NotSupportedException("不支持的地址族")
        };
        this._listenEndPointPool = this._listenEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? EndPointPoolV6 : EndPointPool;
        this._forwardEndPointPool = this._forwardEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? EndPointPoolV6 : EndPointPool;
        this._listenTcpListener = new TcpListener(this._listenEndPoint);
        this._udpSocket = new Socket(this._listenEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp); // 创建UDP Socket对象
        this._udpSocket.Bind(this._listenEndPoint); // 绑定到监听地址
        this._udpSocket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null); // 忽略UDP连接重置错误
        //this._listenUdpClient = new UdpClientSocket(config.ListenEndPoint); // 绑定监听端口，但不连接到目标地址，因为需要接受和发送来自任意地址的UDP数据包
        //this._listenUdpClient.Client.IOControl(SioUdpConnreset, [0, 0, 0, 0], null); // 忽略UDP连接重置错误

    }

    ~PortForwarder() => this.Dispose();

    public static void ExpandObjectPool(int targetCount)
    {
        Console.WriteLine($"扩展对象池大小到{targetCount}");
        UdpSocketBufferPool.ExpandPool(targetCount);
        EndPointPool.ExpandPool(targetCount);
        EndPointPoolV6.ExpandPool(targetCount);
    }
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this._cancellationTokenSource?.Cancel();
        Task.WaitAll(this._workerTasks);
        this._cancellationTokenSource?.Dispose();
        this._listenTcpListener.Dispose();
        //this._listenUdpClient.Dispose();
        this._udpSocket.Dispose();
    }

    public void Start()
    {
        this._cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = this._cancellationTokenSource.Token;
        this._listenTcpListener.Start();
        this._workerTasks = [
            Task.Run(() => ListenTcp(cancellationToken), cancellationToken),
            Task.Run(() => ListenUdp(cancellationToken), cancellationToken),
            Task.Run(() => ReceiveUdpQueueHandler(cancellationToken), cancellationToken),
            Task.Run(() => SendUdpQueueHandler(cancellationToken), cancellationToken),
            Task.Run(() => UdpSessionCleaner(cancellationToken), cancellationToken),
            ..Enumerable.Range(0, this._tcpConnectHandleThreadCount)
            .Select(_ => Task.Run(() => TcpConnectQueueHandler(cancellationToken), cancellationToken))
           ];
    }

    public void Stop()
    {
        this._cancellationTokenSource?.Cancel();
        this._listenTcpListener.Stop();
        Task.WaitAll(this._workerTasks);
        Console.WriteLine("端口转发停止");
    }

    /// <summary>
    /// 监听TCP连接 并将接收到的TcpClient加入到队列
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task ListenTcp(CancellationToken cancellationToken)
    {
        Console.WriteLine($"开始在 {this._listenEndPoint} 上监听TCP连接，并转发到{this._forwardEndPoint}");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient listenTcpClient = await this._listenTcpListener.AcceptTcpClientAsync(cancellationToken); // 接收连接
                // 将接收到的TcpClient加入到队列
                this._tcpConnectQueue.Enqueue(listenTcpClient);
            }
        }
        catch (OperationCanceledException)
        {
            // TCP监听线程结束
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TCP监听发生错误：{ex}");
        }
        finally
        {
            Console.WriteLine("TCP监听结束");
        }
    }

    /// <summary>
    /// 处理TCP连接队列中的TcpClient
    /// </summary>
    /// <param name="cancellationTokenSource"></param>
    /// <returns></returns>
    private async Task TcpConnectQueueHandler(CancellationToken cancellationTokenSource)
    {
        try
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                TcpClient tcpClient = await this._tcpConnectQueue.DequeueAsync(cancellationTokenSource);
                // 开启任务双向转发数据
                _ = ForwardTcp(tcpClient, cancellationTokenSource);
            }
        }
        catch (OperationCanceledException)
        {
            //TCP连接处理线程结束
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理TCP连接时发生错误：{ex}");
        }
        Console.WriteLine($"TCP连接处理线程结束");
    }

    /// <summary>
    /// 转发TCP数据
    /// </summary>
    /// <param name="listenTcpClient"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task ForwardTcp(TcpClient listenTcpClient, CancellationToken cancellationToken)
    {
        EndPoint? connectEndPoint = listenTcpClient.Client.RemoteEndPoint;
        if (connectEndPoint is not null) Console.WriteLine($"接收来自 {connectEndPoint} 的TCP连接");
        try
        {
            using TcpClient forwardTcpClient = new(this._forwardEndPoint.AddressFamily); // 创建到目标地址的连接
            await forwardTcpClient.ConnectAsync(this._forwardEndPoint, cancellationToken); // 连接到目标地址
            Console.WriteLine($"连接到目标地址TCP {this._forwardEndPoint.Address}:{this._forwardEndPoint.Port}");
            await using NetworkStream listenStream = listenTcpClient.GetStream();
            await using NetworkStream forwardStream = forwardTcpClient.GetStream();
            using CancellationTokenSource linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource().Token);// 创建CancellationTokenSource用于取消转发任务
            Task[] tasks = [
                Forward(listenStream, forwardStream, linkedTokenSource),
                Forward(forwardStream, listenStream, linkedTokenSource)
            ];
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"转发TCP数据时发生错误：{ex}");
        }
        finally
        {
            listenTcpClient.Dispose(); // 销毁TcpClient
            if (connectEndPoint is not null) Console.WriteLine($"{connectEndPoint} 的TCP连接已关闭");
        }
        return;

        static async Task Forward(NetworkStream sourceStream, NetworkStream targetStream, CancellationTokenSource cancellationTokenSource)
        {
            try
            {
                await sourceStream.CopyToAsync(targetStream, cancellationTokenSource.Token);
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.InnerException?.Message ?? ex.Message);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("TCP转发被取消");
                // 由于一侧连接关闭因此取消转发
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TCP转发发生错误：{ex}");
            }
            await cancellationTokenSource.CancelAsync(); // 取消另一侧的转发任务
        }
    }

    /// <summary>
    /// 监听UDP数据包 并将接收到的数据包加入到接收队列
    /// </summary>
    /// <param name="cancellationToken"></param>
    public async ValueTask ListenUdp(CancellationToken cancellationToken)
    {
        Console.WriteLine($"开始在 {this._listenEndPoint} 上监听UDP数据包，并转发到{this._forwardEndPoint}");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 接收来自监听地址的客户端的UDP数据包 并加入到接收队列
                byte[] buffer = UdpSocketBufferPool.Rent();
                EndPoint remoteEndPoint = this._listenEndPointPool.Rent();
                Memory<byte> memory = buffer;
                SocketReceiveFromResult socketReceive = await this._udpSocket.ReceiveFromAsync(memory, remoteEndPoint, cancellationToken);
                this._receiveUdpQueue.Enqueue((buffer, socketReceive.ReceivedBytes, socketReceive.RemoteEndPoint));
                //int length = this._udpSocket.ReceiveFrom(buffer, ref remoteEndPoint);
                //this._receiveUdpQueue.Enqueue((buffer, length, remoteEndPoint));
            }
        }
        catch (OperationCanceledException)
        {
            //UDP监听线程结束
        }
        catch (Exception ex)
        {
            Console.WriteLine($"接收目标地址的UDP包时发生错误：{ex}");
        }
        Console.WriteLine("UDP监听结束");
    }

    /// <summary>
    /// 将监听地址接收到的UDP数据包转发到目标地址
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task ReceiveUdpQueueHandler(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (byte[] buffer, int length, EndPoint endPoint) = await this._receiveUdpQueue.DequeueAsync(cancellationToken);
                //Console.WriteLine($"从监听端口接收到UDP数据包: {endPoint as IPEndPoint}");
                // 接收到客户端的UDP数据包后，查找是否已经存在与该客户端的会话
                if (this._udpSessionMapping.TryGetValue(endPoint, out UdpSessionInfo? udpClientInfo))
                {
                    // 更新最后活跃时间，转发本次数据
                    udpClientInfo.LastActivateTime = DateTime.Now;
                    udpClientInfo.UdpClientSocket.Send(buffer, length, SocketFlags.None); // 转发本次数据到目标地址
                    this._listenEndPointPool.Return(endPoint);// 回收EndPoint对象
                    //Console.WriteLine("ReceiveUdpQueueHandler 回收了EndPoint对象");
                }
                else
                {
                    // 如果不存在与该客户端的会话，则创建一个新的会话。新建一个Udp客户端，并开启异步任务监听目标地址的响应，转发到客户端
                    Socket udpClient = new(this._localBindIpAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    udpClient.Bind(new IPEndPoint(this._localBindIpAddress, 0));
                    await udpClient.ConnectAsync(this._forwardEndPoint, cancellationToken); // 连接到目标地址
                    UdpSessionInfo newUdpSessionInfo = new() // 创建一个新的UDP会话信息
                    {
                        UdpClientSocket = udpClient,
                        LastActivateTime = DateTime.Now,
                        CancellationTokenSource = new CancellationTokenSource()
                    };
                    newUdpSessionInfo.ForwardTask = Task.Run(() => ForwardUdp(newUdpSessionInfo, endPoint, cancellationToken), cancellationToken); // 启动异步任务，转发来自目标地址的UDP响应
                    this._udpSessionMapping[endPoint] = newUdpSessionInfo; // 添加到会话映射表
                    udpClient.Send(buffer, length, SocketFlags.None); // 转发本次数据到目标地址
                    Console.WriteLine($"添加UDP客户端: {endPoint as IPEndPoint} 当前有{this._udpSessionMapping.Count}个udp客户端");
                }
                UdpSocketBufferPool.Return(buffer); // 回收缓冲区对象
                //Console.WriteLine("ReceiveUdpQueueHandler 回收了Buffer对象");
                //Console.WriteLine($"转发UDP从监听侧到转发侧: {sendEndPoint} => {this._forwardEndPoint}");
            }
        }
        catch (OperationCanceledException)
        {
            // UDP接收处理线程结束
        }
        catch (Exception ex)
        {
            Console.WriteLine($"接收UDP数据包时发生错误：{ex}");
        }
        Console.WriteLine($"UDP接收处理线程结束");
    }

    /// <summary>
    /// 监听来自目标地址的UDP数据包并添加到发送队列
    /// </summary>
    /// <param name="udpSessionInfo">udp会话信息</param>
    /// <param name="clientEndPoint">客户端的终结点</param>
    /// <param name="cancellationToken"></param>
    private async ValueTask ForwardUdp(UdpSessionInfo udpSessionInfo, EndPoint clientEndPoint, CancellationToken cancellationToken)
    {
        Console.WriteLine($"开始将来自 {udpSessionInfo.UdpClientSocket.LocalEndPoint} 的UDP数据包转发至 {clientEndPoint}");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] buffer = UdpSocketBufferPool.Rent();
                EndPoint remoteEndPoint = this._forwardEndPointPool.Rent();
                Memory<byte> memory = buffer;
                try
                {
                    SocketReceiveFromResult socketReceive = await udpSessionInfo.UdpClientSocket.ReceiveFromAsync(memory, SocketFlags.None, remoteEndPoint, cancellationToken);
                    this._sendUdpQueue.Enqueue((buffer, socketReceive.ReceivedBytes, clientEndPoint));
                }
                catch (SocketException)
                {
                    UdpSocketBufferPool.Return(buffer); // 回收缓冲区对象
                    EndPointPool.Return(remoteEndPoint); // 回收EndPoint对象
                    throw new OperationCanceledException();
                }
                //Console.WriteLine($"转发UDP: {udpSessionInfo.UdpClientSocket.Client.LocalEndPoint} => {clientEndPoint}");
            }
        }
        catch (OperationCanceledException)
        {
            // {clientEndPoint} 的UDP数据包转发线程结束
        }

        catch (Exception ex)
        {
            Console.WriteLine($"转发UDP数据包时发生错误：{ex}");
        }
        finally
        {
            this._listenEndPointPool.Return(clientEndPoint);// 当转发任务结束后回收EndPoint对象
            //Console.WriteLine("UDP转发任务结束 回收EndPoint对象");
            Console.WriteLine($"{udpSessionInfo.UdpClientSocket.LocalEndPoint} 的UDP转发任务结束");
        }

    }

    /// <summary>
    /// 将目标地址接收到的响应UDP数据包发送到客户端
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task SendUdpQueueHandler(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (byte[] buffer, int length, EndPoint clientEndPoint) = await this._sendUdpQueue.DequeueAsync(cancellationToken);
                this._udpSocket.SendTo(buffer, length, SocketFlags.None, clientEndPoint);
                UdpSocketBufferPool.Return(buffer); // 回收缓冲区对象
                this._forwardEndPointPool.Return(clientEndPoint); // 回收EndPoint对象
                //Console.WriteLine("转发了响应UDP数据包 回收了Buffer和EndPoint对象");
            }
        }
        catch (OperationCanceledException)
        {
            // UDP发送线程结束
        }
        catch (Exception ex)
        {
            Console.WriteLine($"发送UDP数据包时发生错误：{ex}");
        }
        Console.WriteLine($"UDP发送处理线程结束");
    }

    /// <summary>
    /// 清理不活跃的UDP会话
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task UdpSessionCleaner(CancellationToken cancellationToken)
    {
        Console.WriteLine($"UDP会话清理后台任务启动 超时时间:{this._udpClientCleanInterval.TotalSeconds}s");
        try
        {
            await Task.Delay(this._udpClientCleanInterval, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTime now = DateTime.Now;
                foreach (KeyValuePair<EndPoint, UdpSessionInfo> udpClient in this._udpSessionMapping)
                {
                    if (now - udpClient.Value.LastActivateTime < this._udpSessionTimeout) continue;
                    await udpClient.Value.CancellationTokenSource.CancelAsync();
                    await udpClient.Value.ForwardTask;
                    udpClient.Value.ForwardTask = Task.CompletedTask; // 删除对转发任务的引用 防止循环引用
                    udpClient.Value.UdpClientSocket.Dispose();
                    udpClient.Value.CancellationTokenSource.Dispose();
                    this._udpSessionMapping.TryRemove(udpClient.Key, out _);
                    Console.WriteLine($"移除不活跃的UDP客户端: {udpClient.Key} 当前有{this._udpSessionMapping.Count}个udp客户端");
                }
                await Task.Delay(this._udpClientCleanInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            //UDP会话清理线程结束
        }
        catch (Exception ex)
        {
            Console.WriteLine($"清理UDP会话时发生错误：{ex}");
        }
        foreach (KeyValuePair<EndPoint, UdpSessionInfo> udpClient in this._udpSessionMapping)
        {
            await udpClient.Value.CancellationTokenSource.CancelAsync();
            await udpClient.Value.ForwardTask;
            udpClient.Value.UdpClientSocket.Dispose();
            udpClient.Value.CancellationTokenSource.Dispose();
            Console.WriteLine($"移除UDP客户端: {udpClient.Key} 当前有{this._udpSessionMapping.Count}个UDP客户端");
        }
        this._udpSessionMapping.Clear();
        Console.WriteLine("UDP会话清理线程结束");

    }

    /// <summary>
    /// UDP会话信息
    /// </summary>
    private class UdpSessionInfo
    {
        /// <summary>
        /// 最后活跃时间
        /// </summary>
        public DateTime LastActivateTime { get; set; }
        /// <summary>
        /// 该UDP会话的客户端Socket对象 用于接收来自目标地址的响应数据包
        /// </summary>
        public required Socket UdpClientSocket { get; init; }
        /// <summary>
        /// 该UDP会话的取消令牌源
        /// </summary>
        public required CancellationTokenSource CancellationTokenSource { get; init; }
        /// <summary>
        /// 转发任务
        /// </summary>
        public Task ForwardTask { get; set; } = Task.CompletedTask;
    }

    public readonly struct Config
    {
        public required IPEndPoint ListenEndPoint { get; init; }
        public required IPEndPoint ForwardEndPoint { get; init; }
        public required int UdpSessionTimeout { get; init; }
        public required int TcpConnectHandleThreadCount { get; init; }
        public required int UdpClientCleanInterval { get; init; }
    }
}