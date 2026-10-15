using System;

namespace SlKit.Core;

/// <summary>
/// The rule SlHttp follows for the per-host connection limit of its shared transport: raise it to
/// what SlKit needs, never lower it.
/// </summary>
/// <remarks>
/// SlHttp bounds traffic with each client's own <c>MaxConcurrency</c>. A tighter per-host limit
/// underneath (the 2 connections of <c>ServicePointManager.DefaultConnectionLimit</c> on the
/// WebRequest path) would silently turn "4 at once" into "2 at once" and make plugins calling the
/// same host wait on each other. Lowering a limit someone else raised would break them instead, so
/// the only allowed change is upwards. Stateless; thread affinity: any thread.
/// </remarks>
public static class ConnectionLimit
{
    /// <summary>The value that means "no per-host limit" (what the managed handler defaults to).</summary>
    public const int Unlimited = int.MaxValue;

    /// <summary>The limit to apply: <paramref name="current"/> when it already allows <paramref name="wanted"/>, else <paramref name="wanted"/>.</summary>
    /// <param name="current">The limit in force now.</param>
    /// <param name="wanted">The limit SlKit needs; values below 1 count as 1.</param>
    /// <returns>The larger of the two. Never below <paramref name="current"/>.</returns>
    public static int Raise(int current, int wanted) => Math.Max(current, Math.Max(1, wanted));

    /// <summary>Adds per-client concurrency limits without overflowing into a negative "limit".</summary>
    /// <param name="total">Running total.</param>
    /// <param name="add">A client's <c>MaxConcurrency</c>.</param>
    /// <returns>The sum, saturated at <see cref="Unlimited"/>.</returns>
    public static int SaturatingAdd(int total, int add)
    {
        long sum = (long)Math.Max(0, total) + Math.Max(0, add);
        return sum >= Unlimited ? Unlimited : (int)sum;
    }

    /// <summary>Formats a limit for a log line: a number, or "unlimited".</summary>
    /// <param name="limit">The limit.</param>
    /// <returns>The text.</returns>
    public static string Describe(int limit) => limit >= Unlimited ? "unlimited" : limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
