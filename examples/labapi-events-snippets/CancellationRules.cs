using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using PlayerRoles;
using PlayerStatsSystem;
using SlKit;

namespace Examples.LabapiEventsSnippets
{
    /// <summary>
    /// Four ways an event handler changes what the game does, each one the way the game actually reads it:
    /// <list type="bullet">
    /// <item><b>Cancel</b> with <c>IsAllowed = false</c> only. Never write <c>true</c> or <c>= !cond</c>:
    /// the game reads the flag once after every handler ran, so a <c>true</c> un-cancels another plugin.</item>
    /// <item><b>Mutate the same object</b> the game holds (<c>Hurting</c>): reassigning the property is ignored.</item>
    /// <item><b>Write a setter the game re-reads</b> (<c>InteractingDoor.CanOpen</c>, <c>ChangingRole.NewRole</c>).</item>
    /// <item><b>Never re-enter</b>: no <c>SetRole</c> or damage call inside the matching <c>-ing</c> handler.</item>
    /// </list>
    /// All handlers run on the main thread, so plain collections need no lock.
    /// </summary>
    internal sealed class CancellationRules
    {
        private const int GuardSwapsPerRound = 2;

        private readonly HashSet<int> _shielded = new HashSet<int>();  // cannot be hurt
        private readonly HashSet<int> _softened = new HashSet<int>();  // takes half damage
        private readonly HashSet<int> _frozen = new HashSet<int>();    // cannot use doors
        private readonly HashSet<int> _masters = new HashSet<int>();   // opens any door, locked included
        private int _guardSwapsLeft = GuardSwapsPerRound;

        public void Shield(int playerId) => _shielded.Add(playerId);
        public void Soften(int playerId) => _softened.Add(playerId);
        public void Freeze(int playerId) => _frozen.Add(playerId);
        public void GrantMaster(int playerId) => _masters.Add(playerId);

        public void Forget(int playerId)
        {
            _shielded.Remove(playerId);
            _softened.Remove(playerId);
            _frozen.Remove(playerId);
            _masters.Remove(playerId);
        }

        public void ResetRound()
        {
            _shielded.Clear();
            _softened.Clear();
            _frozen.Clear();
            _masters.Clear();
            _guardSwapsLeft = GuardSwapsPerRound;
        }

        /// <summary>
        /// Per action normally, per frame per victim under damage-over-time effects: no logging in the
        /// body, a rate-limited line in the catch, and a cheap early return before any lookup.
        /// </summary>
        public void OnHurting(PlayerHurtingEventArgs ev)
        {
            try
            {
                // Sees only cancels made by EARLIER handlers. A later one can still cancel after us,
                // which is fine here: halving a number is reversible, nothing was committed.
                if (!ev.IsAllowed || ev.Player == null)
                    return;

                int victim = ev.Player.PlayerId;

                if (_shielded.Contains(victim))
                {
                    ev.IsAllowed = false; // write false only, and only when we mean to deny
                    return;
                }

                // The game applies its OWN local handler after the raise, so
                // "ev.DamageHandler = new ..." compiles and does nothing (lint rule SL003).
                // The args hold the same object the game holds: change that one in place.
                if (_softened.Contains(victim) && ev.DamageHandler is StandardDamageHandler standard)
                    standard.Damage *= 0.5f;
            }
            catch (Exception e)
            {
                // Not Logger.Error("..." + e): a persistent fault would log once per damage event.
                RateLimitedLog.Error("snippets.hurting", () => "Hurting handler failed: " + e);
            }
        }

        /// <summary>
        /// On a locked door the game raises this with <c>CanOpen == false</c> and takes <c>ev.CanOpen</c>
        /// as the lock verdict, so setting it to true opens the door. Do not cancel and then toggle
        /// <c>Door.IsOpened</c>: cancelling a locked-door interaction still plays the denial feedback and
        /// fires <c>InteractedDoor(canOpen: false)</c>, and <c>IsOpened</c> ignores locks and permissions.
        /// </summary>
        public void OnInteractingDoor(PlayerInteractingDoorEventArgs ev)
        {
            try
            {
                if (!ev.IsAllowed || ev.Player == null)
                    return;

                int id = ev.Player.PlayerId;

                if (_frozen.Contains(id))
                {
                    ev.IsAllowed = false;
                    return;
                }

                if (!ev.CanOpen && _masters.Contains(id))
                    ev.CanOpen = true; // a setter the game re-reads, on the locked and the unlocked path
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("snippets.door", () => "InteractingDoor handler failed: " + e);
            }
        }

        /// <summary>
        /// Role override by editing the args. <c>ev.Player.SetRole(...)</c> here would re-enter
        /// <c>ChangingRole</c> synchronously, and the outer call would then overwrite the inner result.
        /// </summary>
        public void OnChangingRole(PlayerChangingRoleEventArgs ev)
        {
            try
            {
                if (!ev.IsAllowed || ev.NewRole != RoleTypeId.ClassD || ev.ChangeReason != RoleChangeReason.RoundStart)
                    return;

                if (_guardSwapsLeft <= 0)
                    return;

                _guardSwapsLeft--;
                ev.NewRole = RoleTypeId.FacilityGuard; // NewRole is re-read by the game when the raise is allowed
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("snippets.role", () => "ChangingRole handler failed: " + e);
            }
        }
    }
}
