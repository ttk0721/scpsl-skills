using System;

namespace SlKit.Core;

/// <summary>
/// Fixed-capacity ring buffer of <see cref="double"/> samples. Adding never allocates; once full,
/// the oldest sample is overwritten.
/// </summary>
/// <remarks>
/// Used for per-frame measurements, where an allocation per frame would feed the server's Boehm
/// GC (non-generational: every collection marks the whole live heap, so allocation rate sets how
/// often the main thread pays for a mark). Thread affinity: not thread-safe; use it from one
/// thread (SlKit uses it on the main thread only) and copy samples out for other threads.
/// </remarks>
public sealed class RingBuffer
{
    private readonly double[] _items;
    private int _next;
    private int _count;
    private long _totalAdded;

    /// <summary>Creates a buffer holding the last <paramref name="capacity"/> samples.</summary>
    /// <param name="capacity">Maximum number of samples kept; must be positive.</param>
    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _items = new double[capacity];
    }

    /// <summary>Maximum number of samples kept.</summary>
    public int Capacity => _items.Length;

    /// <summary>Number of samples currently held (at most <see cref="Capacity"/>).</summary>
    public int Count => _count;

    /// <summary>Number of samples ever added, including overwritten ones.</summary>
    public long TotalAdded => _totalAdded;

    /// <summary>The most recently added sample, or 0 when empty.</summary>
    public double Last => _count == 0 ? 0 : _items[(_next - 1 + _items.Length) % _items.Length];

    /// <summary>Appends a sample, overwriting the oldest one when full. Allocation-free.</summary>
    /// <param name="value">The sample.</param>
    public void Add(double value)
    {
        _items[_next] = value;
        _next++;
        if (_next == _items.Length)
        {
            _next = 0;
        }

        if (_count < _items.Length)
        {
            _count++;
        }

        _totalAdded++;
    }

    /// <summary>Removes all samples (keeps <see cref="TotalAdded"/>).</summary>
    public void Clear()
    {
        _next = 0;
        _count = 0;
    }

    /// <summary>Copies every held sample, oldest first, into <paramref name="destination"/>.</summary>
    /// <param name="destination">Target array; must hold at least <see cref="Count"/> elements.</param>
    /// <returns>The number of samples copied.</returns>
    public int CopyTo(double[] destination) => CopyLast(_count, destination, 0);

    /// <summary>
    /// Copies the newest <paramref name="count"/> samples (fewer if not available), oldest first,
    /// into <paramref name="destination"/> starting at <paramref name="offset"/>. Allocation-free.
    /// </summary>
    /// <param name="count">How many of the newest samples to copy.</param>
    /// <param name="destination">Target array.</param>
    /// <param name="offset">Start index in <paramref name="destination"/>.</param>
    /// <returns>The number of samples copied.</returns>
    public int CopyLast(int count, double[] destination, int offset)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (count > _count)
        {
            count = _count;
        }

        if (count <= 0)
        {
            return 0;
        }

        if (offset < 0 || destination.Length - offset < count)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        int start = _next - count;
        if (start < 0)
        {
            start += _items.Length;
        }

        int firstPart = Math.Min(count, _items.Length - start);
        Array.Copy(_items, start, destination, offset, firstPart);
        if (firstPart < count)
        {
            Array.Copy(_items, 0, destination, offset + firstPart, count - firstPart);
        }

        return count;
    }
}
