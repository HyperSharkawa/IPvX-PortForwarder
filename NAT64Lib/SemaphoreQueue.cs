using System.Collections.Concurrent;

namespace NAT64Lib;
internal class SemaphoreQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = [];
    private SemaphoreSlim _signal = new(0);
    private readonly Lock _lock = new();

    public void Enqueue(T value)
    {
        _queue.Enqueue(value);
        _signal.Release();
    }

    public async Task<T> DequeueAsync()
    {
        await _signal.WaitAsync();
        T value = _queue.TryDequeue(out T? thing) ? thing : throw new Exception();
        return value;
    }

    public async Task<T> DequeueAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken);
        T value = _queue.TryDequeue(out T? thing) ? thing : throw new Exception();
        return value;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            this._signal.Dispose();
            this._signal = new SemaphoreSlim(0);
        }
    }

    public bool HasPendingTasks => !_queue.IsEmpty;

}