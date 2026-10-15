using System;

namespace SlKit;

/// <summary>Outcome of one <see cref="SlHttpClient"/> request. Never thrown, always delivered.</summary>
/// <remarks>
/// A request goes through two phases, and the result says which one ended it: first it waits for
/// one of the client's concurrency slots (limited by <see cref="SlHttpClient.QueueTimeout"/>), then
/// it is sent (limited by <see cref="SlHttpClient.Timeout"/>). <see cref="QueueTimedOut"/> and
/// <see cref="TimedOut"/> are never both true, and <see cref="Sent"/> tells whether the endpoint
/// can have seen the request at all, which decides whether a non-idempotent call may be retried.
/// Thread affinity: immutable, any thread.
/// </remarks>
public sealed class HttpResult
{
    internal HttpResult(
        int statusCode,
        string body,
        Exception error,
        TimeSpan elapsed,
        TimeSpan queueTime,
        bool sent,
        bool timedOut = false,
        bool queueTimedOut = false,
        bool tooLarge = false,
        bool rejected = false)
    {
        StatusCode = statusCode;
        Body = body;
        Error = error;
        Elapsed = elapsed;
        QueueTime = queueTime;
        Sent = sent;
        TimedOut = timedOut;
        QueueTimedOut = queueTimedOut;
        TooLarge = tooLarge;
        Rejected = rejected;
    }

    /// <summary>True for a 2xx response whose body was read completely.</summary>
    public bool Success => Error == null && StatusCode >= 200 && StatusCode < 300;

    /// <summary>
    /// HTTP status code, or 0 when no response headers arrived (network error, timeout, cancel,
    /// rejection, queue timeout). Set together with <see cref="Error"/> when the headers arrived but
    /// the body failed or was too large.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>Response body as text (also for non-2xx responses), or null when no complete body arrived.</summary>
    public string Body { get; }

    /// <summary>The failure, or null when a complete response arrived. Describes it; it was never thrown at you.</summary>
    public Exception Error { get; }

    /// <summary>
    /// True when the request was SENT and then abandoned because the request timeout elapsed
    /// (counted from the moment it got a concurrency slot): the endpoint or the network was slow.
    /// A request that timed out while still waiting for a slot reports <see cref="QueueTimedOut"/> instead.
    /// </summary>
    public bool TimedOut { get; }

    /// <summary>
    /// True when the request never got a concurrency slot within the client's
    /// <see cref="SlHttpClient.QueueTimeout"/>: it was NOT sent, so the endpoint never saw it. The
    /// cause is this client's own backlog (too many requests for its <c>MaxConcurrency</c>), not the endpoint.
    /// </summary>
    public bool QueueTimedOut { get; }

    /// <summary>
    /// True when the request was handed to the transport, so the endpoint may have received it
    /// (even when the result is a failure). False for <see cref="Rejected"/>, <see cref="QueueTimedOut"/>,
    /// a cancel while waiting for a slot, and a request factory that threw: those were never sent
    /// and are always safe to retry.
    /// </summary>
    public bool Sent { get; }

    /// <summary>
    /// True when the response body exceeded the client's or the request's byte limit. The body is
    /// then null: a truncated document is never handed to a parser.
    /// </summary>
    public bool TooLarge { get; }

    /// <summary>
    /// True when the request was refused without being sent, because the client already had
    /// <see cref="SlHttpClient.MaxConcurrency"/> requests running and <see cref="SlHttpClient.MaxQueued"/> waiting.
    /// </summary>
    public bool Rejected { get; }

    /// <summary>Wall-clock duration from the start of the request to its outcome: <see cref="QueueTime"/> plus the time on the wire.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Time spent waiting for a concurrency slot (zero when one was free at once).</summary>
    public TimeSpan QueueTime { get; }

    /// <summary>Time from getting a slot to the outcome (<see cref="Elapsed"/> minus <see cref="QueueTime"/>); zero when never sent.</summary>
    public TimeSpan RequestTime => Sent ? Elapsed - QueueTime : TimeSpan.Zero;

    /// <summary>Short description for logs.</summary>
    /// <returns>Status, timing (with the queue wait when there was one) and error summary.</returns>
    public override string ToString()
    {
        int ms = (int)Elapsed.TotalMilliseconds;
        int queuedMs = (int)QueueTime.TotalMilliseconds;
        string queued = queuedMs > 0 ? " (" + queuedMs + " ms of it waiting for a slot)" : string.Empty;
        if (Rejected)
        {
            return "rejected, not sent: too many requests in flight";
        }

        if (QueueTimedOut)
        {
            return "not sent: no free slot within " + queuedMs + " ms (client backlog, not the endpoint)";
        }

        // Here the number is the wire time only, so the queue wait is added, not "of it".
        if (TimedOut)
        {
            return "timeout after " + (int)RequestTime.TotalMilliseconds + " ms on the wire"
                + (queuedMs > 0 ? " (after " + queuedMs + " ms waiting for a slot)" : string.Empty);
        }

        if (TooLarge)
        {
            return "HTTP " + StatusCode + ", body over the size limit, after " + ms + " ms" + queued;
        }

        if (Error != null)
        {
            return (Sent ? "error after " : "error before sending, after ") + ms + " ms" + queued + ": " + Error.GetBaseException().Message;
        }

        return "HTTP " + StatusCode + " in " + ms + " ms" + queued;
    }
}
