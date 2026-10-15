using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// One plugin's HTTP client: its own request timeout, queue timeout, default headers, concurrency
/// limit, queue limit and response size cap, over SlKit's shared connection pool. Created by
/// <see cref="SlHttp.CreateClient"/>.
/// </summary>
/// <remarks>
/// Why bounded: a plain <c>Task.Run</c> per request lets a burst (a webhook per kill in a 40-player
/// round) put dozens of requests on the thread pool and the network at once, and each body read
/// with <c>ReadAsStringAsync</c> can be arbitrarily large. Here at most
/// <see cref="MaxConcurrency"/> requests run, at most <see cref="MaxQueued"/> wait (asynchronously,
/// holding no thread) for up to <see cref="QueueTimeout"/>, anything beyond is refused at once, and
/// a body is read only up to <see cref="MaxResponseBytes"/>. The two waits are timed separately, so
/// the result says whether the client's own backlog (<see cref="HttpResult.QueueTimedOut"/>, never
/// sent) or the endpoint (<see cref="HttpResult.TimedOut"/>) was slow. Every outcome is an <see cref="HttpResult"/>; the returned task
/// never faults. Never call <c>.Result</c> or <c>.Wait()</c> on it from the main thread.
/// The client is immutable after creation and holds no thread or socket of its own.
/// Thread affinity: every member may be called from any thread; callbacks run on the main thread.
/// </remarks>
public sealed class SlHttpClient
{
    private readonly AdmissionLimit _admission;
    private readonly HttpRequestRunner _runner;

    internal SlHttpClient(string name, SlHttpOptions options)
    {
        HttpOptionsValidator.Validate(options);

        Name = name;
        Timeout = options.Timeout;
        QueueTimeout = options.QueueTimeout;
        MaxConcurrency = options.MaxConcurrency;
        MaxQueued = options.MaxQueued;
        MaxResponseBytes = Math.Min(options.MaxResponseBytes, BoundedRead.MaxLimit);
        BaseAddress = options.BaseAddress;
        _admission = new AdmissionLimit((long)MaxConcurrency + MaxQueued); // long: an int.MaxValue queue must not wrap negative
        _runner = new HttpRequestRunner(Name, MaxConcurrency, QueueTimeout, HttpDefaultHeaders.Copy(options.DefaultHeaders));
        SlHttp.AddConcurrency(MaxConcurrency);
    }

    /// <summary>Owner name given at creation. Thread affinity: any thread.</summary>
    public string Name { get; }

    /// <summary>Default request timeout, counted from getting a slot. Thread affinity: any thread.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Longest wait for a concurrency slot (see <see cref="SlHttpOptions.QueueTimeout"/>). Thread affinity: any thread.</summary>
    public TimeSpan QueueTimeout { get; }

    /// <summary>Most requests on the wire at once. Thread affinity: any thread.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Most requests waiting for a slot. Thread affinity: any thread.</summary>
    public int MaxQueued { get; }

    /// <summary>Default body limit in bytes. Thread affinity: any thread.</summary>
    public long MaxResponseBytes { get; }

    /// <summary>Base for relative URLs, or null. Thread affinity: any thread.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Requests on the wire now. Thread affinity: any thread.</summary>
    public int InFlightCount => _runner.InFlightCount;

    /// <summary>Requests accepted and not finished (running plus waiting). Thread affinity: any thread.</summary>
    public int OutstandingCount => _admission.Count;

    /// <summary>Requests refused because the client was full. Thread affinity: any thread.</summary>
    public long RejectedCount => _admission.RefusedCount;

    /// <summary>Requests that gave up waiting for a slot (<see cref="HttpResult.QueueTimedOut"/>), never sent. Thread affinity: any thread.</summary>
    public long QueueTimedOutCount => _runner.QueueTimedOutCount;

    /// <summary>
    /// GETs <paramref name="url"/> as a string. Thread affinity: any thread; returns immediately;
    /// <paramref name="onMainThread"/> runs on the main thread.
    /// </summary>
    /// <param name="url">Absolute URL, or relative to <see cref="BaseAddress"/>.</param>
    /// <param name="onMainThread">Optional; receives the result on the main thread.</param>
    /// <param name="timeout">Optional request timeout for this call, counted from getting a slot; <see cref="Timeout"/> when null. The queue wait is limited by <see cref="QueueTimeout"/>.</param>
    /// <param name="cancellationToken">Optional cancellation (reported as an error result).</param>
    /// <param name="maxResponseBytes">Optional per-request body limit; <see cref="MaxResponseBytes"/> when null.</param>
    /// <returns>A task that completes with the same result (never faults). Do not block on it.</returns>
    public Task<HttpResult> GetStringAsync(string url, Action<HttpResult> onMainThread = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, long? maxResponseBytes = null)
    {
        if (url == null)
        {
            throw new ArgumentNullException(nameof(url));
        }

        return Start(() => CreateMessage(HttpMethod.Get, url), onMainThread, timeout, cancellationToken, maxResponseBytes);
    }

    /// <summary>
    /// POSTs an already-serialised JSON string (<c>application/json; charset=utf-8</c>).
    /// Serialise on a worker from a snapshot if the payload is large.
    /// Thread affinity: any thread; returns immediately; <paramref name="onMainThread"/> runs on the main thread.
    /// </summary>
    /// <param name="url">Absolute URL, or relative to <see cref="BaseAddress"/>.</param>
    /// <param name="json">The JSON body.</param>
    /// <param name="onMainThread">Optional; receives the result on the main thread.</param>
    /// <param name="timeout">Optional request timeout for this call, counted from getting a slot; <see cref="Timeout"/> when null. The queue wait is limited by <see cref="QueueTimeout"/>.</param>
    /// <param name="cancellationToken">Optional cancellation (reported as an error result).</param>
    /// <param name="maxResponseBytes">Optional per-request body limit; <see cref="MaxResponseBytes"/> when null.</param>
    /// <returns>A task that completes with the same result (never faults). Do not block on it.</returns>
    public Task<HttpResult> PostJsonAsync(string url, string json, Action<HttpResult> onMainThread = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, long? maxResponseBytes = null)
    {
        if (url == null)
        {
            throw new ArgumentNullException(nameof(url));
        }

        if (json == null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        return Start(
            () =>
            {
                HttpRequestMessage message = CreateMessage(HttpMethod.Post, url);
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return message;
            },
            onMainThread,
            timeout,
            cancellationToken,
            maxResponseBytes);
    }

    /// <summary>
    /// Sends a request built by <paramref name="createRequest"/> (any method, body or per-request
    /// header). The factory runs on a pool thread and must build a new message each call; the
    /// client's <see cref="SlHttpOptions.DefaultHeaders"/> are added unless the request already has them.
    /// Thread affinity: any thread; returns immediately; <paramref name="onMainThread"/> runs on the main thread.
    /// </summary>
    /// <param name="createRequest">Builds the request; must not touch game state (it runs off the main thread).</param>
    /// <param name="onMainThread">Optional; receives the result on the main thread.</param>
    /// <param name="timeout">Optional request timeout for this call, counted from getting a slot; <see cref="Timeout"/> when null. The queue wait is limited by <see cref="QueueTimeout"/>.</param>
    /// <param name="cancellationToken">Optional cancellation (reported as an error result).</param>
    /// <param name="maxResponseBytes">Optional per-request body limit; <see cref="MaxResponseBytes"/> when null.</param>
    /// <returns>A task that completes with the same result (never faults). Do not block on it.</returns>
    public Task<HttpResult> SendAsync(Func<HttpRequestMessage> createRequest, Action<HttpResult> onMainThread = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, long? maxResponseBytes = null)
    {
        if (createRequest == null)
        {
            throw new ArgumentNullException(nameof(createRequest));
        }

        return Start(createRequest, onMainThread, timeout, cancellationToken, maxResponseBytes);
    }

    private Task<HttpResult> Start(Func<HttpRequestMessage> createRequest, Action<HttpResult> onMainThread, TimeSpan? timeout, CancellationToken cancellationToken, long? maxResponseBytes)
    {
        if (maxResponseBytes.HasValue && maxResponseBytes.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes), "The body limit must not be negative.");
        }

        long limit = Math.Min(maxResponseBytes ?? MaxResponseBytes, BoundedRead.MaxLimit);
        TimeSpan effective = timeout ?? Timeout;

        if (onMainThread != null)
        {
            MainThread.EnsureCallbacksDrained("SlHttp", Name);
        }

        // Admission on the caller's thread: a full client refuses at once instead of growing a
        // backlog of tasks that would all time out anyway.
        if (!_admission.TryEnter())
        {
            HttpResult refused = new HttpResult(0, null, new InvalidOperationException(
                "SlHttp client '" + Name + "' is full: " + MaxConcurrency + " requests running and " + MaxQueued + " waiting."),
                TimeSpan.Zero, TimeSpan.Zero, sent: false, rejected: true);
            if (onMainThread != null)
            {
                MainThread.Post(() => onMainThread(refused));
            }

            return Task.FromResult(refused);
        }

        // Task.Run keeps the synchronous start of the handler (DNS, connect) off the caller's thread.
        return Task.Run(async () =>
        {
            HttpResult result;
            try
            {
                result = await _runner.RunAsync(createRequest, effective, cancellationToken, limit).ConfigureAwait(false);
            }
            finally
            {
                _admission.Leave();
            }

            if (onMainThread != null)
            {
                MainThread.Post(() => onMainThread(result));
            }

            return result;
        });
    }

    private HttpRequestMessage CreateMessage(HttpMethod method, string url)
    {
        return BaseAddress == null
            ? new HttpRequestMessage(method, url)
            : new HttpRequestMessage(method, new Uri(BaseAddress, url));
    }
}
