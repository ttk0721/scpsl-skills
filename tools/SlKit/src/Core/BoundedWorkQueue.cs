using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SlKit.Core;

/// <summary>
/// Multi-producer queue with a hard capacity, a non-blocking <see cref="TryEnqueue"/> and a
/// blocking-with-timeout <see cref="TryDequeue"/> for a consumer thread.
/// </summary>
/// <remarks>
/// The producer side never waits: when the queue is full <see cref="TryEnqueue"/> returns false
/// and the caller decides (drop, retry next frame, count). A producer on the game's main thread
/// must never block, because every frame of the server runs on that one thread.
/// Enqueue is lock-free (<see cref="ConcurrentQueue{T}"/> plus an interlocked counter); the
/// consumer is woken through a <see cref="SemaphoreSlim"/>.
/// Thread affinity: every member is thread-safe.
/// </remarks>
/// <typeparam name="T">Item type.</typeparam>
public sealed class BoundedWorkQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();
    private readonly SemaphoreSlim _available = new SemaphoreSlim(0, int.MaxValue);
    private readonly int _capacity;
    private int _count;
    private long _rejected;

    /// <summary>Creates an empty queue.</summary>
    /// <param name="capacity">Maximum number of queued items; must be positive.</param>
    public BoundedWorkQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _capacity = capacity;
    }

    /// <summary>Maximum number of queued items.</summary>
    public int Capacity => _capacity;

    /// <summary>Items currently queued (approximate under concurrency).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Total number of items refused because the queue was full.</summary>
    public long RejectedCount => Interlocked.Read(ref _rejected);

    /// <summary>Adds an item unless the queue is full. Never blocks. Thread-safe.</summary>
    /// <param name="item">The item.</param>
    /// <returns>True when queued; false when the queue was full.</returns>
    public bool TryEnqueue(T item)
    {
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _rejected);
            return false;
        }

        _queue.Enqueue(item);
        _available.Release();
        return true;
    }

    /// <summary>
    /// Takes the oldest item, waiting up to <paramref name="millisecondsTimeout"/> for one to arrive.
    /// Intended for a dedicated consumer thread only: never call it with a non-zero timeout on the
    /// game's main thread. Thread-safe.
    /// </summary>
    /// <param name="item">The dequeued item, or default.</param>
    /// <param name="millisecondsTimeout">Maximum wait; 0 = do not wait; <see cref="Timeout.Infinite"/> = wait forever.</param>
    /// <returns>True when an item was taken.</returns>
    public bool TryDequeue(out T item, int millisecondsTimeout)
    {
        if (!_available.Wait(millisecondsTimeout))
        {
            item = default;
            return false;
        }

        // Every Release() above pairs with exactly one Enqueue() that happened before it.
        if (!_queue.TryDequeue(out item))
        {
            return false;
        }

        Interlocked.Decrement(ref _count);
        return true;
    }
}
