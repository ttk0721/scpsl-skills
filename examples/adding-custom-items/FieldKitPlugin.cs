using System;
using System.Collections.Generic;
using System.ComponentModel;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using PlayerRoles;
using SlKit;
using UnityEngine;

namespace Examples.AddingCustomItems
{
    public sealed class FieldKitConfig
    {
        [Description("Turns the whole plugin off without removing it (kill switch).")]
        public bool IsEnabled { get; set; } = true;

        [Description("Extra HP healed when a field kit is used, on top of the vanilla medkit heal.")]
        public float BonusHeal { get; set; } = 50f;

        [Description("Most field kits that may exist at once (held or on the floor). Bounds the tag store.")]
        public int MaxActive { get; set; } = 4;

        [Description("If true, a kit given to a player can only be picked up by that player (floor-spawned kits stay free for all).")]
        public bool BindToOwner { get; set; } = false;

        [Description("Roles that get one kit when they spawn. Empty means nobody; use the 'fieldkit' command.")]
        public List<RoleTypeId> GiveOnRoles { get; set; } = new List<RoleTypeId>();

        [Description("Hint shown when a kit is equipped. Empty disables the hint.")]
        public string EquipHint { get; set; } = "Field kit: heals 50 extra HP";

        [Description("How often vanished kits are forgotten, in seconds (minimum 10).")]
        public int SweepSeconds { get; set; } = 30;
    }

    public enum GiveResult
    {
        Given,
        DroppedAtFeet,
        CapReached,
        Failed,
    }

    /// <summary>
    /// A "custom item" the cheap way: a vanilla medkit that a plugin-owned table says is special.
    /// No new ItemType exists, so the tag is the serial, and everything below is about keeping that
    /// tag honest: set it where the item is created, test it on every event that carries an item,
    /// and forget it when the item or the round is gone.
    /// </summary>
    public sealed class FieldKitPlugin : Plugin<FieldKitConfig>
    {
        // Fixed here, not in config: UsedItem healing and consume-on-use only hold for a medkit, and
        // the registry validates this type against the live object to catch recycled serials.
        internal const ItemType KitType = ItemType.Medkit;

        private readonly KitRegistry _registry = new KitRegistry();
        private IDisposable _sweepJob;
        private bool _subscribed;

        // The command classes are created by the loader, not by us, so they reach the plugin here.
        internal static FieldKitPlugin Instance { get; private set; }

        public override string Name => "FieldKit";
        public override string Description => "Example: a custom item tagged by serial.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            if (Config == null || !Config.IsEnabled || _subscribed)
                return;

            try
            {
                // Fallible work first, subscriptions last, so a throw leaves nothing live.
                SlClock.Install();
                _sweepJob = SlClock.Every(
                    TimeSpan.FromSeconds(Math.Max(10, Config.SweepSeconds)),
                    SweepJob,
                    "fieldkit.sweep");

                // Every one of these is per-action or per-lifecycle in the events catalog; none is hot.
                PlayerEvents.ChangedRole += OnChangedRole;
                PlayerEvents.ChangedItem += OnChangedItem;
                PlayerEvents.PickingUpItem += OnPickingUpItem;
                PlayerEvents.UsedItem += OnUsedItem;
                ServerEvents.WaitingForPlayers += OnRoundBoundary;
                ServerEvents.RoundRestarted += OnRoundBoundary;
                ServerEvents.Shutdown += OnShutdown;
                _subscribed = true;
                Instance = this;
            }
            catch
            {
                // Swallowing would let the loader print "enabled" for a plugin that is not running.
                Release();
                throw;
            }
        }

        // Never called by LabAPI or the game; Shutdown does the real work.
        public override void Disable() => Release();

        // ---- Giving -------------------------------------------------------------------------

        /// <summary>
        /// Gives a kit and tags it in the same call. The tag is set where the item is created
        /// because no event reliably announces "a tagged item appeared" (PickupCreated is skipped
        /// for pickups our own code creates).
        /// </summary>
        internal GiveResult Give(Player player, bool dropIfFull)
        {
            if (_registry.Count >= Math.Max(1, Config?.MaxActive ?? 0))
                return GiveResult.CapReached;

            // Null means 8 non-ammo items or an unknown type. Category limits are not checked here.
            Item item = player.AddItem(KitType);
            if (item != null)
            {
                _registry.Mark(item.Serial, KitType, player.UserId);
                return GiveResult.Given;
            }

            return dropIfFull ? SpawnAt(player.Position, player.UserId, GiveResult.DroppedAtFeet) : GiveResult.Failed;
        }

        internal GiveResult SpawnAt(Vector3 position, string ownerUserId, GiveResult onSuccess = GiveResult.Given)
        {
            if (_registry.Count >= Math.Max(1, Config?.MaxActive ?? 0))
                return GiveResult.CapReached;

            // Create already network-spawns; calling Spawn() again only logs a warning.
            Pickup pickup = Pickup.Create(KitType, position);
            if (pickup == null)
                return GiveResult.Failed;

            _registry.Mark(pickup.Serial, KitType, ownerUserId);
            return onSuccess;
        }

        internal int ActiveCount => _registry.Count;

        // ---- Recognising --------------------------------------------------------------------

        // Role-based gives wait for ChangedRole: it is the last signal of a spawn and the only one
        // where the loadout already exists (RoundStarted still sees role None).
        private void OnChangedRole(PlayerChangedRoleEventArgs ev)
        {
            FieldKitConfig cfg = Config;
            if (cfg == null || cfg.GiveOnRoles == null || cfg.GiveOnRoles.Count == 0 || ev.Player == null)
                return;
            if (ev.Player.IsHost || ev.Player.IsDummy || !cfg.GiveOnRoles.Contains(ev.NewRole.RoleTypeId))
                return;

            Give(ev.Player, dropIfFull: false);
        }

        // Held: fires on every equip and holster, for every item. The registry's empty check keeps it
        // to one branch while no kit exists.
        private void OnChangedItem(PlayerChangedItemEventArgs ev)
        {
            FieldKitConfig cfg = Config;
            Item now = ev.NewItem;
            if (cfg == null || now == null || string.IsNullOrEmpty(cfg.EquipHint))
                return;

            if (_registry.TryGet(now.Serial, now.Type, out _))
                ev.Player.SendHint(cfg.EquipHint, 2f);
        }

        // Picked up by someone else. Deny before it moves, not after: once it is in the other
        // inventory the serial has changed owner and there is nothing left to cancel.
        private void OnPickingUpItem(PlayerPickingUpItemEventArgs ev)
        {
            FieldKitConfig cfg = Config;
            Pickup pickup = ev.Pickup;
            if (cfg == null || !cfg.BindToOwner || pickup == null)
                return;

            // A null owner (a kit spawned on the floor) belongs to whoever finds it.
            if (_registry.TryGet(pickup.Serial, pickup.Type, out KitTag tag)
                && tag.OwnerUserId != null && tag.OwnerUserId != ev.Player.UserId)
            {
                ev.IsAllowed = false;
                ev.Player.SendHint("This kit belongs to someone else.", 2f);
            }
        }

        // Used: the completion event, after the vanilla heal and after the medkit removed itself.
        private void OnUsedItem(PlayerUsedItemEventArgs ev)
        {
            FieldKitConfig cfg = Config;
            UsableItem used = ev.UsableItem;
            if (cfg == null || used == null)
                return;

            ushort serial = used.Serial;
            if (!_registry.TryGet(serial, used.Type, out _))
                return;

            ev.Player.Heal(cfg.BonusHeal);
            // A medkit is consumed on use, so this serial is finished. Forgetting it now frees the cap
            // slot at once instead of waiting for the sweep.
            _registry.Remove(serial);
        }

        // ---- Cleanup ------------------------------------------------------------------------

        // A restart reloads the scene and destroys every pickup with no per-item event; the serial
        // counter keeps running. Forgetting everything here is the only reliable reset.
        private void OnRoundBoundary() => _registry.Clear();

        private void SweepJob()
        {
            int pruned = _registry.Sweep();
            if (pruned > 0)
                LabApi.Features.Console.Logger.Debug("FieldKit: forgot " + pruned + " vanished kit(s).");
        }

        private void OnShutdown()
        {
            // Nothing is persisted on purpose: serials are meaningless in the next process.
            Release();
        }

        private void Release()
        {
            // Unconditional: "-=" of a handler that was never added is harmless, and it also undoes
            // a half-finished Enable.
            PlayerEvents.ChangedRole -= OnChangedRole;
            PlayerEvents.ChangedItem -= OnChangedItem;
            PlayerEvents.PickingUpItem -= OnPickingUpItem;
            PlayerEvents.UsedItem -= OnUsedItem;
            ServerEvents.WaitingForPlayers -= OnRoundBoundary;
            ServerEvents.RoundRestarted -= OnRoundBoundary;
            ServerEvents.Shutdown -= OnShutdown;

            _sweepJob?.Dispose();
            _sweepJob = null;
            _registry.Clear();
            _subscribed = false;
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }
    }
}
