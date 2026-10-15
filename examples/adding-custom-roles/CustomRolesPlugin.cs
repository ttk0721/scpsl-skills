using System;
using System.Collections.Generic;
using CustomPlayerEffects;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using PlayerRoles;
using PlayerStatsSystem;
using SlKit;
using UnityEngine;
using Log = LabApi.Features.Console.Logger; // alias: UnityEngine also has a Logger type

namespace Examples.AddingCustomRoles
{
    /// <summary>
    /// A custom role is NOT a game role. The player really is <see cref="CustomRoleDefinition.BaseRole"/>
    /// (so teams, factions, spawn rules and every other plugin keep working) and we layer a loadout, stats,
    /// effects, appearance and abilities on top, keyed by UserId. The pipeline:
    ///   ChangingRole (decide, O(1))  ->  ReceivingLoadout (swap items)  ->  ChangedRole (stats, effects, look).
    /// Everything is undone by the next ChangedRole of that player, and death is a role change too, so there is no
    /// separate death path.
    /// </summary>
    public sealed class CustomRolesPlugin : Plugin<CustomRolesConfig>
    {
        public override string Name => "CustomRoles";                  // constant: it names the config folder
        public override string Description => "Template: custom roles on top of base roles.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7); // literal, never CompiledVersion

        /// <summary>The running plugin, for the command and for other plugins. Null before Enable and after Shutdown.</summary>
        public static CustomRolesPlugin Instance { get; private set; }

        private readonly RoleRegistry _registry = new RoleRegistry();
        private readonly List<string> _stale = new List<string>(8);   // scratch for the tick: no allocation per second
        private IDisposable _tick;
        private bool _enabled;

        // An empty or comment-only config.yml deserialises to null.
        private CustomRolesConfig Cfg => Config ??= new CustomRolesConfig();

        public override void LoadConfigs()
        {
            base.LoadConfigs();                    // replaces Config with a new object: never cache it
            Cfg.Normalize();
        }

        public override void Enable()
        {
            if (_enabled) return;                  // idempotent: a second Enable must not double-subscribe
            try
            {
                // Fallible work first. No world access: there are no players or rooms in Enable.
                Cfg.Normalize();
                SlClock.Install();
                Instance = this;

                // Subscribe last. The kill switch is read inside the handlers, so a config reload can flip it
                // without a restart and these stay registered.
                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.Shutdown += OnShutdown;
                PlayerEvents.ChangingRole += OnChangingRole;
                PlayerEvents.ReceivingLoadout += OnReceivingLoadout;
                PlayerEvents.ChangedRole += OnChangedRole;
                PlayerEvents.Hurt += OnHurt;
                PlayerEvents.Left += OnLeft;

                // ONE job for all holders. Cost is O(holders) per second, not O(players), and nothing per player
                // is scheduled (no coroutine per holder).
                _tick = SlClock.Every(TimeSpan.FromSeconds(1), Tick, "customroles.tick");
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
            PlayerEvents.ChangingRole -= OnChangingRole;
            PlayerEvents.ReceivingLoadout -= OnReceivingLoadout;
            PlayerEvents.ChangedRole -= OnChangedRole;
            PlayerEvents.Hurt -= OnHurt;
            PlayerEvents.Left -= OnLeft;
            _tick?.Dispose();
            _tick = null;
            _registry.Clear();
            Instance = null;
            _enabled = false;
        }

        // ---- public surface -------------------------------------------------------------------------------

        /// <summary>
        /// For other plugins (HUD, stats, anti-cheat): is this player currently a custom role, and which?
        /// Ask this instead of guessing from CustomInfo or caching the answer: it is verified against the live role
        /// instance, so a stale entry can never answer true.
        /// </summary>
        public static bool TryGetRoleId(Player player, out string roleId)
        {
            roleId = null;
            CustomRolesPlugin self = Instance;
            if (self == null || player == null || player.IsDestroyed) return false;
            string id = player.UserId;
            if (string.IsNullOrEmpty(id) || !self._registry.TryGetActive(id, out Assignment a)) return false;
            if (a.LifeId != player.LifeId) return false;
            roleId = a.RoleId;
            return true;
        }

        /// <summary>
        /// Spawns the player as the custom role (a full respawn: the game treats a same-role set that way).
        /// Call it from commands, timers or API code, NEVER from a role or spawn event handler of the same player.
        /// </summary>
        public bool Grant(Player player, string roleId, out string error)
        {
            error = null;
            if (!Cfg.IsEnabled) { error = "CustomRoles is disabled in its config."; return false; }
            CustomRoleDefinition def = FindDefinition(roleId);
            if (def == null) { error = "Unknown or disabled role '" + roleId + "'."; return false; }
            if (player == null || player.IsDestroyed || player.IsHost || player.IsDummy || !player.IsReady
                || string.IsNullOrEmpty(player.UserId))
            {
                error = "Target must be a connected, authenticated player (not the host or a dummy).";
                return false;
            }
            // A role missing from the loaded table (holiday roles) silently becomes None: refuse up front.
            if (!def.BaseRole.TryGetRoleTemplate(out PlayerRoleBase _))
            {
                error = "Base role " + def.BaseRole + " is not available on this build.";
                return false;
            }

            string id = player.UserId;
            _registry.SetPending(id, def.Id, Time.frameCount);
            // RemoteAdmin + All: honest reason (no fake RoundStart/Escaped counters) and a normal spawn with loadout.
            player.SetRole(def.BaseRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);

            // The role change is synchronous. If it was cancelled or redirected by another plugin, say so.
            if (_registry.TryGetActive(id, out Assignment a) && a.RoleId == def.Id) return true;
            _registry.ClearPending(id);
            error = "The role change was cancelled or redirected by another plugin.";
            return false;
        }

        public IEnumerable<string> DefinitionIds()
        {
            List<CustomRoleDefinition> roles = Cfg.Roles;
            if (roles == null) yield break;
            foreach (CustomRoleDefinition d in roles)
                if (d != null && d.Enabled) yield return d.Id;
        }

        // ---- the pipeline ---------------------------------------------------------------------------------

        private void OnChangingRole(PlayerChangingRoleEventArgs e)
        {
            // Runs once per player in the round-start frame: cheap filters first, no allocation, no Player.List.
            try
            {
                CustomRolesConfig cfg = Cfg;
                if (!cfg.IsEnabled || !e.IsAllowed || !IsVanillaSpawn(e.ChangeReason)) return;

                CustomRoleDefinition def = PickDefinition(cfg, e.NewRole);
                Player p = e.Player;
                if (p.IsHost || p.IsDummy) return;   // dummies share one UserId: they cannot be keyed
                string id = p.UserId;
                if (string.IsNullOrEmpty(id)) return;

                if (def == null)
                {
                    // A same-frame decision from an earlier, cancelled ChangingRole must not ride on this one.
                    if (_registry.PendingCount != 0) _registry.ClearPending(id);
                    return;
                }

                // Decide here, apply later. Position, stats and items of the new role do not exist yet.
                _registry.SetPending(id, def.Id, Time.frameCount);
            }
            catch (Exception ex) { Log.Error("[CustomRoles] ChangingRole: " + ex); }
        }

        private void OnReceivingLoadout(PlayerReceivingLoadoutEventArgs e)
        {
            try
            {
                if (_registry.PendingCount == 0) return;           // nearly always: one int compare
                Player p = e.Player;
                string id = p.UserId;
                if (string.IsNullOrEmpty(id) || !_registry.TryGetFreshPending(id, Time.frameCount, out PendingGrant g)) return;
                CustomRoleDefinition def = FindDefinition(g.RoleId);
                // Another plugin may have redirected the role after we decided: then this is not our loadout.
                if (def == null || def.Items.Count == 0 || p.Role != def.BaseRole) return;

                // Edit the lists in place (a replaced list or InventoryReset is ignored by the game). Do not keep them: pooled.
                e.ClearItems();
                foreach (ItemType item in def.Items) e.AddItem(item);
                e.ClearAmmo();
                foreach (KeyValuePair<ItemType, ushort> kv in def.Ammo) e.SetAmmo(kv.Key, kv.Value);
            }
            catch (Exception ex) { Log.Error("[CustomRoles] ReceivingLoadout: " + ex); }
        }

        private void OnChangedRole(PlayerChangedRoleEventArgs e)
        {
            try
            {
                // The common case on a 60 player server: nobody holds a custom role and nothing is pending.
                if (_registry.ActiveCount == 0 && _registry.PendingCount == 0) return;
                Player p = e.Player;
                if (p.IsHost || p.IsDummy) return;
                string id = p.UserId;
                if (string.IsNullOrEmpty(id)) return;

                // 1) Whatever this player held belonged to the OLD role instance. A new role, a respawn and a death
                //    all land here (KillPlayer sets Spectator before Death is raised), so this is the one cleanup path.
                if (_registry.TryRemoveActive(id, out Assignment old)) Release(p, old);

                // 2) A decision made by ChangingRole of THIS call stack?
                if (!_registry.TryTakePending(id, Time.frameCount, out PendingGrant grant)) return;
                CustomRoleDefinition def = FindDefinition(grant.RoleId);
                // Someone redirected the role after us, or the config changed under us: drop the decision.
                if (def == null || def.BaseRole != e.NewRole.RoleTypeId) return;

                Apply(p, id, def);
            }
            catch (Exception ex) { Log.Error("[CustomRoles] ChangedRole: " + ex); }
        }

        private void Apply(Player p, string userId, CustomRoleDefinition def)
        {
            var a = new Assignment { RoleId = def.Id, LifeId = p.LifeId };
            _registry.Activate(userId, a);       // first, so a throw below still leaves something the tick can clean

            // Valid only from ChangedRole: the game resets stats and effects after Spawned.
            if (def.MaxHealth > 0f)
            {
                p.MaxHealth = def.MaxHealth;      // max first: lowering the max clamps the current value
                p.Health = def.MaxHealth;
            }
            if (def.ArtificialHealth > 0f)
            {
                // The max resets to the default on a class change; raise it before the value.
                if (p.MaxArtificialHealth < def.ArtificialHealth) p.MaxArtificialHealth = def.ArtificialHealth;
                p.ArtificialHealth = def.ArtificialHealth;
            }

            // Passive abilities as status effects: no handler, no per-frame cost of ours, and the game wipes them on
            // the next role change, so they clean themselves up. Duration 0 means "forever" and is deliberate here.
            // Both effects are Positive, so spawn protection cannot refuse them.
            if (def.DamageReduction > 0) p.EnableEffect<DamageReduction>((byte)def.DamageReduction, 0f);
            if (def.MovementBoost > 0) p.EnableEffect<MovementBoost>((byte)def.MovementBoost, 0f);

            ApplyAppearance(p, def, a);

            if (Cfg.Debug) Log.Info("[CustomRoles] " + def.Id + " assigned (" + def.BaseRole + ")");
        }

        private static void ApplyAppearance(Player p, CustomRoleDefinition def, Assignment a)
        {
            string text = def.DisplayName;
            if (string.IsNullOrEmpty(text)) return;
            if (!Player.ValidateCustomInfo(text, out string reason))
            {
                Log.Warn("[CustomRoles] " + def.Id + ": DisplayName rejected by the game (" + reason + "), no custom info shown.");
                return;
            }
            // Custom info and the info-area flags are NOT reset by a role change (verified in the code, not run on a
            // server): a dead player would keep the label. Remember what was there so Release can undo exactly this.
            a.TouchedAppearance = true;
            a.PrevCustomInfo = p.CustomInfo;
            a.PrevInfoArea = p.InfoArea;
            PlayerInfoArea area = p.InfoArea | PlayerInfoArea.CustomInfo;
            if (def.HideVanillaRole) area &= ~PlayerInfoArea.Role;
            p.CustomInfo = text;
            p.InfoArea = area;
            a.AppliedCustomInfo = text;
            a.AppliedInfoArea = area;
        }

        private static void Release(Player p, Assignment a)
        {
            if (p == null || p.IsDestroyed) return;
            if (a.TouchedAppearance)
            {
                // Only revert what is still ours: another plugin may have taken over the label since.
                if (p.CustomInfo == a.AppliedCustomInfo) p.CustomInfo = a.PrevCustomInfo ?? string.Empty;
                if (p.InfoArea == a.AppliedInfoArea) p.InfoArea = a.PrevInfoArea;
            }
            // Same role instance still running (kill switch, definition removed): the game will not wipe the effects.
            // After a role change the LifeId differs and the effects are already gone.
            if (p.LifeId == a.LifeId)
            {
                p.DisableEffect<DamageReduction>();
                p.DisableEffect<MovementBoost>();
            }
        }

        // ---- abilities ------------------------------------------------------------------------------------

        private void OnHurt(PlayerHurtEventArgs e)
        {
            // Per-action event: the gate is the first line. With no holders this is one int compare per hit.
            if (_registry.ActiveCount == 0) return;
            try
            {
                Player attacker = e.Attacker;
                // IsFriendlyFire and IsSuicide are valid from Hurt on (they are false in Hurting).
                if (attacker == null || !(e.DamageHandler is AttackerDamageHandler ah) || ah.IsFriendlyFire || ah.IsSuicide) return;

                string id = attacker.UserId;
                if (string.IsNullOrEmpty(id) || !_registry.TryGetActive(id, out Assignment a)) return;
                if (a.LifeId != attacker.LifeId) return;            // a stale entry must never act
                CustomRoleDefinition def = FindDefinition(a.RoleId);
                if (def == null || def.LeechPercent <= 0 || !Cfg.IsEnabled) return;

                // Whole points only: sub-step writes add CPU, no information. Heal clamps to the max health.
                float heal = (float)Math.Floor(ah.DealtHealthDamage * def.LeechPercent / 100f);
                if (heal >= 1f) attacker.Heal(heal);
            }
            catch (Exception ex) { Log.Error("[CustomRoles] Hurt: " + ex); }
        }

        private void Tick()
        {
            _registry.SweepPending(Time.frameCount, _stale);
            if (_registry.ActiveCount == 0) return;
            CustomRolesConfig cfg = Cfg;
            bool on = cfg.IsEnabled;

            _stale.Clear();
            foreach (KeyValuePair<string, Assignment> kv in _registry.Active)
            {
                Assignment a = kv.Value;
                Player p = Player.Get(kv.Key);                // one lookup per HOLDER, not per player
                CustomRoleDefinition def = on ? FindDefinition(a.RoleId) : null;

                // Self-healing: anything that no longer matches the live role instance is released, whichever path
                // missed it. This is the safety net, not the primary cleanup (that is ChangedRole).
                if (def == null || p == null || p.IsDestroyed || !p.IsAlive || p.LifeId != a.LifeId)
                {
                    _stale.Add(kv.Key);
                    continue;
                }

                // Late joiners get their role before Joined, when the client may not be ready: announce once ready.
                if (!a.Announced && p.IsReady)
                {
                    a.Announced = true;
                    if (cfg.AnnounceHintSeconds > 0f)
                        p.SendHint("You are now: " + (string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName), cfg.AnnounceHintSeconds);
                }

                // Whole points at 1 Hz. Every change is also sent to every dead player, so never heal per frame.
                if (def.RegenPerSecond > 0 && p.Health < p.MaxHealth) p.Heal(def.RegenPerSecond);
            }

            for (int i = 0; i < _stale.Count; i++)
                if (_registry.TryRemoveActive(_stale[i], out Assignment gone))
                    Release(Player.Get(_stale[i]), gone);
        }

        // ---- lifecycle ------------------------------------------------------------------------------------

        private void OnLeft(PlayerLeftEventArgs e)
        {
            try
            {
                // The hub is going away: nothing to restore on it, only our own state to drop. Left also fires for
                // every hub at a round restart and for unauthenticated hubs (UserId null).
                string id = e.Player.UserId;
                if (string.IsNullOrEmpty(id)) return;
                _registry.TryRemoveActive(id, out Assignment _);
                _registry.ClearPending(id);
            }
            catch (Exception ex) { Log.Error("[CustomRoles] Left: " + ex); }
        }

        private void OnWaitingForPlayers()
        {
            // New world: every Player of the last round is gone and no custom role survives a scene reload.
            _registry.Clear();
        }

        private void OnShutdown()
        {
            // Synchronous and cheap. LabAPI and the game never call Disable(); this is the only last call.
            Teardown();
        }

        // ---- helpers --------------------------------------------------------------------------------------

        /// <summary>Only the vanilla spawn flows roll. Admin sets, deaths and escapes never do.</summary>
        private static bool IsVanillaSpawn(RoleChangeReason reason)
        {
            return reason == RoleChangeReason.RoundStart || reason == RoleChangeReason.LateJoin
                || reason == RoleChangeReason.Respawn || reason == RoleChangeReason.RespawnMiniwave;
        }

        private CustomRoleDefinition PickDefinition(CustomRolesConfig cfg, RoleTypeId newRole)
        {
            List<CustomRoleDefinition> roles = cfg.Roles;
            if (roles == null) return null;
            for (int i = 0; i < roles.Count; i++)               // a handful of entries: a loop beats any lookup table
            {
                CustomRoleDefinition d = roles[i];
                if (d == null || !d.IsRollable(newRole)) continue;
                // Assignments are applied one player at a time inside the same frame, so the count is current.
                if (d.MaxAlive > 0 && _registry.CountAlive(d.Id) >= d.MaxAlive) continue;
                if (UnityEngine.Random.value < d.Chance) return d;
            }
            return null;
        }

        private CustomRoleDefinition FindDefinition(string roleId)
        {
            List<CustomRoleDefinition> roles = Cfg.Roles;      // read at use time: a reload replaces the list
            if (roles == null || roleId == null) return null;
            for (int i = 0; i < roles.Count; i++)
            {
                CustomRoleDefinition d = roles[i];
                if (d != null && d.Enabled && string.Equals(d.Id, roleId, StringComparison.OrdinalIgnoreCase)) return d;
            }
            return null;
        }
    }
}
