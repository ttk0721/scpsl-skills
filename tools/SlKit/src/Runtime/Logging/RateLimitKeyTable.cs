using System;
using System.Collections.Generic;
using System.Threading;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// The keys of one <see cref="RateLimitedLogger"/>: a token bucket per key and the refused calls waiting to be
/// reported, all behind one short lock that is never held while logging or running a message factory. Past
/// <see cref="RateLimitedLogger.MaxKeys"/> keys the rest share the <see cref="RateLimitedLogger.OverflowKey"/> bucket.
/// Thread affinity: any thread.
/// </summary>
internal sealed class RateLimitKeyTable
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly int _burst;
    private readonly double _refillPerSecond;
    private readonly long _summaryTicks;
    private long _nextSweep;

    internal RateLimitKeyTable(int burst, double refillPerSecond, long summaryTicks)
    {
        _burst = burst;
        _refillPerSecond = refillPerSecond;
        _summaryTicks = summaryTicks;
    }

    /// <summary>Number of keys currently tracked.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Forgets every key and its bucket; pending suppressed counts are dropped.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _nextSweep = 0;
        }
    }

    /// <summary>Takes one line from the key's bucket and notes the level for a later summary.</summary>
    /// <param name="key">The key; replaced by <see cref="RateLimitedLogger.OverflowKey"/> when the table is full and the key is new.</param>
    /// <param name="level">Level of the call.</param>
    /// <param name="now">Stopwatch ticks.</param>
    /// <param name="pending">When the line is allowed: refused calls of this key not reported yet, otherwise 0.</param>
    /// <returns>True when the line may be logged.</returns>
    internal bool TryTake(ref string key, RateLimitedLogger.Level level, long now, out long pending)
    {
        pending = 0;
        lock (_gate)
        {
            Entry entry = GetOrCreate(ref key, now);
            entry.LastLevel = level;
            bool allowed = entry.Bucket.TryTake(now);
            if (allowed)
            {
                pending = entry.Bucket.TakeDeniedCount();
            }

            return allowed;
        }
    }

    /// <summary>True once the summary interval since the last <see cref="TakeSuppressed"/> has passed. Lock-free.</summary>
    internal bool IsSweepDue(long now) => now >= Volatile.Read(ref _nextSweep);

    /// <summary>Collects and clears the refused-call counts of every key, and schedules the next sweep.</summary>
    /// <param name="now">Stopwatch ticks.</param>
    /// <returns>The keys with refused calls pending, or null when there are none (nothing is allocated then).</returns>
    internal List<Suppressed> TakeSuppressed(long now)
    {
        List<Suppressed> pending = null;

        lock (_gate)
        {
            _nextSweep = now + _summaryTicks;
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                long denied = kv.Value.Bucket.TakeDeniedCount();
                if (denied <= 0)
                {
                    continue;
                }

                if (pending == null)
                {
                    pending = new List<Suppressed>();
                }

                pending.Add(new Suppressed { Key = kv.Key, Count = denied, Level = kv.Value.LastLevel });
            }
        }

        return pending;
    }

    private Entry GetOrCreate(ref string key, long now)
    {
        if (_entries.TryGetValue(key, out Entry entry))
        {
            return entry;
        }

        if (_entries.Count >= RateLimitedLogger.MaxKeys)
        {
            key = RateLimitedLogger.OverflowKey;
            if (_entries.TryGetValue(key, out entry))
            {
                return entry;
            }
        }

        entry = new Entry { Bucket = new TokenBucket(_burst, _refillPerSecond, now) };
        _entries.Add(key, entry);
        return entry;
    }

    private sealed class Entry
    {
        public TokenBucket Bucket;
        public RateLimitedLogger.Level LastLevel;
    }

    /// <summary>Refused calls of one key, waiting for a summary line.</summary>
    internal struct Suppressed
    {
        public string Key;
        public long Count;
        public RateLimitedLogger.Level Level;
    }
}
