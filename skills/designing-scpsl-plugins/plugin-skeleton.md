# Plugin skeleton (designing-scpsl-plugins)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Original example code, not compiled as
part of this repository: build it against the server's `SCPSL_Data/Managed` assemblies and
SlKit (see deploying-scpsl-plugins).

A session tracker that shows every design decision from `design-checklist.md` in one place:
literal API version, config read at use time with a null guard, idempotent `Enable()` with
rollback, `UserId` keys, one owner for round state, one clock, a main-thread snapshot for HTTP,
a synchronous shutdown flush, and a kill switch.

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using SlKit;
using Log = LabApi.Features.Console.Logger; // alias: UnityEngine also has a Logger type

public sealed class TrackerConfig
{
    [Description("Master kill switch. Read at use time, so 'labapi reload configs' applies it.")]
    public bool Enabled { get; set; } = true;

    [Description("Seconds between uploads.")]
    public float FlushSeconds { get; set; } = 30f;

    [Description("Upload endpoint. Empty disables uploads.")]
    public string WebhookUrl { get; set; } = "";

    [Description("Verbose logging. Keep off on production.")]
    public bool Debug { get; set; } = false;
}

public sealed class TrackerPlugin : Plugin<TrackerConfig>
{
    public override string Name => "Tracker";                 // constant: it names the config folder
    public override string Description => "Session tracker";
    public override string Author => "you";
    public override Version RequiredApiVersion => new Version(1, 1, 7); // literal, never CompiledVersion

    private sealed class Session
    {
        public string UserId;
        public string Nickname;
        public long JoinedAt;      // Stopwatch ticks
        public string LastRole;    // cached on ChangedRole, still readable in Left
    }

    // One owner for this state. Keyed by UserId; no Player or ReferenceHub is stored.
    private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(64);
    private readonly StringBuilder _json = new StringBuilder(4096);
    private IDisposable _tickJob;
    private SlHttpClient _http;    // this plugin's own client: its slots, timeouts and headers are not shared
    private long _nextFlush;
    private int _failedUploads;    // a failed upload is state, not just a log line
    private bool _sinkBroken;      // permanent failure (bad or deleted webhook): stop until reload
    private bool _enabled;

    // An empty or comment-only config.yml deserialises to null.
    private TrackerConfig Cfg => Config ??= new TrackerConfig();

    public override void LoadConfigs()
    {
        base.LoadConfigs();                    // replaces Config with a new object
        TrackerConfig c = Cfg;
        if (c.FlushSeconds < 5f) c.FlushSeconds = 5f;
        _sinkBroken = false;                   // a reload is the operator's way to re-arm uploads
        // Rebuild anything derived from Config here. Do not touch state that Enable() creates:
        // this also runs before Enable() and for plugins that never enabled.
    }

    public override void Enable()
    {
        if (_enabled) return;                  // EXILED's reload can call Enable() twice
        try
        {
            // Fallible work first. No world access here: Enable() runs before the map exists.
            SlClock.Install();      // main thread only; also ends ClockScope.Round jobs at RoundRestarted
            // One request in flight is enough for one upload per period; a backlog is refused, not queued.
            _http ??= SlHttp.CreateClient(Name, new SlHttpOptions { MaxConcurrency = 1, MaxQueued = 2 });

            // Subscribe and register last.
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.Shutdown += OnShutdown;
            PlayerEvents.Joined += OnJoined;
            PlayerEvents.Left += OnLeft;
            PlayerEvents.ChangedRole += OnChangedRole;
            _tickJob = SlClock.Every(TimeSpan.FromSeconds(1), Tick, "tracker.tick");
            _enabled = true;
        }
        catch
        {
            Teardown();                        // undo partial subscriptions...
            throw;                             // ...and let the loader report the failure
        }
    }

    // Never called by LabAPI or the game. Kept correct, but nothing depends on it.
    public override void Disable() => Teardown();

    private void Teardown()
    {
        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
        ServerEvents.Shutdown -= OnShutdown;
        PlayerEvents.Joined -= OnJoined;
        PlayerEvents.Left -= OnLeft;
        PlayerEvents.ChangedRole -= OnChangedRole;
        _tickJob?.Dispose();
        _tickJob = null;
        _enabled = false;
    }

    private void OnWaitingForPlayers()
    {
        // New world: every Player of the last round is gone. Per-round state is reset here.
        // _sessions survives on purpose: sessions span restarts and are keyed by UserId.
    }

    private void OnJoined(PlayerJoinedEventArgs ev)
    {
        try
        {
            Player p = ev.Player;
            if (p.IsDummy || string.IsNullOrEmpty(p.UserId)) return;
            // Joined fires again after every round restart: resume, do not reset.
            if (!_sessions.TryGetValue(p.UserId, out Session s))
            {
                s = new Session { UserId = p.UserId, JoinedAt = Stopwatch.GetTimestamp() };
                _sessions[p.UserId] = s;
            }
            s.Nickname = p.Nickname;
        }
        catch (Exception e) { Log.Error("[Tracker] Joined: " + e); } // LabAPI's own log names the wrong handler
    }

    private void OnChangedRole(PlayerChangedRoleEventArgs ev)
    {
        try
        {
            Player p = ev.Player;
            if (p.UserId != null && _sessions.TryGetValue(p.UserId, out Session s))
                s.LastRole = p.Role.ToString();
        }
        catch (Exception e) { Log.Error("[Tracker] ChangedRole: " + e); }
    }

    private void OnLeft(PlayerLeftEventArgs ev)
    {
        try
        {
            // Left fires for every hub during a restart's scene teardown. That is not a disconnect.
            // (A real disconnect inside the same window looks identical: sessions kept here are
            // resumed by Joined, and a real tracker would reconcile against Player.ReadyList.)
            if (RoundRestarting.RoundRestart.IsRoundRestarting) return;
            string id = ev.Player.UserId;
            if (id != null) _sessions.Remove(id); // a real tracker would queue a "left" record here
        }
        catch (Exception e) { Log.Error("[Tracker] Left: " + e); }
    }

    // One 1 Hz job; the flush period is read from Config at use time.
    private void Tick()
    {
        TrackerConfig cfg = Cfg;
        if (!cfg.Enabled || cfg.WebhookUrl.Length == 0 || _sinkBroken) return;   // kill switch
        long now = Stopwatch.GetTimestamp();
        if (now < _nextFlush) return;
        _nextFlush = now + (long)(cfg.FlushSeconds * Stopwatch.Frequency);

        // Snapshot on the main thread: plain strings only leave this method.
        _json.Clear().Append("{\"online\":[");
        bool first = true;
        foreach (Session s in _sessions.Values)
        {
            if (!first) _json.Append(',');
            first = false;
            AppendJsonString(_json, s.UserId);
        }
        _json.Append("],\"failed_uploads\":").Append(_failedUploads).Append('}');

        // Off the main thread; the callback runs on the main thread. Never .Result/.Wait() it.
        _http.PostJsonAsync(cfg.WebhookUrl, _json.ToString(), r =>
        {
            if (r.Success) return;
            _failedUploads++;
            // Classify, do not retry blindly. 0 (no response, or never sent: Rejected/QueueTimedOut),
            // timeouts, 5xx and 429 are transient: the next flush sends a fresh snapshot. Other 4xx
            // cannot succeed on retry. A failed result can carry a real status, so key on StatusCode.
            if (r.StatusCode >= 400 && r.StatusCode < 500 && r.StatusCode != 429 && !_sinkBroken)
            {
                _sinkBroken = true;
                Log.Warn("[Tracker] webhook rejected with HTTP " + r.StatusCode + ", uploads stopped until config reload");
            }
        });

        if (cfg.Debug) Log.Info("[Tracker] flushed " + _sessions.Count); // flag checked before building
    }

    private void OnShutdown()
    {
        // Synchronous: the process quits shortly after this event. No async work here.
        try
        {
            string path = Path.Combine(this.GetConfigDirectory().FullName, "sessions.txt");
            // scpsl-lint: ignore SL007 -- shutdown flush must finish before the process exits
            File.WriteAllLines(path, _sessions.Keys);
        }
        catch (Exception e) { Log.Error("[Tracker] Shutdown flush: " + e); }
    }

    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s ?? string.Empty)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); // tabs, newlines, ...
            else sb.Append(c);
        }
        sb.Append('"');
    }
}
```

## What each part prevents

| Part | Prevents |
|---|---|
| `new Version(1, 1, 7)` | a version check that can never fire (`CompiledVersion` is the running server's version) |
| `Cfg => Config ??= ...` and reading `Cfg` in `Tick` | a `NullReferenceException` on an empty `config.yml`; stale values after `labapi reload configs` |
| `_enabled` guard, subscribe last, `Teardown(); throw;` | double subscriptions on a second `Enable()`; half-subscribed plugin after a failed `Enable()`; a hidden failure |
| `UserId` keys, no `Player` fields | references to destroyed hubs after a restart; recycled `PlayerId` mix-ups |
| `IsRoundRestarting` check in `Left` | every session closed as "left" at each restart (derived from the lifecycle trace, not observed; a real disconnect inside the restart window is missed) |
| one `SlClock.Every` job | per-player coroutines; idle-mode stalls of MEC Update-segment waits; a throw that kills the timer |
| snapshot then the plugin's own `SlHttpClient` | game objects touched off-thread; main-thread waits; another plugin's backlog or headers leaking into yours |
| `_failedUploads` | silent data loss on a failed request |
| status classes, `_sinkBroken` | endless retries against a deleted webhook; a failure policy whose names ("dropped") disagree with what the code does. For retried items keep a pending flag and call them deferred until abandoned (checklist item 6) |
| `ServerEvents.Shutdown` flush | data that only `Disable()` would have saved (it is never called) |
| `cfg.Debug` before the string is built | paying for a stack walk and two regexes per line on production |

## Re-verifying on a new build

- `PlayerJoinedEventArgs`, `PlayerLeftEventArgs`, `PlayerChangedRoleEventArgs` still expose
  `Player`; `Player.UserId`, `Nickname`, `Role`, `IsDummy` still exist.
- `ServerEvents.WaitingForPlayers` and `ServerEvents.Shutdown` are still argument-less
  `LabEventHandler` events.
- `Plugin<TConfig>.Config` still has a public setter and `LoadConfigs()` is still overridable.
- `RoundRestarting.RoundRestart.IsRoundRestarting` is still a public static and is still true
  while the old scene's hubs are destroyed.
- `this.GetConfigDirectory()` (extension in `LabApi.Loader`) still exists.
- SlKit: `SlClock.Install`, `Every`, `SlHttp.CreateClient`,
  `SlHttpClient.PostJsonAsync` and `HttpResult.Success` signatures in its README.
