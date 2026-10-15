using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Hints;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Wrappers;
using SlKit;

namespace Examples.AddingPlayerHud
{
    /// <summary>
    /// The one owner of the hint slot for this plugin. Events only change data; this class is the
    /// only place that calls SendHint, once per player per visit at most.
    /// </summary>
    public sealed class HudService
    {
        private sealed class PlayerHud
        {
            public int Kills;
            public int Deaths;
            public string Sent;     // last text we sent (null = never)
            public long SentAt;     // Stopwatch ticks of that send
            public int Epoch;       // re-assert generation this player was last sent at
        }

        private readonly Func<HudConfig> _config;

        // Round state, keyed by UserId. Never a Player or ReferenceHub: the round restart
        // destroys every hub, and a PlayerId can be reused by someone else.
        private readonly Dictionary<string, PlayerHud> _players = new Dictionary<string, PlayerHud>(64);

        // A preference, not round state, so it survives WaitingForPlayers. Bounded by the number of
        // distinct players who ever hid the HUD on this server run.
        private readonly HashSet<string> _hidden = new HashSet<string>();

        private readonly List<Player> _scratch = new List<Player>(64);
        private readonly StringBuilder _sb = new StringBuilder(128);
        private IDisposable _job;
        private long _roundStart;   // Stopwatch ticks, 0 = no round running

        // Coexistence with other hint writers (hud-coexistence.md). Events only arm a deadline;
        // the first visit after it bumps _epoch, and every player whose Epoch is behind is re-sent
        // by his own staggered visit. Nothing is sent from an event and there is no timer per player.
        private long _reassertAt;   // Stopwatch ticks, 0 = nothing pending
        private int _epoch;

        public HudService(Func<HudConfig> config) => _config = config;

        public void Start()
        {
            SlClock.Install();
            // One visit per player per period, spread over the frames. After a round restart or a
            // mass join every state is brand new in the same frame, so without this the first
            // keep-alives would all land together and stay aligned.
            _job = SlClock.EveryStaggered<Player>(
                TimeSpan.FromSeconds(_config().RefreshSeconds), Refill, Visit, "adding-player-hud.visit");
        }

        public void Stop()
        {
            _job?.Dispose();
            _job = null;
            _players.Clear();
        }

        // ---- data only: nothing below this line up to Visit sends a hint ----------------------

        public void OnRoundStarted()
        {
            _roundStart = Stopwatch.GetTimestamp();
            ArmReassert();
        }

        // ChangedRole is the last signal of a role change and also covers spawn waves and the
        // Spectator step of a death. It can fire dozens of times in one frame at round start:
        // arming is one comparison, so that is fine.
        public void OnRoleChanged() => ArmReassert();

        public void OnWaitingForPlayers()
        {
            _players.Clear();
            _roundStart = 0;
            _reassertAt = 0;
        }

        // First event of a burst sets the deadline; later events inside the window do not move it,
        // so the re-send latency stays bounded however long the burst lasts.
        private void ArmReassert()
        {
            HudConfig cfg = _config();
            if (!cfg.IsEnabled || !cfg.ReassertAfterSpawn || _reassertAt != 0) return;
            _reassertAt = Stopwatch.GetTimestamp() + (long)(cfg.ReassertDelaySeconds * Stopwatch.Frequency);
        }

        public void OnLeft(PlayerLeftEventArgs ev)
        {
            string id = ev.Player?.UserId;
            if (!string.IsNullOrEmpty(id)) _players.Remove(id);
        }

        public void OnDeath(PlayerDeathEventArgs ev)
        {
            HudConfig cfg = _config();
            if (!cfg.IsEnabled) return;

            Player victim = ev.Player;
            Player killer = ev.Attacker;
            if (victim == null || victim.IsDummy) return;

            Get(victim.UserId).Deaths++;

            if (killer == null || killer.IsDummy || killer.IsHost) return;
            if (killer.PlayerId == victim.PlayerId) return;                          // suicide
            // Team kill by Faction. Custom-role plugins can change factions, so ask which ones are installed.
            if (!cfg.CountTeamKills && killer.Faction == victim.Faction) return;
            Get(killer.UserId).Kills++;
        }

        public void SetHidden(string userId, bool hidden)
        {
            if (string.IsNullOrEmpty(userId)) return;
            if (hidden) _hidden.Add(userId);
            else _hidden.Remove(userId);
        }

        private PlayerHud Get(string userId)
        {
            if (!_players.TryGetValue(userId ?? "", out PlayerHud hud))
                _players[userId ?? ""] = hud = new PlayerHud();
            return hud;
        }

        // ---- the only sender --------------------------------------------------------------------

        private IReadOnlyList<Player> Refill()
        {
            // Called once per period, after the previous period's visits are done.
            _scratch.Clear();
            if (!_config().IsEnabled) return _scratch;       // kill switch: nobody to visit
            foreach (Player p in Player.ReadyList)           // authenticated players and dummies; never the host
                if (!p.IsDummy) _scratch.Add(p);
            return _scratch;
        }

        private void Visit(Player p)
        {
            if (p.IsDestroyed) return;                       // the list is a snapshot
            HudConfig cfg = _config();
            string id = p.UserId;
            if (!cfg.IsEnabled || string.IsNullOrEmpty(id)) return;

            // A hidden HUD sends nothing. The last hint expires on its own within HintDurationSeconds;
            // an empty hint would cost a message and clear the single slot, other plugins' hints included.
            if (_hidden.Contains(id)) return;

            PlayerHud hud = Get(id);
            bool timer = cfg.ShowRoundTimer && _roundStart != 0;
            string text = Build(cfg, hud, timer);
            if (text.Length == 0) return;

            long now = Stopwatch.GetTimestamp();
            if (_reassertAt != 0 && now >= _reassertAt)
            {
                _epoch++;           // the burst is over: every player is re-sent once, at his own visit
                _reassertAt = 0;
            }

            bool changed = !string.Equals(text, hud.Sent, StringComparison.Ordinal);
            // NOT suppressed by the change check: there is one hint slot per player, so vanilla
            // (item warnings) and other plugins replace this hint, and an unchanged string is exactly
            // what hides that. Identical text resent every 0.5 s does not flicker (verified, client); the keep-alive period is unverified.
            bool keepAlive = now - hud.SentAt >= (long)(cfg.KeepAliveSeconds * Stopwatch.Frequency);
            // Same: an unchanged string must still go out once after a spawn burst.
            bool reassert = hud.Epoch != _epoch;
            if (!changed && !keepAlive && !reassert) return;

            // The game drops hints for a connection while it re-creates pickups after a role
            // change. Do not record such a send as done: the next visit tries again.
            if (HintDisplay.SuppressedReceivers.Contains(p.Connection)) return;

            hud.Sent = text;
            hud.SentAt = now;
            hud.Epoch = _epoch;
            try
            {
                if (timer)
                {
                    // The age is recomputed from OUR start tick on every send. Offset 0 would restart
                    // the clock at 00:00 on each keep-alive. The text holds "{0}" and stays identical,
                    // so the change check above keeps working while the client does the ticking.
                    double age = (now - _roundStart) / (double)Stopwatch.Frequency;
                    var clock = TimespanHintParameter.FromOffset(age > 0 ? -age : 0d, @"h\:mm\:ss", negate: true);
                    p.SendHint(text, new HintParameter[] { clock }, null, cfg.HintDurationSeconds);
                }
                else
                {
                    p.SendHint(text, cfg.HintDurationSeconds);
                }
            }
            catch (Exception e)
            {
                // One bad send must not skip the rest of the players. Rate-limited: this can repeat per visit.
                RateLimitedLog.Warn("adding-player-hud.send", () => "send failed: " + e.Message);
            }
        }

        private string Build(HudConfig cfg, PlayerHud hud, bool timer)
        {
            if (!timer && !cfg.ShowKills) return "";

            _sb.Clear();
            string align = AlignTag(cfg.Align);
            if (align != null) _sb.Append("<align=").Append(align).Append('>');
            _sb.Append("<size=").Append(cfg.TextSizePercent).Append("%>");

            // Every tag opened here is closed below, in the same method. An unclosed <size> or
            // <align> leaks into whatever the client renders after us. No player-controlled text
            // goes in, so there is nothing to brace-escape: "{0}" is ours and must stay intact.
            if (timer) _sb.Append("Round {0}");
            if (timer && cfg.ShowKills) _sb.Append("   ");
            if (cfg.ShowKills) _sb.Append("K ").Append(hud.Kills).Append("  D ").Append(hud.Deaths);

            _sb.Append("</size>");
            if (align != null) _sb.Append("</align>");
            return _sb.ToString();
        }

        // A whitelist: the config string is spliced into rich text, so only known values pass.
        private static string AlignTag(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "left": return "left";
                case "right": return "right";
                case "center": return "center";
                default: return null;
            }
        }
    }
}
