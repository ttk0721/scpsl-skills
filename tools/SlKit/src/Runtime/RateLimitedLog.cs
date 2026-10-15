using System;

namespace SlKit;

/// <summary>
/// Logging with a token bucket per key, so a message that repeats every frame, every packet or
/// every player costs a counter increment instead of a log line.
/// </summary>
/// <remarks>
/// Why: LabAPI's <c>Logger.*</c> does a stack walk for the calling assembly and runs regexes in
/// <c>ServerConsole.AddLog</c>, so one line is far from free, and an exception caught in an
/// event handler can fire thousands of times a minute. The message is a <see cref="Func{TResult}"/>
/// so that string building (the other half of the cost) is also skipped when the bucket refuses:
/// <c>RateLimitedLog.Warn("voice", () =&gt; $"bad packet from {p.Nickname}")</c> allocates nothing for the string
/// but the closure (hoist it into a static or cached delegate on truly hot paths).
/// Each key allows <see cref="Burst"/> lines at once and then one per <see cref="RefillInterval"/>.
/// Refused calls are counted and reported as "N messages suppressed" the next time the key gets a
/// line, and by a periodic sweep (<see cref="SummaryInterval"/>) piggybacked on any call,
/// <see cref="EnablePeriodicSummary"/> or <see cref="FlushSuppressed"/>.
/// This class is the one table every plugin shares, so it has no setting a plugin can change for the
/// others: the limits are fixed (3 lines, then one per 10 s, summaries every 30 s), and there is no
/// way to wipe the table. Keys are shared too, so prefix them with your plugin name. A plugin that
/// needs other limits, or keys of its own, creates a <see cref="RateLimitedLogger"/>.
/// Thread affinity: any thread. LabAPI's logger ends in concurrent queues, and all state here
/// sits behind one short lock that is never held while logging or running the factory. The factory
/// runs on the calling thread, so it must follow that thread's rules (game objects: main thread only).
/// </remarks>
public static class RateLimitedLog
{
    /// <summary>Most distinct keys tracked. Further keys share the <see cref="OverflowKey"/> bucket, so dynamic keys cannot leak memory.</summary>
    public const int MaxKeys = RateLimitedLogger.MaxKeys;

    /// <summary>Key used once <see cref="MaxKeys"/> distinct keys exist.</summary>
    public const string OverflowKey = RateLimitedLogger.OverflowKey;

    // The one table shared by every plugin. Readonly and never replaced; its limits are the defaults.
    private static readonly RateLimitedLogger Shared = new RateLimitedLogger("shared");

    /// <summary>Lines a key may emit back to back before limiting starts: 3. Read-only; for other limits use <see cref="RateLimitedLogger"/>.</summary>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static int Burst => Shared.Burst;

    /// <summary>Time to earn back one line once the burst is used up: 10 s. Read-only; for other limits use <see cref="RateLimitedLogger"/>.</summary>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static TimeSpan RefillInterval => Shared.RefillInterval;

    /// <summary>How often suppressed counts are summed up and logged: 30 s. Read-only; for another interval use <see cref="RateLimitedLogger"/>.</summary>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static TimeSpan SummaryInterval => Shared.SummaryInterval;

    /// <summary>Calls refused since process start on the shared table, across all its keys (summaries do not reset it).</summary>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static long SuppressedTotal => Shared.SuppressedTotal;

    /// <summary>Number of keys currently tracked on the shared table.</summary>
    /// <remarks>Thread affinity: any thread.</remarks>
    public static int KeyCount => Shared.KeyCount;

    /// <summary>Logs at Info level unless the key's bucket is empty.</summary>
    /// <param name="key">Identifies the message source, for example <c>"MyPlugin.voice.parse"</c>. Keep it a constant. Keys are shared by every plugin, so prefix yours.</param>
    /// <param name="message">Builds the text. Evaluated only when the line is allowed; an exception inside it is logged instead of thrown.</param>
    /// <remarks>Thread affinity: any thread. Never throws.</remarks>
    public static void Info(string key, Func<string> message) => Shared.Write(RateLimitedLogger.Level.Info, key, message);

    /// <summary>Logs at Warn level unless the key's bucket is empty.</summary>
    /// <param name="key">Identifies the message source. Keep it a constant.</param>
    /// <param name="message">Builds the text. Evaluated only when the line is allowed.</param>
    /// <remarks>Thread affinity: any thread. Never throws.</remarks>
    public static void Warn(string key, Func<string> message) => Shared.Write(RateLimitedLogger.Level.Warn, key, message);

    /// <summary>Logs at Error level unless the key's bucket is empty.</summary>
    /// <param name="key">Identifies the message source. Keep it a constant.</param>
    /// <param name="message">Builds the text. Evaluated only when the line is allowed.</param>
    /// <remarks>Thread affinity: any thread. Never throws.</remarks>
    public static void Error(string key, Func<string> message) => Shared.Write(RateLimitedLogger.Level.Error, key, message);

    /// <summary>
    /// Logs the "N messages suppressed" line for every key of the shared table that has refused calls
    /// pending, now, regardless of <see cref="SummaryInterval"/>. It only brings a line forward; no
    /// count is lost and no limit changes.
    /// </summary>
    /// <remarks>Thread affinity: any thread. Call it from a shutdown hook so the last counts are not lost.</remarks>
    public static void FlushSuppressed() => Shared.FlushSuppressed();

    /// <summary>
    /// Registers a <see cref="SlClock"/> job that runs <see cref="FlushSuppressed"/> every
    /// <see cref="SummaryInterval"/>, so summaries appear even when no log call comes in.
    /// </summary>
    /// <remarks>
    /// Thread affinity: MAIN (registers a clock job; installs <see cref="SlClock"/> if needed). Idempotent
    /// for the whole process: the first plugin to call it registers the one job.
    /// Without it, summaries still appear on the next allowed line of the key and on the next
    /// call of any key after <see cref="SummaryInterval"/>.
    /// </remarks>
    public static void EnablePeriodicSummary() => Shared.EnablePeriodicSummary();

    /// <summary>Test hook: forgets every key of the shared table. Not public, so no plugin can wipe the others' buckets and suppressed counts.</summary>
    internal static void Reset() => Shared.Reset();
}
