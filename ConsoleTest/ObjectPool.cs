namespace ConsoleTest;

internal class ObjectPool<T>
{
    private readonly SemaphoreQueue<T> _queue = new();
    private readonly Func<T> _factory;
    private int _objectCount;
    public ObjectPool(Func<T> factory)
    {
        this._factory = factory;
    }
    public T Get()
    {
        if (this._queue.HasPendingTasks) return this._queue.Dequeue();
        Interlocked.Increment(ref _objectCount);
        Console.WriteLine($"创建新对象。当前对象数：{this._objectCount}");
        return _factory();
    }
    public void Return(T value)
    {
        this._queue.Enqueue(value);
    }
}

