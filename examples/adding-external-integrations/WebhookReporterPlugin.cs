using System;
using System.ComponentModel;
using System.IO;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using SlKit;
using SlKit.Core;
using Log = LabApi.Features.Console.Logger; // alias: UnityEngine also has a Logger type

namespace Examples.AddingExternalIntegrations
{
    /// <summary>Config: public properties only. The webhook URL is a secret: keep this file out of version control.</summary>
    public sealed class ReporterConfig
    {
        [Description("Kill switch. Read at use time, so 'labapi reload configs' applies it. When false nothing is queued or sent.")]
        public bool IsEnabled { get; set; } = true;

        [Description("Discord webhook URL. Empty disables the plugin. It contains a secret: it is never logged.")]
        public string WebhookUrl { get; set; } = string.Empty;

        [Description("Seconds between delivery ticks. Also the batching window.")]
        public int FlushSeconds { get; set; } = 5;

        [Description("Entries per message (1-10). One request per tick at most, far below Discord's webhook rate limit.")]
        public int MaxEntriesPerBatch { get; set; } = 10;

        [Description("HTTP timeout in seconds (1-30).")]
        public int TimeoutSeconds { get; set; } = 8;

        [Description("Outbox capacity. When full, new entries are refused and counted.")]
        public int MaxQueued { get; set; } = 500;

        [Description("Entries older than this many hours are discarded instead of sent late.")]
        public int MaxAgeHours { get; set; } = 24;

        [Description("Entries accepted per round. A ban storm cannot flood the channel; the rest is counted.")]
        public int MaxEventsPerRound { get; set; } = 100;

        [Description("Text put in front of every line, for example the server name. May be empty.")]
        public string ServerLabel { get; set; } = string.Empty;
    }

    /// <summary>
    /// Staff feed: round start and end, bans and kicks go to a Discord webhook. The point is the
    /// integration pattern, not the events: snapshot on the main thread, deliver through SlHttp, confirm
    /// before forgetting, survive outages and restarts, never block a frame.
    /// </summary>
    public sealed class WebhookReporterPlugin : Plugin<ReporterConfig>
    {
        private readonly Outbox _outbox = new Outbox();
        private OutboxStore _store;
        private WebhookSender _sender;
        private SlHttpClient _http;
        private SlKit.BackgroundWorker _worker; // qualified: System.ComponentModel (for [Description]) has a BackgroundWorker too
        private IDisposable _tickJob;
        private bool _enabled;
        private long _nextTick;
        private int _roundEvents;       // per-round: reset at WaitingForPlayers
        private int _roundSuppressed;   // per-round: reset at WaitingForPlayers

        public override string Name => "WebhookReporter";
        public override string Description => "Recipe: reliable webhook delivery with a persisted outbox.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);

        // A constant: LabApiProperties.CompiledVersion is the running server's version, so it can never fail.
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        // An empty or comment-only config.yml deserialises to null.
        private ReporterConfig Cfg => Config ??= new ReporterConfig();

        public override void LoadConfigs()
        {
            base.LoadConfigs();
            // A reload is the operator's way to re-arm a sender that stopped on 401/403/404.
            // The sender is null before Enable, and nothing created by Enable is touched here.
            _sender?.Rearm();
        }

        public override void Enable()
        {
            if (_enabled)
                return; // EXILED's reload can call Enable() twice for a plugin it loaded

            try
            {
                // Fallible work first, subscriptions last.
                SlClock.Install();
                _store = new OutboxStore(Path.Combine(this.GetConfigDirectory().FullName, "outbox.tsv"));
                // Own client and own worker, created once (both live for the whole process). The shared
                // defaults would queue this plugin's outbox write behind every other plugin's IO. One
                // request at a time is all this sender ever starts. The 1 MiB response cap stays: a
                // Discord reply is tiny, and a larger body comes back as TooLarge with the real status.
                _http = SlHttp.CreateClient(Name, new SlHttpOptions { MaxConcurrency = 1, MaxQueued = 4 });
                _worker = Background.ForOwner(Name, 8);
                _sender = new WebhookSender(_outbox, _store, () => Cfg, _http, _worker);

                // Read at Enable, once: a few KB at most. Reports that did not get out before the last
                // stop are sent now; the age limit drops anything that went stale in the meantime.
                Exception readError;
                _outbox.Restore(_store.Read(out readError), Cfg.MaxQueued);
                if (readError != null)
                    Log.Warn("[WebhookReporter] could not read the outbox, starting empty: " + readError.Message);

                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.Shutdown += OnShutdown;
                ServerEvents.RoundStarted += OnRoundStarted;
                ServerEvents.RoundEnded += OnRoundEnded;
                PlayerEvents.Banned += OnBanned;
                PlayerEvents.Kicked += OnKicked;

                // One job for everything periodic. It runs every second and decides at use time whether
                // a flush is due, so FlushSeconds follows 'labapi reload configs'.
                _tickJob = SlClock.Every(TimeSpan.FromSeconds(1), Tick, "webhook.tick");
                _enabled = true;
            }
            catch
            {
                Teardown(); // undo partial subscriptions, then let the loader report the failure
                throw;
            }
        }

        // Never called by LabAPI or the game. Kept correct; nothing depends on it.
        public override void Disable() => Teardown();

        private void Teardown()
        {
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.RoundEnded -= OnRoundEnded;
            PlayerEvents.Banned -= OnBanned;
            PlayerEvents.Kicked -= OnKicked;
            _tickJob?.Dispose();
            _tickJob = null;
            _enabled = false;
        }

        private void OnWaitingForPlayers()
        {
            // New scene: every Player of the last round is gone, and this plugin holds none. The outbox
            // is NOT cleared: it is the one thing that must outlive rounds. Only per-round counters reset.
            try
            {
                int suppressed = _roundSuppressed;
                _roundEvents = 0;
                _roundSuppressed = 0;

                // After the reset, or the cap would swallow its own summary line.
                if (suppressed > 0)
                    Report("Event cap reached last round, " + suppressed + " events not reported.");

                // One line per round, not per event: state is visible without a status command.
                if (_sender != null && Cfg.IsEnabled)
                    Log.Info("[WebhookReporter] " + _sender.Describe());
            }
            catch (Exception e)
            {
                Log.Error("[WebhookReporter] WaitingForPlayers: " + e);
            }
        }

        private void OnRoundStarted()
        {
            try
            {
                Report("Round started, " + Server.PlayerCount + " players.");
            }
            catch (Exception e)
            {
                Log.Error("[WebhookReporter] RoundStarted: " + e);
            }
        }

        private void OnRoundEnded(RoundEndedEventArgs ev)
        {
            try
            {
                Report("Round ended, leading team: " + ev.LeadingTeam + ".");
            }
            catch (Exception e)
            {
                Log.Error("[WebhookReporter] RoundEnded: " + e);
            }
        }

        private void OnBanned(PlayerBannedEventArgs ev)
        {
            try
            {
                // Snapshot NOW, on the main thread: Player is null for an offline ban, and no wrapper is
                // kept past this method. Only strings go on to the outbox.
                string target = ev.Player != null ? ev.Player.Nickname : "(offline)";
                string issuer = ev.Issuer != null ? ev.Issuer.Nickname : "(console)";
                Report("Ban: " + DiscordPayload.Code(target, 48) + " (" + DiscordPayload.Code(ev.PlayerId, 40) + ") by "
                    + DiscordPayload.Code(issuer, 48) + " for " + FormatDuration(ev.Duration)
                    + ", reason: " + DiscordPayload.OneLine(ev.Reason, 160));
            }
            catch (Exception e)
            {
                Log.Error("[WebhookReporter] Banned: " + e);
            }
        }

        private void OnKicked(PlayerKickedEventArgs ev)
        {
            try
            {
                Report("Kick: " + DiscordPayload.Code(ev.Player != null ? ev.Player.Nickname : "(unknown)", 48) + " by "
                    + DiscordPayload.Code(ev.Issuer != null ? ev.Issuer.Nickname : "(console)", 48)
                    + ", reason: " + DiscordPayload.OneLine(ev.Reason, 160));
            }
            catch (Exception e)
            {
                Log.Error("[WebhookReporter] Kicked: " + e);
            }
        }

        // The only way into the outbox. Cheap: a counter check, one string, one list add. No I/O.
        private void Report(string text)
        {
            ReporterConfig cfg = Cfg;
            if (!cfg.IsEnabled || string.IsNullOrEmpty(cfg.WebhookUrl))
                return;

            if (++_roundEvents > cfg.MaxEventsPerRound)
            {
                _roundSuppressed++;
                return;
            }

            string line = DiscordPayload.OneLine(string.IsNullOrEmpty(cfg.ServerLabel) ? text : cfg.ServerLabel + ": " + text, DiscordPayload.MaxLineChars);
            _outbox.TryAdd(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), line, Math.Max(1, cfg.MaxQueued));
        }

        private void Tick()
        {
            // The job runs at 1 Hz; delivery and persistence run at FlushSeconds.
            long now = StopwatchTime.Now;
            if (now < _nextTick)
                return;

            _nextTick = now + StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(Math.Max(1, Cfg.FlushSeconds)));
            _sender.Tick();
            _sender.PersistIfDirty();
        }

        private void OnShutdown()
        {
            // The only reliable last call, and the process exits right after it: write synchronously.
            try
            {
                _sender?.FlushForShutdown();
            }
            catch (Exception e)
            {
                Log.Error("[WebhookReporter] Shutdown flush: " + e.Message);
            }

            Teardown();
        }

        private static string FormatDuration(long seconds)
        {
            if (seconds <= 0)
                return "0 s";

            if (seconds >= 86400)
                return (seconds / 86400) + " d";

            if (seconds >= 3600)
                return (seconds / 3600) + " h";

            return seconds >= 60 ? (seconds / 60) + " min" : seconds + " s";
        }
    }
}
