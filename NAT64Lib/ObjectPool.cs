using System.Collections.Concurrent;

namespace NAT64Lib;

internal class ObjectPool<T>
{
    private readonly ConcurrentQueue<T> _pool = [];
    private readonly Func<T> _factory;
    private int _activeObjectCount;
    private int _totalCreatedCount;

    public ObjectPool(Func<T> factory, int initialCount = 0)
    {
        this._factory = factory;
        this.ExpandPool(initialCount);
        //Task.Run(PrintObjectStatus);
    }

    public T Rent()
    {
        Interlocked.Increment(ref this._activeObjectCount);
        //Console.WriteLine($"借用{typeof(T)}对象。当前对象数：{this._totalCreatedCount}");
        if (this._pool.TryDequeue(out T? item)) return item;
        Interlocked.Increment(ref this._totalCreatedCount);
        Console.WriteLine($"创建新{typeof(T)}对象。总创建对象数:{this._totalCreatedCount} 当前对象数:{this._pool.Count} 使用中对象数:{this._activeObjectCount}");
        return this._factory();
    }

    public PooledObject RentScoped() => new(this, this.Rent());

    public void Return(T value)
    {
        //Console.WriteLine($"回收{typeof(T)}对象。当前对象数：{this._totalCreatedCount}");
        Interlocked.Decrement(ref _activeObjectCount);
        this._pool.Enqueue(value);
    }

    public async void PrintObjectStatus()
    {
        while (true)
        {
            await Task.Delay(2000);
            Console.WriteLine($"类型:{typeof(T)} 总创建对象数:{this._totalCreatedCount} 当前对象数:{this._pool.Count} 使用中对象数:{this._activeObjectCount}");
        }
    }

    public readonly struct PooledObject : IDisposable
    {
        private readonly ObjectPool<T> _pool;
        public T Value { get; }

        public PooledObject(ObjectPool<T> pool, T value)
        {
            this._pool = pool;
            this.Value = value;
        }
        public void Dispose() => this._pool.Return(this.Value);
    }

    public void ExpandPool(int targetCount)
    {
        while (this._totalCreatedCount < targetCount)
        {
            this._pool.Enqueue(this._factory());
            Interlocked.Increment(ref this._totalCreatedCount);
            //Console.WriteLine($"创建新{typeof(T)}对象。总创建对象数:{this._totalCreatedCount} 当前对象数:{this._pool.Count} 使用中对象数:{this._activeObjectCount}");
        }
    }
}

