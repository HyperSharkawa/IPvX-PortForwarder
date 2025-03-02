using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace NAT64Lib;
public class ChannelBasedQueue<T>
{
    private readonly Channel<T> _channel;

    public ChannelBasedQueue(int? capacity = null)
    {
        BoundedChannelOptions options = new(capacity ?? int.MaxValue)
        {
            SingleReader = true,
            SingleWriter = false
        };
        this._channel = capacity.HasValue ? Channel.CreateBounded<T>(options) : Channel.CreateUnbounded<T>();
    }

    public bool Enqueue(T item) => this._channel.Writer.TryWrite(item);

    public ValueTask<T> DequeueAsync(CancellationToken cancellationToken = default) => this._channel.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// ȡ��һ��Ԫ�ز����ء��������Ϊ��������ֱ����Ԫ�ؿ�ȡ��
    /// </summary>
    /// <returns></returns>
    public T Dequeue() => this._channel.Reader.TryRead(out T? item) ? item : this._channel.Reader.ReadAsync().AsTask().Result;

    /// <summary>
    /// ����ȡ��һ��Ԫ�ز�����item�з��ء����Ϊ���򷵻�false��
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    public bool TryDequeue([MaybeNullWhen(false)] out T item) => this._channel.Reader.TryRead(out item);

    public ValueTask<bool> IsEmpty => this._channel.Reader.WaitToReadAsync();

    public void Clear()
    {
        while (this._channel.Reader.TryRead(out _)) { }
    }
}
