using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using PlayerStatsSystem;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 6 (cancellable damage). The EXILED original:
    /// <code>
    /// private void OnHurting(HurtingEventArgs ev)
    /// {
    ///     if (ev.DamageHandler.Type == DamageType.Falldown) { ev.IsAllowed = false; return; }
    ///     if (ev.Attacker == null || ev.Attacker == ev.Player) return;
    ///     if (ev.Attacker.Role.Side == ev.Player.Role.Side) ev.Amount *= 0.5f;
    /// }
    /// </code>
    /// Four things differ in LabAPI:
    /// 1. There is no <c>DamageType</c> enum and no <c>ev.Amount</c>. The args carry the game's
    ///    <c>DamageHandlerBase</c>; test its CLASS (and for universal handlers its translation id), and change
    ///    damage on a <c>StandardDamageHandler</c>.
    /// 2. The <c>DamageHandler</c> setter on the args is IGNORED by the game (it keeps its own local handler).
    ///    Mutate the existing handler (<c>h.Damage *= x</c>); never assign <c>ev.DamageHandler = ...</c> (scpsl-lint SL003).
    /// 3. LabAPI <c>Hurting</c> runs later than EXILED's: after the god-mode and spawn-protection early returns.
    ///    A protected victim never reaches your handler at all.
    /// 4. Cancelling <c>Dying</c> is not "undo the hit": the damage was already applied, so the victim stays alive at
    ///    0 HP unless the handler restores <c>Health</c>.
    /// Damage here is RAW: the hitbox factor, the game's friendly-fire multiplier, effects and armour run after it.
    /// To see the final number, read it in <c>Hurt</c>.
    /// </summary>
    internal sealed class DamageRules
    {
        private readonly Func<PairsConfig> _cfg;
        private readonly HashSet<string> _secondChanceUsed = new HashSet<string>();
        private bool _started;

        public DamageRules(Func<PairsConfig> cfg)
        {
            _cfg = cfg;
        }

        public void Start()
        {
            if (_started) return;
            PlayerEvents.Hurting += OnHurting;
            PlayerEvents.Dying += OnDying;
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            _started = true;
        }

        public void Stop()
        {
            if (!_started) return;
            PlayerEvents.Hurting -= OnHurting;
            PlayerEvents.Dying -= OnDying;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            _secondChanceUsed.Clear();
            _started = false;
        }

        // Hurting is HOT on some sources (MicroHID every frame, every shotgun pellet): the cheap type test comes first,
        // before any wrapper work, logging or list walking.
        private void OnHurting(PlayerHurtingEventArgs ev)
        {
            StandardDamageHandler handler = ev.DamageHandler as StandardDamageHandler;
            if (handler == null) return;

            PairsConfig cfg = _cfg();   // read at use time: a config reload replaces the object

            // EXILED: ev.DamageHandler.Type == DamageType.Falldown. Fall damage is a UniversalDamageHandler with this translation.
            if (cfg.BlockFallDamage && IsFall(handler))
            {
                ev.IsAllowed = false;   // cancel: DealDamage returns false, nothing is applied
                return;
            }

            if (cfg.FriendlyDamageMultiplier >= 1f) return;
            if (handler.Damage <= 0f) return;   // -1 is an instakill that skips the modifier chain; 0 has nothing to scale

            Player attacker = ev.Attacker;   // null for environment damage, or when the attacker's hub is gone
            if (attacker == null || ReferenceEquals(attacker, ev.Player)) return;

            // EXILED compared Role.Side. Friendly fire in the game is decided by faction, so compare faction.
            if (attacker.Faction == ev.Player.Faction)
                handler.Damage *= cfg.FriendlyDamageMultiplier;   // same object: honoured
        }

        private void OnDying(PlayerDyingEventArgs ev)
        {
            int health = _cfg().SecondChanceHealth;
            if (health <= 0) return;

            Player victim = ev.Player;
            if (victim.IsDummy) return;   // every dummy shares the user id "ID_Dummy"
            string id = victim.UserId;
            if (string.IsNullOrEmpty(id) || !_secondChanceUsed.Add(id)) return;

            ev.IsAllowed = false;
            // The hit has already been applied: without this the player stays "alive" at 0 HP and the next damage re-enters the death path.
            victim.Health = Math.Min(health, victim.MaxHealth);
        }

        private void OnWaitingForPlayers()
        {
            _secondChanceUsed.Clear();   // per-round state
        }

        private static bool IsFall(StandardDamageHandler handler)
        {
            UniversalDamageHandler universal = handler as UniversalDamageHandler;
            return universal != null && universal.TranslationId == DeathTranslations.Falldown.Id;
        }
    }
}
