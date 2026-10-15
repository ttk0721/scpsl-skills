namespace SlKit.Core;

/// <summary>
/// The rule behind SlKit's own error lines: a few at once, then a trickle, with the lines held back
/// counted so the next one that gets through can say how many were suppressed.
/// </summary>
/// <remarks>
/// Stateless: the caller keeps the <see cref="TokenBucket"/> in a field. It is created on first
/// use, so a job or worker that never fails never allocates one.
/// Thread affinity: none (pure), but a bucket is NOT thread-safe, so one belongs to one thread.
/// </remarks>
internal static class ErrorLogThrottle
{
    /// <summary>Asks whether one more error line may be written now.</summary>
    /// <param name="bucket">The caller's bucket; created here on first use.</param>
    /// <param name="burst">Lines allowed back to back (used when the bucket is created).</param>
    /// <param name="refillPerSecond">Lines earned back per second (used when the bucket is created).</param>
    /// <param name="now">Current Stopwatch timestamp.</param>
    /// <param name="suppressed">Errors refused since the previous line that was allowed; 0 when this one is refused.</param>
    /// <returns>True when the caller should write the line.</returns>
    internal static bool TryAcquire(ref TokenBucket bucket, double burst, double refillPerSecond, long now, out long suppressed)
    {
        bucket ??= new TokenBucket(burst, refillPerSecond, now);
        suppressed = 0;
        if (!bucket.TryTake(now))
        {
            return false;
        }

        suppressed = bucket.TakeDeniedCount();
        return true;
    }
}
