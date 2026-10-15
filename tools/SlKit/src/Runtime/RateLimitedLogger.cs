using System;
using System.Collections.Generic;
using System.Threading;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// A rate-limited log that belongs to one plugin: its own keys, its own limits, its own counters.
/// Create it once (in <c>Enable</c>) and keep it in a field.
/// </summary>
/// <remarks>
/// Why an instance: <see cref="RateLimitedLog"/> shares one key table and fixed limits between every
/// plugin on the server, because SlKit is one DLL. A plugin that wants other limits, or wants its
/// keys kept apart from everybody else's (same key text, own bucket; a full key table of its own,
/// not a shared one), creates a logger. Nothing a logger does reaches another logger or the shared
/// <see cref="RateLimitedLog"/> table, including <see cref="Reset"/>.
/// Each key allows <see cref="Burst"/> lines at once and then one per <see cref="RefillInterval"/>.
/// The message is a <see cref="Func{TResult}"/> that runs only when the line is allowed, so a refused
/// call costs a counter increment and builds no string. Refused calls are counted and reported as
/// "N messages suppressed" the next time the key gets a line, and by a periodic sweep
/// (<see cref="SummaryInterval"/>) piggybacked on any call, <see cref="EnablePeriodicSummary"/> or
/// <see cref="FlushSuppressed"/>.
/// Thread affinity: any thread. All state sits behind one short lock that is never held while
/// logging or running the factory. The factory runs on the calling thread, so it must follow that
/// thread's rules (game objects: main thread only). The steady-state path allocates nothing.
/// </remarks>
public sealed class RateLimitedLogger
{
    /// <summary>Most distinct keys tracked per logger. Further keys share the <see cref="OverflowKey"/> bucket, so dynamic keys cannot leak memory.</summary>
    public const int MaxKeys = 512;

    /// <summary>Key used once <see cref="MaxKeys"/> distinct keys exist.</summary>
    public const string OverflowKey = "(other)";

    /// <summary>Default <see cref="Burst"/>: 3 lines.</summary>
    public const int DefaultBurst = 3;

    /// <summary>Default <see cref="RefillInterval"/>: 10 s.</summary>
    public static readonly TimeSpan DefaultRefillInterval = TimeSpan.FromSeconds(10);

    /// <summary>Default <see cref="SummaryInterval"/>: 30 s.</summary>
    public static readonly TimeSpan DefaultSummaryInterval = TimeSpan.FromSeconds(30);

    internal enum Level
    {
        Info,
        Warn,
        Error,
    }
    private readonly RateLimitKeyTable _table;
    private long _suppressedTotal;
    private int _periodicEnabled;

    /// <summary>Creates a logger with its own limits.</summary>
    /// <param name="name">Owner name, normally the plugin name; used for the periodic summary's clock job and in diagnostics. Not added to log lines.</param>
    /// <param name="burst">Lines a key may emit back to back before limiting starts; at least 1.</param>
    /// <param name="refillInterval">Time to earn back one line once the burst is used up; null for <see cref="DefaultRefillInterval"/>; at least 1 ms.</param>
    /// <param name="summaryInterval">How often suppressed counts are summed up and logged; null for <see cref="DefaultSummaryInterval"/>; at least 1 s.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is below its minimum. Nothing is clamped silently.</exception>
    public RateLimitedLogger(string name, int burst = DefaultBurst, TimeSpan? refillInterval = null, TimeSpan? summaryInterval = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A logger needs an owner name.", nameof(name));
        }

        if (burst < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(burst), "Burst must be at least 1.");
        }

        TimeSpan refill = refillInterval ?? DefaultRefillInterval;
        if (refill < TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(refillInterval), "The refill interval must be at least 1 ms.");
        }

        TimeSpan summary = summaryInterval ?? DefaultSummaryInterval;
        if (summary < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(summaryInterval), "The summary interval must be at least 1 s.");
        }

        Name = name;
        Burst = burst;
        RefillInterval = refill;
        SummaryInterval = summary;
        _table = new RateLimitKeyTable(burst, 1.0 / refill.TotalSeconds, StopwatchTime.FromTimeSpan(summary));
    }

    /// <summary>The owner name given at creation. Thread affinity: any thread.</summary>
    public string Name { get; }

    /// <summary>Lines a key may emit back to back before limiting starts. Fixed at creation. Thread affinity: any thread.</summary>
    public int Burst { get; }

    /// <summary>Time to earn back one line once the burst is used up. Fixed at creation. Thread affinity: any thread.</summary>
    public TimeSpan RefillInterval { get; }

    /// <summary>How often suppressed counts are summed up and logged. Fixed at creation. Thread affinity: any thread.</summary>
    public TimeSpan SummaryInterval { get; }

    /// <summary>Calls refused since creation, across this logger's keys (summaries do not reset it). Thread affinity: any thread.</summary>
    public long SuppressedTotal => Interlocked.Read(ref _suppressedTotal);

    /// <summary>Number of keys currently tracked by this logger. Thread affinity: any thread.</summary>
    public int KeyCount => _table.Count;

    /// <summary>Logs at Info level unless the key's bucket is empty.</summary>
    /// <param name="key">Identifies the message source, for example <c>"voice.parse"</c>. Keep it a constant.</param>
    /// <param name="message">Builds the text. Evaluated only when the line is allowed; an exception inside it is logged instead of thrown.</param>
    /// <remarks>Thread affinity: any thread. Never throws.</remarks>
    public void Info(string key, Func<string> message) => Write(Level.Info, key, message);

    /// <summary>Logs at Warn level unless the key's bucket is empty.</summary>
    /// <param name="key">Identifies the message source. Keep it a constant.</param>
    /// <param name="message">Builds the text. Evaluated only when the line is allowed.</param>
    /// <remarks>Thread affinity: any thread. Never throws.</remarks>
    public void Warn(string key, Func<string> message) => Write(Level.Warn, key, message);

    /// <summary>Logs at Error level unless the key's bucket is empty.</summary>
    /// <param name="key">Identifies the message source. Keep it a constant.</param>
    /// <param name="message">Builds the text. Evaluated only when the line is allowed.</param>
    /// <remarks>Thread affinity: any thread. Never throws.</remarks>
    public void Error(string key, Func<string> message) => Write(Level.Error, key, message);

    /// <summary>
    /// Logs the "N messages suppressed" line for every key of this logger that has refused calls
    /// pending, now, regardless of <see cref="SummaryInterval"/>.
    /// </summary>
    /// <remarks>Thread affinity: any thread. Call it from a shutdown hook so the last counts are not lost.</remarks>
    public void FlushSuppressed()
    {
        Sweep(StopwatchTime.Now);
    }

    /// <summary>
    /// Registers a <see cref="SlClock"/> job that runs <see cref="FlushSuppressed"/> every
    /// <see cref="SummaryInterval"/>, so summaries appear even when no log call comes in.
    /// </summary>
    /// <remarks>
    /// Thread affinity: MAIN (registers a clock job; installs <see cref="SlClock"/> if needed). Idempotent.
    /// Without it, summaries still appear on the next allowed line of the key and on the next
    /// call of any key of this logger after <see cref="SummaryInterval"/>.
    /// </remarks>
    public void EnablePeriodicSummary()
    {
        if (Interlocked.Exchange(ref _periodicEnabled, 1) != 0)
        {
            return;
        }

        try
        {
            SlClock.Every(SummaryInterval, FlushSuppressed, "ratelimitedlog.summary." + Name);
        }
        catch (Exception)
        {
            // Registration failed (for example off the main thread): allow a retry.
            Volatile.Write(ref _periodicEnabled, 0);
            throw;
        }
    }

    /// <summary>Forgets every key of THIS logger and its bucket. Pending suppressed counts of those keys are dropped. Other loggers and <see cref="RateLimitedLog"/> are not touched.</summary>
    /// <remarks>Thread affinity: any thread.</remarks>
    public void Reset() => _table.Clear();

    internal void Write(Level level, string key, Func<string> message)
    {
        if (message == null)
        {
            return;
        }

        if (key == null)
        {
            key = string.Empty;
        }

        long now = StopwatchTime.Now;
        bool allowed = _table.TryTake(ref key, level, now, out long pending);

        if (!allowed)
        {
            Interlocked.Increment(ref _suppressedTotal);
        }
        else
        {
            // Outside the lock: logging and the factory can be slow, and the factory may call back in here.
            if (pending > 0)
            {
                LogLineEmitter.Emit(level, FormatSuppressed(key, pending));
            }

            string text;
            try
            {
                text = message() ?? string.Empty;
            }
            catch (Exception e)
            {
                LogLineEmitter.Emit(Level.Warn, "[" + key + "] message factory threw " + e.GetType().Name + ": " + e.Message);
                text = null;
            }

            if (text != null)
            {
                LogLineEmitter.Emit(level, "[" + key + "] " + text);
            }
        }

        // Piggyback: a busy key keeps every key's summaries flowing without a timer.
        if (_table.IsSweepDue(now))
        {
            Sweep(now);
        }
    }

    private void Sweep(long now)
    {
        List<RateLimitKeyTable.Suppressed> pending = _table.TakeSuppressed(now);
        if (pending == null)
        {
            return;
        }

        foreach (RateLimitKeyTable.Suppressed s in pending)
        {
            LogLineEmitter.Emit(s.Level, FormatSuppressed(s.Key, s.Count));
        }
    }

    private static string FormatSuppressed(string key, long count)
    {
        return "[" + key + "] " + count + (count == 1 ? " message" : " messages") + " suppressed";
    }
}
