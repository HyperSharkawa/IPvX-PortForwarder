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
    private readonly TcpListener _listenTcpListener;
    private readonly UdpClient _listenUdpClient; // 用于监听UDP数据包
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreQueue<UdpReceiveResult> _receiveUdpQueue = new();
    private readonly SemaphoreQueue<(UdpReceiveResult, IPEndPoint)> _sendUdpQueue = new();
    private readonly SemaphoreQueue<TcpClient> _tcpConnectQueue = new();
    private Task[] _worker = [];


    private readonly ConcurrentDictionary<IPEndPoint, UdpSessionInfo> _udpSessionMapping = [];


    public PortForwarder(Config config)
    {
        this._tcpConnectHandleThreadCount = config.TcpConnectHandleThreadCount;
        this._udpClientCleanInterval = TimeSpan.FromSeconds(config.UdpClientCleanInterval);
        this._udpSessionTimeout = TimeSpan.FromSeconds(config.UdpSessionTimeout);
        this._listenEndPoint = config.ListenEndPoint;
        this._forwardEndPoint = config.ForwardEndPoint;
        this._listenTcpListener = new TcpListener(this._listenEndPoint);
        this._listenUdpClient = new UdpClient(config.ListenEndPoint.Port); // 绑定监听端口，但不连接到目标地址，因为需要接受和发送来自任意地址的UDP数据包
        const int sioUdpConnreset = -1744830452; // SIO_UDP_CONNRESET的常量值
        this._listenUdpClient.Client.IOControl(sioUdpConnreset, [0, 0, 0, 0], null); // 忽略UDP连接重置错误
    }

    ~PortForwarder() => this.Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this._cancellationTokenSource?.Cancel();
        Task.WaitAll(this._worker);
        this._cancellationTokenSource?.Dispose();
        this._listenTcpListener.Dispose();
        this._listenUdpClient.Dispose();
    }

    public void Start()
    {
        this._cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = this._cancellationTokenSource.Token;
        this._listenTcpListener.Start();
        this._worker = [
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
        Task.WaitAll(this._worker);
        Console.WriteLine("端口转发停止");
    }

    public async Task ListenTcp(CancellationToken cancellationToken)
    {
        Console.WriteLine($"开始在 {this._listenEndPoint} 上监听TCP连接");
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

    public async Task ListenUdp(CancellationToken cancellationToken)
    {
        Console.WriteLine($"开始在 {this._listenEndPoint} 上监听并转发UDP数据包");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 接收来自客户端的UDP数据包 并加入到接收队列
                UdpReceiveResult receiveResult = await this._listenUdpClient.ReceiveAsync(cancellationToken);
                this._receiveUdpQueue.Enqueue(receiveResult);
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
            using CancellationTokenSource cancellationTokenSource = new(); // 创建CancellationTokenSource用于取消转发任务
            Task[] tasks = [
                Forward(listenStream, forwardStream, cancellationTokenSource),
                Forward(forwardStream, listenStream, cancellationTokenSource)
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

    private async Task ForwardUdp(UdpSessionInfo udpSessionInfo, IPEndPoint clientEndPoint, CancellationToken cancellationToken)
    {
        Console.WriteLine($"开始将来自 {udpSessionInfo.UdpClient.Client.LocalEndPoint} 的UDP数据包转发至 {clientEndPoint}");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult receiveResult = await udpSessionInfo.UdpClient.ReceiveAsync(udpSessionInfo.CancellationTokenSource.Token);
                //await this._listenUdpClient.SendAsync(receiveResult.Buffer, receiveResult.Buffer.Length, clientEndPoint);
                this._sendUdpQueue.Enqueue((receiveResult, clientEndPoint));
                //Console.WriteLine($"转发UDP: {udpSessionInfo.UdpClient.Client.LocalEndPoint} => {clientEndPoint}");
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
            Console.WriteLine($"{udpSessionInfo.UdpClient.Client.LocalEndPoint} 的UDP转发任务结束");
        }

    }

    private async Task TcpConnectQueueHandler(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient tcpClient = await this._tcpConnectQueue.DequeueAsync(cancellationToken);
                // 开启任务双向转发数据
                _ = ForwardTcp(tcpClient, cancellationToken);
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

    private async Task ReceiveUdpQueueHandler(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult receiveResult = await this._receiveUdpQueue.DequeueAsync(cancellationToken);
                // 接收到客户端的UDP数据包后，查找是否已经存在与该客户端的会话
                if (this._udpSessionMapping.TryGetValue(receiveResult.RemoteEndPoint, out UdpSessionInfo? udpClientInfo))
                {
                    // 更新最后活跃时间，转发本次数据
                    udpClientInfo.LastActivateTime = DateTime.Now;
                    await udpClientInfo.UdpClient.SendAsync(receiveResult.Buffer, receiveResult.Buffer.Length);
                }
                else
                {
                    // 如果不存在与该客户端的会话，则创建一个新的会话。新建一个Udp客户端，并开启异步任务监听目标地址的响应，转发到客户端
                    // 创建一个新的 IPEndPoint，随机分配一个端口号
                    UdpClient udpClient = new(new IPEndPoint(this._forwardEndPoint.Address, 0));
                    udpClient.Connect(this._forwardEndPoint); // 连接到目标地址
                    UdpSessionInfo newUdpSessionInfo = new() // 创建一个新的UDP会话信息
                    {
                        UdpClient = udpClient,
                        LastActivateTime = DateTime.Now,
                        CancellationTokenSource = new CancellationTokenSource()
                    };
                    newUdpSessionInfo.ForwardTask = Task.Run(() => ForwardUdp(newUdpSessionInfo, receiveResult.RemoteEndPoint, cancellationToken), cancellationToken); // 启动异步任务，转发来自目标地址的UDP响应
                    //newUdpSessionInfo.ForwardTask = ForwardUdp(newUdpSessionInfo, receiveResult.RemoteEndPoint, cancellationToken);
                    this._udpSessionMapping[receiveResult.RemoteEndPoint] = newUdpSessionInfo; // 添加到会话映射表
                    await udpClient.SendAsync(receiveResult.Buffer, receiveResult.Buffer.Length); // 转发本次数据
                    Console.WriteLine($"添加UDP客户端: {receiveResult.RemoteEndPoint} 当前有{this._udpSessionMapping.Count}个udp客户端");
                }
                //Console.WriteLine($"转发UDP: {receiveResult.RemoteEndPoint} => {this._forwardEndPoint}");
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

    private async Task SendUdpQueueHandler(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (UdpReceiveResult sendResult, IPEndPoint clientEndPoint) = await this._sendUdpQueue.DequeueAsync(cancellationToken);
                await this._listenUdpClient.SendAsync(sendResult.Buffer, sendResult.Buffer.Length, clientEndPoint);
                //Console.WriteLine($"转发UDP: {this._forwardEndPoint} => {sendResult.RemoteEndPoint}");
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

    private async Task UdpSessionCleaner(CancellationToken cancellationToken)
    {
        Console.WriteLine($"UDP会话清理后台任务启动 超时时间:{this._udpClientCleanInterval.TotalSeconds}s");
        try
        {
            await Task.Delay(this._udpClientCleanInterval, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTime now = DateTime.Now;
                foreach (KeyValuePair<IPEndPoint, UdpSessionInfo> udpClient in this._udpSessionMapping)
                {
                    if (now - udpClient.Value.LastActivateTime < this._udpSessionTimeout) continue;
                    await udpClient.Value.CancellationTokenSource.CancelAsync();
                    await udpClient.Value.ForwardTask;
                    udpClient.Value.ForwardTask = Task.CompletedTask; // 删除对转发任务的引用 防止循环引用
                    udpClient.Value.UdpClient.Dispose();
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
        foreach (KeyValuePair<IPEndPoint, UdpSessionInfo> udpClient in this._udpSessionMapping)
        {
            await udpClient.Value.CancellationTokenSource.CancelAsync();
            await udpClient.Value.ForwardTask;
            udpClient.Value.UdpClient.Dispose();
            udpClient.Value.CancellationTokenSource.Dispose();
            Console.WriteLine($"移除UDP客户端: {udpClient.Key} 当前有{this._udpSessionMapping.Count}个UDP客户端");
        }
        this._udpSessionMapping.Clear();
        Console.WriteLine("UDP会话清理线程结束");

    }

    private class UdpSessionInfo
    {
        // 最后活跃时间
        public DateTime LastActivateTime { get; set; } = DateTime.Now;
        public required UdpClient UdpClient { get; init; }
        // 用于在不活跃时取消转发任务
        public required CancellationTokenSource CancellationTokenSource { get; init; }
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