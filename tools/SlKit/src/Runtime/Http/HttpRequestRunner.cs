using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// Sends the requests of one <see cref="SlHttpClient"/> in two timed phases: a wait for one of its concurrency slots
/// (under the client's queue timeout only), then the request itself (under the request timeout, counted from the slot),
/// with the body read up to a size limit. Every outcome becomes an <see cref="HttpResult"/>; nothing is thrown.
/// Thread affinity: any thread.
/// </summary>
internal sealed class HttpRequestRunner
{
    private static readonly Action<object> DisposeState = DisposeQuietly;

    private readonly string _name;
    private readonly int _maxConcurrency;
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _queueWait;
    private readonly HttpDefaultHeaders _headers;
    private int _inFlight;
    private long _queueTimedOut;

    internal HttpRequestRunner(string name, int maxConcurrency, TimeSpan queueTimeout, HttpDefaultHeaders headers)
    {
        _name = name;
        _maxConcurrency = maxConcurrency;
        _queueWait = ClampTimeout(queueTimeout, allowZero: true);
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _headers = headers;
    }

    /// <summary>Requests on the wire now.</summary>
    internal int InFlightCount => Volatile.Read(ref _inFlight);

    /// <summary>Requests that gave up waiting for a slot, never sent.</summary>
    internal long QueueTimedOutCount => Interlocked.Read(ref _queueTimedOut);

    internal async Task<HttpResult> RunAsync(Func<HttpRequestMessage> createRequest, TimeSpan timeout, CancellationToken cancellationToken, long limit)
    {
        long started = StopwatchTime.Now;
        long acquiredAt = 0; // stays 0 until a concurrency slot is held
        CancellationTokenSource timeoutSource = null;
        CancellationTokenSource linked = null;
        bool sent = false;
        int status = 0;
        try
        {
            // Phase 1: a concurrency slot, under the client's queue limit only. The request timeout
            // must not run here: a request that times out while queued was never sent, and reporting
            // it as TimedOut would blame an endpoint that never saw it. WaitAsync(timeout, token)
            // returns false on timeout (no token source needed) and completes synchronously, without
            // allocating, when a slot is free.
            if (!await _slots.WaitAsync(_queueWait, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _queueTimedOut);
                TimeSpan waited = Elapsed(started);
                return new HttpResult(0, null, new TimeoutException(
                        "SlHttp client '" + _name + "' had no free slot within " + (int)waited.TotalMilliseconds + " ms ("
                        + _maxConcurrency + " requests running); the request was not sent."),
                    waited, waited, sent: false, queueTimedOut: true);
            }

            acquiredAt = StopwatchTime.Now;
            Interlocked.Increment(ref _inFlight);

            // Phase 2: the request, under the request timeout counted from here. Creating the token
            // sources sits inside the try as well: the contract is "never faults", and a bad timeout
            // or an already disposed caller token source must become a result, not an exception.
            timeoutSource = new CancellationTokenSource(ClampTimeout(timeout, allowZero: false));
            linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
            CancellationToken token = linked.Token;

            using (HttpRequestMessage request = createRequest())
            {
                if (request == null)
                {
                    throw new InvalidOperationException("The request factory returned null.");
                }

                _headers.AddTo(request);
                SlHttp.EnsureHostLimit(request.RequestUri);

                // Headers only: the body is read below under the size limit, never buffered whole.
                sent = true;
                using (HttpResponseMessage response = await SlHttp.Transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    status = (int)response.StatusCode;
                    HttpContent content = response.Content;
                    if (content == null)
                    {
                        return new HttpResult(status, string.Empty, null, Elapsed(started), QueueTime(started, acquiredAt), sent: true);
                    }

                    long? declared = content.Headers.ContentLength;
                    // A HEAD response declares the length of a body it does not send, so the header
                    // alone must not fail it; the read below then sees an empty stream.
                    if (declared.HasValue && declared.Value > limit && request.Method != HttpMethod.Head)
                    {
                        return TooLarge(status, limit, declared, started, acquiredAt);
                    }

                    using (Stream stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (token.Register(DisposeState, stream))
                    {
                        // Not every stream honours the token in ReadAsync; disposing it on cancel ends the read.
                        BoundedReadResult read = await BoundedRead.ReadAsync(stream, limit, token).ConfigureAwait(false);
                        if (read.Exceeded)
                        {
                            return TooLarge(status, limit, null, started, acquiredAt);
                        }

                        string body = BoundedRead.Decode(read.Buffer, read.Count, content.Headers.ContentType?.CharSet);
                        return new HttpResult(status, body, null, Elapsed(started), QueueTime(started, acquiredAt), sent: true);
                    }
                }
            }
        }
        catch (Exception e)
        {
            // Only the request phase has a timeout source, so a timeout here was on the wire (or in
            // the factory, just before it): TimedOut never describes a request still in the queue.
            bool timedOut = timeoutSource != null && timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            return new HttpResult(status, null, e, Elapsed(started), QueueTime(started, acquiredAt), sent, timedOut: timedOut);
        }
        finally
        {
            if (acquiredAt != 0)
            {
                Interlocked.Decrement(ref _inFlight);
                _slots.Release();
            }

            linked?.Dispose();
            timeoutSource?.Dispose();
        }
    }

    private HttpResult TooLarge(int status, long limit, long? declared, long started, long acquiredAt)
    {
        string detail = declared.HasValue
            ? "declares " + declared.Value + " bytes"
            : "is longer";
        return new HttpResult(
            status,
            null,
            new HttpRequestException("Response body " + detail + " than the " + limit + "-byte limit of SlHttp client '" + _name + "'."),
            Elapsed(started),
            QueueTime(started, acquiredAt),
            sent: true,
            tooLarge: true);
    }

    /// <summary>Wait for a slot: up to <paramref name="acquiredAt"/>, or everything so far when no slot was acquired.</summary>
    private static TimeSpan QueueTime(long started, long acquiredAt)
        => acquiredAt == 0 ? Elapsed(started) : StopwatchTime.ToTimeSpan(acquiredAt - started);

    /// <summary>
    /// <see cref="CancellationTokenSource"/> and <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
    /// accept only -1 ms (infinite) or 0..int.MaxValue ms and throw otherwise; clamp instead so a
    /// zero, negative or huge timeout is not a crash. A request timeout of zero becomes 1 ms; a queue
    /// wait of zero stays zero ("do not wait").
    /// </summary>
    private static TimeSpan ClampTimeout(TimeSpan timeout, bool allowZero)
    {
        if (timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            return timeout;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return allowZero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(1);
        }

        return timeout.TotalMilliseconds >= int.MaxValue ? TimeSpan.FromMilliseconds(int.MaxValue) : timeout;
    }

    private static TimeSpan Elapsed(long started) => StopwatchTime.ToTimeSpan(StopwatchTime.Now - started);

    private static void DisposeQuietly(object state)
    {
        try
        {
            ((IDisposable)state).Dispose();
        }
        catch (Exception)
        {
            // Runs inside the token's cancel callback; the read it interrupts reports the outcome.
        }
    }
}
