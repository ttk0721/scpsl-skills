using System;
using System.Globalization;

namespace Examples.AddingExternalIntegrations
{
    /// <summary>What to do with a batch after one HTTP attempt.</summary>
    internal enum Verdict
    {
        /// <summary>2xx: remove the batch from the outbox.</summary>
        Delivered,

        /// <summary>Transient: keep the batch, try again later (deferred).</summary>
        RetryLater,

        /// <summary>The sender itself is misconfigured: keep the data, stop sending until the config is reloaded.</summary>
        StopSender,

        /// <summary>The receiver rejects this payload and will keep rejecting it: remove the batch (dropped).</summary>
        DropBatch,
    }

    /// <summary>
    /// Pure failure policy: no game, no I/O, so it can be tested on plain .NET. Every status class
    /// has exactly one answer, written down here and nowhere else.
    /// </summary>
    internal static class DeliveryPolicy
    {
        /// <summary>Attempts at one batch before the sender cools down for <see cref="CoolDown"/>.</summary>
        public const int MaxAttemptsPerCycle = 6;

        /// <summary>
        /// Pause after a failed cycle. The batch stays in the outbox (deferred), so this is the
        /// rate bound on unbounded retry: at most 6 attempts per 5 minutes while the receiver is down.
        /// </summary>
        public static readonly TimeSpan CoolDown = TimeSpan.FromMinutes(5);

        private const double BaseSeconds = 2.0;
        private const double MaxBackoffSeconds = 60.0;
        private const double MinRetryAfterSeconds = 0.5;
        private const double MaxRetryAfterSeconds = 120.0;

        /// <summary>Maps an HTTP status (0 = no response) to the action for the batch.</summary>
        /// <param name="status">Status code, or 0 for a timeout or transport error.</param>
        /// <returns>The verdict.</returns>
        public static Verdict Classify(int status)
        {
            if (status >= 200 && status < 300)
                return Verdict.Delivered;

            // 0: timeout or network error. The request may still have been processed, which is why
            // the retry must carry the same batch id.
            if (status == 0 || status == 408 || status == 429 || status >= 500)
                return Verdict.RetryLater;

            // 3xx: HttpClient follows redirects itself, so a 3xx that reaches us means the URL is wrong.
            // 401/403/404: wrong, revoked or deleted webhook. Everything we send will fail the same way,
            // so keep the data and stop instead of discarding it batch by batch.
            if (status == 401 || status == 403 || status == 404 || (status >= 300 && status < 400))
                return Verdict.StopSender;

            // 400, 413, 422, ...: the body is the problem. Retrying cannot help, and keeping it would
            // block every later batch behind it.
            if (status >= 400 && status < 500)
                return Verdict.DropBatch;

            // 1xx, negative or anything unforeseen: never guess toward discarding data.
            return Verdict.StopSender;
        }

        /// <summary>Exponential backoff with jitter: 2, 4, 8, 16, 32, 60 s (+-20 %).</summary>
        /// <param name="attempt">1 for the first failed attempt.</param>
        /// <param name="jitter01">A random number in [0, 1).</param>
        /// <returns>The wait before the next attempt.</returns>
        public static TimeSpan Backoff(int attempt, double jitter01)
        {
            double seconds = BaseSeconds * Math.Pow(2.0, Math.Max(0, attempt - 1));
            seconds = Math.Min(MaxBackoffSeconds, seconds) * (0.8 + (0.4 * jitter01));
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Reads <c>"retry_after"</c> (seconds, may be fractional) from a 429 body. The header is not
        /// exposed by SlHttp, and Discord repeats the value in the JSON body.
        /// </summary>
        /// <param name="body">The response body, may be null.</param>
        /// <param name="wait">The wait, clamped to 0.5..120 s.</param>
        /// <returns>True when a value was found.</returns>
        public static bool TryParseRetryAfter(string body, out TimeSpan wait)
        {
            wait = TimeSpan.Zero;
            if (string.IsNullOrEmpty(body))
                return false;

            int key = body.IndexOf("\"retry_after\"", StringComparison.Ordinal);
            if (key < 0)
                return false;

            int i = body.IndexOf(':', key);
            if (i < 0)
                return false;

            i++;
            while (i < body.Length && body[i] == ' ')
                i++;

            int start = i;
            while (i < body.Length && (char.IsDigit(body[i]) || body[i] == '.'))
                i++;

            double seconds;
            if (i == start || !double.TryParse(body.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                return false;

            wait = TimeSpan.FromSeconds(Math.Max(MinRetryAfterSeconds, Math.Min(MaxRetryAfterSeconds, seconds)));
            return true;
        }
    }
}
