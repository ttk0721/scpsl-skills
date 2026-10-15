using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using PlayerRoles;
using PlayerStatsSystem;
using SlKit;

namespace Examples.AddingGameplayRules
{
    /// <summary>
    /// Gameplay rules done with cancellable events: friendly fire, damage modifiers, door,
    /// elevator and pickup restrictions, escape rules. Design contract:
    ///  - A rule only ever CANCELS (writes false) or MULTIPLIES. Both are order-independent, so the
    ///    result does not depend on which plugin subscribed first.
    ///  - Each event is subscribed only while a rule needs it; a disabled rule costs nothing.
    ///  - Handlers do one early-out, array lookups and at most one write. No logging, no LINQ.
    /// </summary>
    public sealed class RulesPlugin : Plugin<RulesConfig>
    {
        private enum Rule { FriendlyFire, Damage, Door, Elevator, Pickup, Escape }

        public override string Name => "AddingGameplayRules";
        public override string Description => "Example: gameplay rules via cancellable events.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);

        // A constant: LabApiProperties.CompiledVersion is the running DLL's own version, so
        // using it would make the version check pass vacuously.
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        // Handlers read this one reference. LoadConfigs replaces it; nothing mutates it in place.
        private RuleTables _t;

        private bool _enabled;
        private bool _hurtingWired, _doorWired, _elevatorWired, _pickupWired, _escapeWired;
        private IDisposable _summaryJob;
        private int _summaryJobSeconds;

        // Per-round: when each player last saw a "denied" hint (Stopwatch ticks). Keyed by
        // PlayerId because it is cleared at WaitingForPlayers, before any id can be reused.
        private readonly Dictionary<int, long> _lastNotice = new Dictionary<int, long>(64);
        private readonly int[] _fired = new int[6];
        private readonly StringBuilder _summary = new StringBuilder(160);

        // An empty or comment-only config.yml deserialises to null.
        private RulesConfig Cfg => Config ?? (Config = new RulesConfig());

        // Runs before Enable() and again on 'labapi reload configs'. Only compile and (re)wire here.
        public override void LoadConfigs()
        {
            base.LoadConfigs();
            _t = new RuleTables(Cfg);
            if (_enabled) Wire();
        }

        public override void Enable()
        {
            if (_enabled) return; // an EXILED-folder reload can call Enable() twice

            // Fallible work first, subscriptions last: a throw after subscribing would leave
            // handlers live with no rollback, because Disable() is never called.
            SlClock.Install();
            if (_t == null) _t = new RuleTables(Cfg);

            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.Shutdown += OnShutdown;
            _enabled = true;
            Wire();
        }

        // Never called by LabAPI or the game. Kept correct so a future loader or a test can use it.
        public override void Disable() => Teardown();

        // Reconciles subscriptions with the compiled config. Idempotent: called from Enable and
        // from every reload, so flipping a rule off at runtime really unsubscribes its handler.
        private void Wire()
        {
            RuleTables t = _t;

            if (t.HurtingNeeded && !_hurtingWired) { PlayerEvents.Hurting += OnHurting; _hurtingWired = true; }
            else if (!t.HurtingNeeded && _hurtingWired) { PlayerEvents.Hurting -= OnHurting; _hurtingWired = false; }

            if (t.DoorsOn && !_doorWired) { PlayerEvents.InteractingDoor += OnInteractingDoor; _doorWired = true; }
            else if (!t.DoorsOn && _doorWired) { PlayerEvents.InteractingDoor -= OnInteractingDoor; _doorWired = false; }

            if (t.ElevatorsOn && !_elevatorWired) { PlayerEvents.InteractingElevator += OnInteractingElevator; _elevatorWired = true; }
            else if (!t.ElevatorsOn && _elevatorWired) { PlayerEvents.InteractingElevator -= OnInteractingElevator; _elevatorWired = false; }

            if (t.PickupsOn && !_pickupWired) { PlayerEvents.PickingUpItem += OnPickingUpItem; _pickupWired = true; }
            else if (!t.PickupsOn && _pickupWired) { PlayerEvents.PickingUpItem -= OnPickingUpItem; _pickupWired = false; }

            // Escaping is raised every frame for every player standing in the escape zone. The
            // subscription exists only while the rule is on, and the handler's first line is one
            // enum compare.
            if (t.EscapeOn && !_escapeWired)
            {
                // scpsl-lint: ignore SL001 -- gated by config, removed on reload; first line of the handler is a single enum compare
                PlayerEvents.Escaping += OnEscaping;
                _escapeWired = true;
            }
            else if (!t.EscapeOn && _escapeWired) { PlayerEvents.Escaping -= OnEscaping; _escapeWired = false; }

            // One clock job for the periodic summary, re-created only when the period changed.
            if (_summaryJob != null && _summaryJobSeconds != t.SummarySeconds)
            {
                _summaryJob.Dispose();
                _summaryJob = null;
            }
            if (_summaryJob == null && t.SummarySeconds > 0)
            {
                _summaryJob = SlClock.Every(TimeSpan.FromSeconds(t.SummarySeconds), Summarize, "rules.summary");
                _summaryJobSeconds = t.SummarySeconds;
            }
        }

        private void OnHurting(PlayerHurtingEventArgs ev)
        {
            try
            {
                // Cheapest tests first. The type test also drops every damage-over-time tick and
                // the warhead: they are not AttackerDamageHandler, so they never reach the wrappers.
                if (!ev.IsAllowed || !(ev.DamageHandler is AttackerDamageHandler h)) return;

                // Damage is RAW here: the game applies hitbox, its own friendly-fire multiplier,
                // effects and armour AFTER this event. -1 is an instakill; scaling it is meaningless.
                if (h.Damage <= 0f) return;

                RuleTables t = _t;
                Player attacker = ev.Attacker;   // null when the attacker's hub is gone
                if (attacker == null) return;
                Player victim = ev.Player;

                // IsFriendlyFire is still false inside Hurting (set later by the modifier chain),
                // so the faction test is ours. Same faction mirrors the game's own enemy test.
                if (t.FriendlyFireOn && attacker.PlayerId != victim.PlayerId)
                {
                    Faction f = attacker.Faction;
                    if (f == victim.Faction && (f == Faction.FoundationStaff || f == Faction.FoundationEnemy))
                    {
                        _fired[(int)Rule.FriendlyFire]++;
                        if (t.FriendlyFireBlock) { ev.IsAllowed = false; return; }   // write false only
                        h.Damage *= t.FriendlyFireScale;                             // same object: honoured
                    }
                }

                if (t.DamageOn)
                {
                    int v = RuleTables.Slot((int)victim.Role, RuleTables.RoleSlots);
                    int a = RuleTables.Slot((int)attacker.Role, RuleTables.RoleSlots);
                    float m = (v < 0 ? 1f : t.VictimMultiplier[v]) * (a < 0 ? 1f : t.AttackerMultiplier[a]);
                    if (m != 1f)
                    {
                        h.Damage *= m;   // never ev.DamageHandler = ...: the game ignores that setter
                        _fired[(int)Rule.Damage]++;
                    }
                }
            }
            catch (Exception e)
            {
                // Hurting can fire per frame per victim under DoT: a persistent fault must not
                // become one log line per raise.
                RateLimitedLog.Error("rules.hurting", () => "Hurting handler failed: " + e);
            }
        }

        private void OnInteractingDoor(PlayerInteractingDoorEventArgs ev)
        {
            try
            {
                if (!ev.IsAllowed) return;
                RuleTables t = _t;
                Player p = ev.Player;
                int role = RuleTables.Slot((int)p.Role, RuleTables.RoleSlots);
                if (role < 0) return;

                if (t.DoorDeny.Count > 0 && t.DoorDeny.TryGetValue((int)ev.Door.DoorName, out bool[] denied) && denied[role])
                {
                    ev.IsAllowed = false;          // cancel on the lock path also fires the game's denial feedback
                    Deny(p, Rule.Door);
                    return;
                }

                // CanOpen is the verdict for BOTH the keycard check and the lock check, so setting
                // it true would also open a locked door. Grant only when nothing locks the door.
                if (t.DoorGrant[role] && !ev.CanOpen && !ev.Door.IsLocked)
                {
                    ev.CanOpen = true;
                    _fired[(int)Rule.Door]++;
                }
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("rules.door", () => "InteractingDoor handler failed: " + e);
            }
        }

        private void OnInteractingElevator(PlayerInteractingElevatorEventArgs ev)
        {
            try
            {
                // The game pre-sets IsAllowed = false for a locked or moving elevator and still
                // raises the event. Writing true would force-send it, so this handler only ever
                // writes false, and the early return also skips already-denied calls.
                if (!ev.IsAllowed) return;
                int role = RuleTables.Slot((int)ev.Player.Role, RuleTables.RoleSlots);
                if (role < 0 || !_t.ElevatorDeny[role]) return;
                ev.IsAllowed = false;
                Deny(ev.Player, Rule.Elevator);
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("rules.elevator", () => "InteractingElevator handler failed: " + e);
            }
        }

        private void OnPickingUpItem(PlayerPickingUpItemEventArgs ev)
        {
            try
            {
                if (!ev.IsAllowed) return;
                int role = RuleTables.Slot((int)ev.Player.Role, RuleTables.RoleSlots);
                if (role < 0) return;
                bool[] items = _t.PickupDeny[role];
                if (items == null) return;
                int item = RuleTables.Slot((int)ev.Pickup.Type, RuleTables.ItemSlots);
                if (item < 0 || !items[item]) return;
                ev.IsAllowed = false;          // the pickup goes back to the world, unharmed
                Deny(ev.Player, Rule.Pickup);
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("rules.pickup", () => "PickingUpItem handler failed: " + e);
            }
        }

        private void OnEscaping(PlayerEscapingEventArgs ev)
        {
            try
            {
                // HOT: runs every frame for every FPC player inside the zone, even when no escape
                // is possible (scenario None). Leave before touching anything else.
                if (ev.EscapeScenario == Escape.EscapeScenarioType.None || !ev.IsAllowed) return;

                RuleTables t = _t;
                int role = RuleTables.Slot((int)ev.OldRole, RuleTables.RoleSlots);
                if (role < 0) return;

                if (t.EscapeBlock[role])
                {
                    // A cancelled escape leaves the player in the zone, so this repeats every frame
                    // until they walk out. Cancelling is one write; the hint is rate-limited per
                    // player; nothing here logs.
                    ev.IsAllowed = false;
                    Deny(ev.Player, Rule.Escape);
                    return;
                }

                RoleTypeId target = t.EscapeRole[role];
                if (target != RoleTypeId.None && ev.NewRole != target)
                {
                    ev.NewRole = target;     // honoured: the game re-reads NewRole after the raise
                    _fired[(int)Rule.Escape]++;
                }
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("rules.escape", () => "Escaping handler failed: " + e);
            }
        }

        // Counts a denial and shows the hint at most once per cooldown per player. The counter only
        // moves when a hint is shown, so a player parked in the escape zone counts once per cooldown
        // window instead of once per frame.
        private void Deny(Player p, Rule rule)
        {
            RuleTables t = _t;
            long now = Stopwatch.GetTimestamp();
            long last;
            if (_lastNotice.TryGetValue(p.PlayerId, out last) && now - last < t.NoticeTicks) return;
            _lastNotice[p.PlayerId] = now;
            _fired[(int)rule]++;
            if (t.DenyMessage.Length > 0) p.SendHint(t.DenyMessage, 2f);
        }

        private void Summarize()
        {
            int total = 0;
            for (int i = 0; i < _fired.Length; i++) total += _fired[i];
            if (total == 0) return;

            _summary.Length = 0;
            _summary.Append("Rules fired since last summary:");
            for (int i = 0; i < _fired.Length; i++)
            {
                if (_fired[i] == 0) continue;
                _summary.Append(' ').Append((Rule)i).Append('=').Append(_fired[i]);
                _fired[i] = 0;
            }
            Logger.Info(_summary.ToString());
        }

        private void OnWaitingForPlayers()
        {
            // A round restart reloads the scene: PlayerIds from the last round mean nothing now.
            _lastNotice.Clear();
        }

        private void OnShutdown()
        {
            // The only reliable last call. Flush the pending counters, then release.
            Summarize();
            Teardown();
        }

        private void Teardown()
        {
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            if (_hurtingWired) { PlayerEvents.Hurting -= OnHurting; _hurtingWired = false; }
            if (_doorWired) { PlayerEvents.InteractingDoor -= OnInteractingDoor; _doorWired = false; }
            if (_elevatorWired) { PlayerEvents.InteractingElevator -= OnInteractingElevator; _elevatorWired = false; }
            if (_pickupWired) { PlayerEvents.PickingUpItem -= OnPickingUpItem; _pickupWired = false; }
            if (_escapeWired) { PlayerEvents.Escaping -= OnEscaping; _escapeWired = false; }
            _summaryJob?.Dispose();
            _summaryJob = null;
            _enabled = false;
        }
    }
}
