using System;

namespace SlKit.Core;

/// <summary>
/// Token bucket rate limiter on the Stopwatch clock: up to <see cref="Capacity"/> events in a
/// burst, refilled continuously at <see cref="RefillPerSecond"/>.
/// </summary>
/// <remarks>
/// Used to rate-limit logging: LabAPI's <c>Logger.*</c> does a stack walk, two regexes and a
/// dispatcher closure per line, so an exception logged every frame costs real main-thread time.
/// The caller passes "now", which keeps the class pure and testable.
/// Thread affinity: none (pure), but an instance is NOT thread-safe; guard it with a lock if
/// several threads share it.
/// </remarks>
public sealed class TokenBucket
{
    private readonly double _refillPerTick;
    private double _tokens;
    private long _lastRefill;
    private long _denied;

    /// <summary>Creates a full bucket.</summary>
    /// <param name="capacity">Maximum burst size in tokens; must be positive.</param>
    /// <param name="refillPerSecond">Tokens added per second; must not be negative.</param>
    /// <param name="now">Current Stopwatch timestamp.</param>
    public TokenBucket(double capacity, double refillPerSecond, long now)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        if (refillPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(refillPerSecond), "Refill rate must not be negative.");
        }

        Capacity = capacity;
        RefillPerSecond = refillPerSecond;
        _refillPerTick = refillPerSecond / StopwatchTime.Frequency;
        _tokens = capacity;
        _lastRefill = now;
    }

    /// <summary>Maximum burst size in tokens.</summary>
    public double Capacity { get; }

    /// <summary>Tokens added per second.</summary>
    public double RefillPerSecond { get; }

    /// <summary>Number of <see cref="TryTake"/> calls refused since the last <see cref="TakeDeniedCount"/>.</summary>
    public long DeniedCount => _denied;

    /// <summary>Tokens available as of the last refill (call <see cref="Refill"/> first for a fresh value).</summary>
    public double Tokens => _tokens;

    /// <summary>Adds the tokens accrued since the last refill, capped at <see cref="Capacity"/>.</summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    public void Refill(long now)
    {
        long elapsed = now - _lastRefill;
        if (elapsed <= 0)
        {
            return;
        }

        _lastRefill = now;
        _tokens = Math.Min(Capacity, _tokens + elapsed * _refillPerTick);
    }

    /// <summary>Takes <paramref name="cost"/> tokens if available.</summary>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <param name="cost">Tokens to take; defaults to 1.</param>
    /// <returns>True when allowed; false (and counted in <see cref="DeniedCount"/>) when not.</returns>
    public bool TryTake(long now, double cost = 1)
    {
        Refill(now);
        if (_tokens >= cost)
        {
            _tokens -= cost;
            return true;
        }

        _denied++;
        return false;
    }

    /// <summary>Returns the refused-call count and resets it to 0 (for "N suppressed" summaries).</summary>
    /// <returns>Calls refused since the previous reset.</returns>
    public long TakeDeniedCount()
    {
        long denied = _denied;
        _denied = 0;
        return denied;
    }
}
