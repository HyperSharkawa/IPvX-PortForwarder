using System.Net;
using System.Net.Sockets;

namespace NAT64Lib;

internal class PooledUdpClient
{
    private const int SioUdpConnreset = -1744830452; // SIO_UDP_CONNRESET的常量值
    private const int MaxUdpBufferSize = 0x10000; // 64KB
    private readonly Socket _socket;
    private static readonly ObjectPool<byte[]> SocketBufferPool = new(() => new byte[MaxUdpBufferSize]);

    private IPEndPoint? _connectRemoteEndPoint;
    private IPEndPoint ConnectRemoteEndPoint
    {
        get => this._connectRemoteEndPoint ?? throw new InvalidOperationException("未设置远程终结点");
        set
        {
            if (this._connectRemoteEndPoint != null) throw new InvalidOperationException("远程终结点已设置");
            this._connectRemoteEndPoint = value;
        }
    }

    public PooledUdpClient(IPEndPoint listenEndPoint, bool ignoreConnectionResetError = false)
    {
        this._socket = new Socket(listenEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        this._socket.Bind(listenEndPoint);
        if (ignoreConnectionResetError) this._socket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null); // 忽略UDP连接重置错误
    }

    public int SendTo(byte[] buffer, IPEndPoint remoteEndPoint) => this._socket.SendTo(buffer, remoteEndPoint);

    public int SendTo(byte[] buffer, int length, IPEndPoint remoteEndPoint) => this._socket.SendTo(buffer, 0, length, SocketFlags.None, remoteEndPoint);

    public int Send(byte[] buffer) => this.SendTo(buffer, this.ConnectRemoteEndPoint);

    public int Send(byte[] buffer, int length) => this.SendTo(buffer, length, this.ConnectRemoteEndPoint);

    public void Connect(IPEndPoint remoteEndPoint) => this.ConnectRemoteEndPoint = remoteEndPoint;

    public void Receive(ref byte[] bufferCopy, ref EndPoint remoteEndPoint)
    {
        byte[] buffer = SocketBufferPool.Rent();
        int length = this._socket.ReceiveFrom(buffer, ref remoteEndPoint);
        buffer.AsSpan()[..length].CopyTo(bufferCopy);
        SocketBufferPool.Return(buffer);
    }

    public void Close() => this._socket.Close();
}

