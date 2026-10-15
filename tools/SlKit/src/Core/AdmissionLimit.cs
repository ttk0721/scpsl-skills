using System;
using System.Threading;

namespace SlKit.Core;

/// <summary>
/// Counts the units of work that are currently admitted and refuses new ones past a limit, without
/// a lock and without waiting.
/// </summary>
/// <remarks>
/// SlHttp admits at most "running + waiting" requests per client and refuses the rest at once, so a
/// burst cannot build an unbounded backlog of tasks that would all time out anyway. The limit is a
/// <see cref="long"/> because it is the SUM of two caller-chosen int settings: adding them as ints
/// wraps negative when one is huge (<c>int.MaxValue</c> as "no queue limit" would then refuse every
/// request). Thread affinity: every member is thread-safe.
/// </remarks>
public sealed class AdmissionLimit
{
    private readonly long _limit;
    private int _count;
    private long _refused;

    /// <summary>Creates a counter that admits up to <paramref name="limit"/> units at once.</summary>
    /// <param name="limit">Most units admitted at once; must not be negative (0 refuses everything).</param>
    public AdmissionLimit(long limit)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must not be negative.");
        }

        _limit = limit;
    }

    /// <summary>Most units admitted at once.</summary>
    public long Limit => _limit;

    /// <summary>Units admitted and not yet left.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Total number of units refused.</summary>
    public long RefusedCount => Interlocked.Read(ref _refused);

    /// <summary>Admits one unit unless the limit is reached. Never blocks. Thread-safe.</summary>
    /// <returns>True when admitted (the caller must call <see cref="Leave"/> exactly once); false when refused.</returns>
    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _count) > _limit)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _refused);
            return false;
        }

        return true;
    }

    /// <summary>Releases one admitted unit. Thread-safe.</summary>
    public void Leave()
    {
        Interlocked.Decrement(ref _count);
    }
}
