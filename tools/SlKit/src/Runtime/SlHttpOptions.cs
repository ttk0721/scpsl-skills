using System;
using System.Collections.Generic;

namespace SlKit;

/// <summary>Settings for <see cref="SlHttp.CreateClient"/>. Copied when the client is created.</summary>
/// <remarks>Thread affinity: a plain mutable object; fill it on one thread, then hand it over.</remarks>
public sealed class SlHttpOptions
{
    /// <summary>Default <see cref="MaxResponseBytes"/>: 1 MiB.</summary>
    public const long DefaultMaxResponseBytes = 1024 * 1024;

    /// <summary>Default <see cref="QueueTimeout"/>: 30 s.</summary>
    public static readonly TimeSpan DefaultQueueTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Request timeout, counted from the moment the request gets a concurrency slot and is sent
    /// (default 10 s). It does NOT include the wait for a slot, which <see cref="QueueTimeout"/>
    /// limits. Must be positive, or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.
    /// When it elapses the result has <see cref="HttpResult.TimedOut"/>.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Longest wait for a concurrency slot (default 30 s). A request still waiting then is not sent:
    /// it completes with <see cref="HttpResult.QueueTimedOut"/>. <see cref="TimeSpan.Zero"/> means
    /// "never wait" (no free slot ends the request at once); <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// means no limit (the wait is then still bounded by <see cref="MaxQueued"/> and the running
    /// requests' <see cref="Timeout"/>). A request can take up to <c>QueueTimeout + Timeout</c> in total.
    /// </summary>
    public TimeSpan QueueTimeout { get; set; } = DefaultQueueTimeout;

    /// <summary>Most requests of this client on the wire at once (default 4, minimum 1).</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// Most requests that may wait for a slot (default 64, minimum 0). A request beyond
    /// <see cref="MaxConcurrency"/> + <see cref="MaxQueued"/> is not sent: it completes at once with
    /// <see cref="HttpResult.Rejected"/>.
    /// </summary>
    public int MaxQueued { get; set; } = 64;

    /// <summary>
    /// Largest response body read, in bytes (default 1 MiB, minimum 0). A larger body ends the
    /// request with <see cref="HttpResult.TooLarge"/> after reading at most this many bytes plus one.
    /// </summary>
    public long MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

    /// <summary>Optional base for relative URLs passed to the client's methods.</summary>
    public Uri BaseAddress { get; set; }

    /// <summary>
    /// Request headers sent with every request of this client only (for example
    /// <c>Authorization</c> or <c>User-Agent</c>). Content headers such as <c>Content-Type</c> are
    /// not allowed here. A header set on the request itself (through <see cref="SlHttpClient.SendAsync"/>) wins.
    /// </summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
