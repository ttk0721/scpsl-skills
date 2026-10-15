using System;
using SlKit;
using SlKit.Core;
using Log = LabApi.Features.Console.Logger; // alias: UnityEngine also has a Logger type

namespace Examples.AddingExternalIntegrations
{
    /// <summary>
    /// The delivery state machine. Every member runs on the main thread: <see cref="SlHttpClient"/> and
    /// <see cref="BackgroundWorker"/> deliver their callbacks there, so no lock or Interlocked is needed here.
    /// Nothing in this class waits for the network.
    /// </summary>
    internal sealed class WebhookSender
    {
        private readonly Outbox _outbox;
        private readonly Func<ReporterConfig> _config;
        private readonly OutboxStore _store;
        private readonly SlHttpClient _http;       // this plugin's own client: limits and headers are not shared with other plugins
        private readonly BackgroundWorker _worker; // this plugin's own thread: a stalled disk here cannot delay another plugin's IO
        private readonly Random _random = new Random();

        // The batch in flight, pinned until it is delivered or dropped. While it is set, entries 0..n-1
        // of the outbox ARE this batch: nothing but RemoveFirst(n) may remove from the head, and retries
        // re-send exactly these entries under exactly this id.
        private OutboxEntry[] _batch;
        private string _batchId;
        private int _attempt;

        private bool _inFlight;
        private long _inFlightSince;
        private int _sendId;            // a late callback of an abandoned send is recognised by this
        private long _notBefore;        // Stopwatch ticks: no send before this
        private bool _stopped;          // misconfigured sender: data kept, nothing sent until re-armed
        private bool _failing;

        public WebhookSender(Outbox outbox, OutboxStore store, Func<ReporterConfig> config, SlHttpClient http, BackgroundWorker worker)
        {
            _outbox = outbox;
            _store = store;
            _config = config;
            _http = http;
            _worker = worker;
        }

        // Counters count ITEMS (entries) except Attempts and FailedAttempts, which count requests, so one
        // stuck batch does not inflate "dropped" on every retry.
        public long Attempts { get; private set; }

        public long FailedAttempts { get; private set; }

        public long Delivered { get; private set; }

        public long Dropped { get; private set; }

        /// <summary>One line for the per-round log summary.</summary>
        public string Describe()
        {
            string state = _stopped ? "stopped" : _inFlight ? "sending" : _notBefore > StopwatchTime.Now ? "waiting" : "idle";
            return "state=" + state + " pending=" + _outbox.Count + " delivered=" + Delivered + " dropped=" + Dropped
                + " expired=" + _outbox.Expired + " overflow=" + _outbox.Overflowed
                + " attempts=" + Attempts + " failed=" + FailedAttempts;
        }

        /// <summary>The operator's way back after fixing the URL: called from <c>LoadConfigs</c>.</summary>
        public void Rearm()
        {
            _stopped = false;
            _notBefore = 0;
        }

        /// <summary>Main thread. Starts at most one request; returns immediately.</summary>
        public void Tick()
        {
            ReporterConfig cfg = _config();
            string url = cfg.WebhookUrl;
            if (!cfg.IsEnabled || string.IsNullOrEmpty(url) || _stopped)
                return;

            long now = StopwatchTime.Now;
            TimeSpan timeout = TimeSpan.FromSeconds(Clamp(cfg.TimeoutSeconds, 1, 30));
            if (_inFlight)
            {
                // The result arrives through MainThread.Post. If that ever fails (PlayerLoop rebuilt by
                // another plugin, queue overflow), a stuck flag would silence the integration for good.
                if (now - _inFlightSince < StopwatchTime.FromTimeSpan(timeout + TimeSpan.FromSeconds(10)))
                    return;

                _sendId++;
                _inFlight = false;
                OnTransient("no callback within the timeout", 0, null, now);
                return;
            }

            if (now < _notBefore)
                return;

            if (_batch == null)
            {
                _outbox.PruneOlderThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ((long)Clamp(cfg.MaxAgeHours, 1, 24 * 30) * 3600));
                if (_outbox.Count == 0)
                    return;

                OutboxEntry[] head = _outbox.Peek(Clamp(cfg.MaxEntriesPerBatch, 1, 10));
                int n = DiscordPayload.CountFitting(head);
                _batch = new OutboxEntry[n];
                Array.Copy(head, _batch, n);
                _batchId = _batch[0].Key + "/" + n;
                _attempt = 0;
            }

            string body = DiscordPayload.BuildBody(_batchId, _batch);
            _attempt++;
            Attempts++;
            _inFlight = true;
            _inFlightSince = now;
            int sendId = ++_sendId;

            // Fire and forget by design: the task never faults, the result comes back on the main thread.
            _ = _http.PostJsonAsync(url, body, result => OnResult(sendId, result), timeout);
        }

        /// <summary>Main thread. Queues a write of the outbox if it changed. A full worker queue just means "next tick".</summary>
        public void PersistIfDirty()
        {
            if (!_outbox.Dirty)
                return;

            OutboxEntry[] snapshot = _outbox.Snapshot();
            long generation = _outbox.NextGeneration();
            bool queued = _worker.TryRun<bool>(
                () =>
                {
                    _store.Write(generation, OutboxStore.Serialize(snapshot)); // worker: snapshot only
                    return true;
                },
                ok => { },
                error =>
                {
                    _outbox.Dirty = true; // try again next tick
                    RateLimitedLog.Warn("webhook.persist", () => "could not write the outbox: " + error.Message);
                });

            if (queued)
                _outbox.Dirty = false;
        }

        /// <summary>
        /// Shutdown only. Synchronous on purpose: the process exits shortly after the event, so a queued
        /// write may never run. A request in flight is NOT awaited: its batch is still in the outbox, so at
        /// worst the next start sends it again.
        /// </summary>
        public void FlushForShutdown()
        {
            string content = OutboxStore.Serialize(_outbox.Snapshot());
            // scpsl-lint: ignore SL007 -- the shutdown flush must finish before the process exits
            _store.Write(_outbox.NextGeneration(), content);
        }

        private void OnResult(int sendId, HttpResult result)
        {
            if (sendId != _sendId)
                return; // an abandoned send answered late: the watchdog already handled that attempt

            _inFlight = false;
            long now = StopwatchTime.Now;

            // Classify by status code, not by Success: since SlKit 0.2 a failed result can still carry the
            // real status (2xx with TooLarge means Discord accepted the batch, so it counts as delivered),
            // and Rejected (the client's own queue is full) has status 0, which takes the retry path.
            switch (DeliveryPolicy.Classify(result.StatusCode))
            {
                case Verdict.Delivered:
                    _outbox.RemoveFirst(_batch.Length);
                    Delivered += _batch.Length;
                    _batch = null;
                    _attempt = 0;
                    _notBefore = 0;
                    if (_failing)
                    {
                        _failing = false;
                        Log.Info("[WebhookReporter] delivery recovered, " + _outbox.Count + " entries pending");
                    }

                    break;

                case Verdict.RetryLater:
                    TimeSpan wait;
                    if (result.StatusCode != 429 || !DeliveryPolicy.TryParseRetryAfter(result.Body, out wait))
                        wait = TimeSpan.Zero;

                    OnTransient(result.StatusCode == 0 ? Redact(result.ToString()) : "HTTP " + result.StatusCode, result.StatusCode, wait == TimeSpan.Zero ? (TimeSpan?)null : wait, now);
                    break;

                case Verdict.DropBatch:
                    FailedAttempts++;
                    Dropped += _batch.Length;
                    _outbox.RemoveFirst(_batch.Length);
                    int droppedNow = _batch.Length;
                    string reason = "HTTP " + result.StatusCode + ": " + Redact(result.Body);
                    RateLimitedLog.Warn("webhook.drop", () => "receiver rejected a batch of " + droppedNow + " entries, dropped. " + reason);
                    _batch = null;
                    _attempt = 0;
                    break;

                default: // StopSender
                    FailedAttempts++;
                    _stopped = true;
                    _batch = null;   // the entries stay in the outbox (deferred) and are re-batched after Rearm
                    _failing = true;
                    Log.Error("[WebhookReporter] HTTP " + result.StatusCode + ": the webhook URL is wrong or revoked. Sending stopped, "
                        + _outbox.Count + " entries kept. Fix webhook_url and run 'labapi reload configs'.");
                    break;
            }
        }

        private void OnTransient(string what, int status, TimeSpan? serverWait, long now)
        {
            FailedAttempts++;
            _failing = true;
            TimeSpan wait = serverWait ?? DeliveryPolicy.Backoff(_attempt, _random.NextDouble());
            int attempt = _attempt;
            if (_attempt >= DeliveryPolicy.MaxAttemptsPerCycle)
            {
                // Bounded: stop hammering a dead receiver. The batch stays pinned and persisted (deferred).
                wait = wait > DeliveryPolicy.CoolDown ? wait : DeliveryPolicy.CoolDown;
                _attempt = 0;
            }

            _notBefore = now + StopwatchTime.FromTimeSpan(wait);
            int seconds = (int)wait.TotalSeconds;
            RateLimitedLog.Warn("webhook.retry", () => what + " (attempt " + attempt + "), retrying in " + seconds + " s");
        }

        // Webhook URLs contain the secret. Exception messages and error bodies can echo a URL back,
        // so nothing derived from a response is logged without passing through here.
        private string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string url = _config().WebhookUrl;
            if (!string.IsNullOrEmpty(url))
                text = text.Replace(url, "[webhook]");

            int k = text.IndexOf("/webhooks/", StringComparison.OrdinalIgnoreCase);
            if (k >= 0)
                text = text.Substring(0, k) + "/webhooks/[redacted]";

            return text.Length > 160 ? text.Substring(0, 160) : text;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
