namespace NAT64Lib;

internal class ObjectPool<T>
{
    private readonly ChannelBasedQueue<T> _objectQueue = new();
    private readonly Func<T> _factory;
    private int _activeCount;
    private int _objectCount;
    public ObjectPool(Func<T> factory)
    {
        this._factory = factory;
    }
    public async ValueTask<T> RentAsync()
    {
        Interlocked.Increment(ref this._activeCount);
        if (!await this._objectQueue.IsEmpty) return await this._objectQueue.DequeueAsync();
        Interlocked.Increment(ref this._objectCount);
        Console.WriteLine($"创建新对象。当前对象数：{this._objectCount}");
        return this._factory();
    }

    public T Rent()
    {
        Interlocked.Increment(ref this._activeCount);
        if (this._objectQueue.TryDequeue(out T? item)) return item;
        Interlocked.Increment(ref this._objectCount);
        Console.WriteLine($"创建新对象。当前对象数：{this._objectCount}");
        return this._factory();
    }

    public void Return(T value)
    {
        Interlocked.Decrement(ref _activeCount);
        this._objectQueue.Enqueue(value);
    }
}

