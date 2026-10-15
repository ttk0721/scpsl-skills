using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// Non-blocking HTTP for plugins. <see cref="CreateClient"/> gives each plugin its own
/// <see cref="SlHttpClient"/> (timeouts, headers, concurrency limit, response size cap); the static
/// methods are a convenience that uses a shared default client.
/// </summary>
/// <remarks>
/// Synchronous HTTP on the main thread freezes the whole server for the duration of the request
/// (every frame of the game runs on that one thread). Requests start on a pool thread (so even
/// DNS resolution or connection setup inside the handler cannot run on the caller's thread), use
/// <c>ConfigureAwait(false)</c> throughout, and never throw: all outcomes arrive as an
/// <see cref="HttpResult"/>, through the returned task and optionally on the main thread.
/// All clients send through one internal <see cref="HttpClient"/> and its handler, so connections
/// are reused and no socket is exhausted, but nothing mutable is shared: there is no public
/// <c>HttpClient</c> whose headers or base address one plugin could change for everybody.
/// Per-host connections: on the game's runtime the handler is the managed socket handler, whose
/// per-host limit is "unlimited" and which does not consult <c>ServicePointManager</c>; each
/// client's <c>MaxConcurrency</c> is the real limit. SlKit checks this when the transport is
/// created, raises a lower limit (never lowers one), and logs the result once
/// (<see cref="TransportDescription"/>).
/// Thread affinity: every member may be called from any thread; callbacks run on the main thread.
/// </remarks>
public static class SlHttp
{
    /// <summary>Name of the shared default client.</summary>
    public const string DefaultName = "default";

    // Hosts whose ServicePoint limit was already raised, with the limit asked for. Used only on a
    // WebRequest-backed handler (see TransportInfo.ServicePointMode).
    private static readonly ConcurrentDictionary<string, int> RaisedHosts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static int _totalConcurrency;

    // The one transport and what was found out about it, fixed when the type is initialised.
    private static readonly TransportInfo Info = CreateTransport();

    // Its own timeout is infinite: every request carries its own token. Never exposed, so no
    // caller can mutate DefaultRequestHeaders/BaseAddress for everyone.
    internal static readonly HttpClient Transport = Info.Client;

    private static readonly SlHttpClient DefaultClient = new SlHttpClient(DefaultName, new SlHttpOptions());

    /// <summary>
    /// The shared default client used by the static methods (default <see cref="SlHttpOptions"/>).
    /// Its concurrency slots are shared by every plugin that uses the static methods, so a plugin
    /// with regular traffic should <see cref="CreateClient"/> its own. Thread affinity: any thread.
    /// </summary>
    public static SlHttpClient Default => DefaultClient;

    /// <summary>Timeout of the shared default client (10 s). Read-only since 0.2.0. Thread affinity: any thread.</summary>
    [Obsolete("SlHttp.DefaultTimeout is read-only since SlKit 0.2.0. Set SlHttpOptions.Timeout on your own client from SlHttp.CreateClient, or pass a timeout per request.")]
    public static TimeSpan DefaultTimeout => DefaultClient.Timeout;

    /// <summary>
    /// Per-host connection limit of the shared transport after SlKit applied its policy.
    /// <see cref="ConnectionLimit.Unlimited"/> (<c>int.MaxValue</c>) means the transport adds no limit
    /// of its own, which is the case on the game's runtime; -1 means the runtime's handler is backed by
    /// <c>HttpWebRequest</c>, where the limit is per <c>ServicePoint</c> and SlKit raises it per host on
    /// first use. Thread affinity: any thread.
    /// </summary>
    public static int TransportConnectionLimit => Info.Limit;

    /// <summary>
    /// One line describing the shared transport and the per-host connection limit in force, as
    /// logged once when the transport was created. Thread affinity: any thread.
    /// </summary>
    public static string TransportDescription => Info.Description;

    /// <summary>
    /// Creates a client for one plugin (or one integration of a plugin) with its own request
    /// timeout, queue timeout, default headers, concurrency limit, queue limit and response size
    /// cap. The options are copied, so changing them later does nothing. Create it once (in
    /// <c>Enable</c>) and keep it; a client holds no thread and no socket of its own.
    /// Thread affinity: any thread.
    /// </summary>
    /// <param name="name">Owner name for diagnostics, normally the plugin name.</param>
    /// <param name="options">Settings; null means the defaults.</param>
    /// <returns>A new client.</returns>
    public static SlHttpClient CreateClient(string name, SlHttpOptions options = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A client needs an owner name.", nameof(name));
        }

        return new SlHttpClient(name, options ?? new SlHttpOptions());
    }

    /// <summary>
    /// GETs <paramref name="url"/> as a string through the shared default client (see
    /// <see cref="SlHttpClient.GetStringAsync"/>). Thread affinity: any thread; returns immediately;
    /// <paramref name="onMainThread"/> runs on the main thread.
    /// </summary>
    /// <param name="url">Absolute URL.</param>
    /// <param name="onMainThread">Optional; receives the result on the main thread.</param>
    /// <param name="timeout">Optional per-request timeout (from getting a slot); the client's when null.</param>
    /// <param name="cancellationToken">Optional cancellation (reported as an error result).</param>
    /// <param name="maxResponseBytes">Optional per-request body limit; the client's (1 MiB) when null.</param>
    /// <returns>A task that completes with the same result (never faults). Do not block on it.</returns>
    public static Task<HttpResult> GetStringAsync(string url, Action<HttpResult> onMainThread = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, long? maxResponseBytes = null)
        => DefaultClient.GetStringAsync(url, onMainThread, timeout, cancellationToken, maxResponseBytes);

    /// <summary>
    /// POSTs an already-serialised JSON string through the shared default client (see
    /// <see cref="SlHttpClient.PostJsonAsync"/>). Thread affinity: any thread; returns immediately;
    /// <paramref name="onMainThread"/> runs on the main thread.
    /// </summary>
    /// <param name="url">Absolute URL.</param>
    /// <param name="json">The JSON body.</param>
    /// <param name="onMainThread">Optional; receives the result on the main thread.</param>
    /// <param name="timeout">Optional per-request timeout (from getting a slot); the client's when null.</param>
    /// <param name="cancellationToken">Optional cancellation (reported as an error result).</param>
    /// <param name="maxResponseBytes">Optional per-request body limit; the client's (1 MiB) when null.</param>
    /// <returns>A task that completes with the same result (never faults). Do not block on it.</returns>
    public static Task<HttpResult> PostJsonAsync(string url, string json, Action<HttpResult> onMainThread = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, long? maxResponseBytes = null)
        => DefaultClient.PostJsonAsync(url, json, onMainThread, timeout, cancellationToken, maxResponseBytes);

    /// <summary>Records a new client's concurrency, so per-host limits (WebRequest-backed handlers only) cover every client. Any thread.</summary>
    internal static void AddConcurrency(int maxConcurrency)
    {
        int seen = Volatile.Read(ref _totalConcurrency);
        while (true)
        {
            int updated = ConnectionLimit.SaturatingAdd(seen, maxConcurrency);
            int actual = Interlocked.CompareExchange(ref _totalConcurrency, updated, seen);
            if (actual == seen)
            {
                return;
            }

            seen = actual;
        }
    }

    /// <summary>
    /// Before a request is sent: on a WebRequest-backed handler, raises the per-host
    /// <c>ServicePoint</c> limit of <paramref name="uri"/>'s host to the total concurrency of all
    /// clients (never lowers it), once per host and size. On the managed handler (the game's) this
    /// is one field read. Runs on a pool thread; never throws.
    /// </summary>
    internal static void EnsureHostLimit(Uri uri)
    {
        if (!Info.ServicePointMode || uri == null || !uri.IsAbsoluteUri)
        {
            return;
        }

        int wanted = Math.Max(1, Volatile.Read(ref _totalConcurrency));
        string host = uri.Scheme + "://" + uri.Authority;
        if (RaisedHosts.TryGetValue(host, out int done) && done >= wanted)
        {
            return;
        }

        try
        {
#pragma warning disable SYSLIB0014 // obsolete on modern .NET only; this is the Mono WebRequest path
            ServicePoint point = ServicePointManager.FindServicePoint(uri);
#pragma warning restore SYSLIB0014
            int before = point.ConnectionLimit;
            int applied = ConnectionLimit.Raise(before, wanted);
            if (applied != before)
            {
                point.ConnectionLimit = applied;
            }

            RaisedHosts[host] = applied;
            SlLog.Info("SlHttp: per-host connection limit for " + host + " is " + ConnectionLimit.Describe(applied)
                + (applied != before ? " (raised from " + before + ")" : " (already enough)")
                + " to cover " + wanted + " concurrent SlHttp requests.");
        }
        catch (Exception e)
        {
            RaisedHosts[host] = wanted; // do not retry and log on every request
            SlLog.Warn("SlHttp could not check the per-host connection limit for " + host + ": " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static TransportInfo CreateTransport()
    {
        HttpClientHandler handler = new HttpClientHandler();
        int limit;
        bool servicePointMode = false;
        string description;
        try
        {
            // The game's System.Net.Http builds HttpClientHandler on its managed socket handler: the
            // limit lives in the handler (default int.MaxValue) and ServicePointManager (2 per host on
            // Mono) is never consulted. A handler with a lower default is raised here, on SlKit's own
            // handler only, so nothing outside SlKit changes.
            int before = handler.MaxConnectionsPerServer;
            int applied = ConnectionLimit.Raise(before, ConnectionLimit.Unlimited);
            if (applied != before)
            {
                handler.MaxConnectionsPerServer = applied;
            }

            limit = handler.MaxConnectionsPerServer;
            description = "SlHttp transport: per-host connection limit " + ConnectionLimit.Describe(limit)
                + (applied != before ? " (raised from " + before + ")" : string.Empty)
                + ", set on the handler (ServicePointManager does not apply to it); each client's MaxConcurrency is the effective limit.";
        }
        catch (Exception e) when (e is NotImplementedException || e is NotSupportedException || e is PlatformNotSupportedException)
        {
            // A handler backed by HttpWebRequest has no handler-level limit; ServicePointManager decides
            // per host. Raise it per host on first use instead of changing DefaultConnectionLimit,
            // which would affect every WebRequest in the process.
            servicePointMode = true;
            limit = -1;
#pragma warning disable SYSLIB0014
            int processDefault = ServicePointManager.DefaultConnectionLimit;
#pragma warning restore SYSLIB0014
            description = "SlHttp transport: WebRequest-backed handler, ServicePointManager.DefaultConnectionLimit is "
                + processDefault + " per host; SlKit raises the limit of each host it calls to the total "
                + "MaxConcurrency of its clients (never lowers it).";
        }

        SlLog.Info(description);
        HttpClient client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        return new TransportInfo(client, limit, servicePointMode, description);
    }

    /// <summary>The shared transport with the per-host connection policy SlKit found for it.</summary>
    private sealed class TransportInfo
    {
        internal TransportInfo(HttpClient client, int limit, bool servicePointMode, string description)
        {
            Client = client;
            Limit = limit;
            ServicePointMode = servicePointMode;
            Description = description;
        }

        internal HttpClient Client { get; }

        /// <summary>The handler's per-host limit, or -1 when it has none (see <see cref="ServicePointMode"/>).</summary>
        internal int Limit { get; }

        /// <summary>True when the handler is backed by <c>HttpWebRequest</c>, so the limit lives in <c>ServicePointManager</c>.</summary>
        internal bool ServicePointMode { get; }

        internal string Description { get; }
    }
}
