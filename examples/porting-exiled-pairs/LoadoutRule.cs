using System;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using PlayerRoles;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 8 (spawn loadout). The EXILED original:
    /// <code>
    /// private void OnChangingRole(ChangingRoleEventArgs ev)
    /// {
    ///     if (ev.NewRole == RoleTypeId.ClassD) ev.Items.Add(ItemType.Painkillers);
    /// }
    /// </code>
    /// LabAPI <c>ChangingRole</c> has no <c>Items</c> and no <c>Ammo</c>. The loadout is granted later, and
    /// <c>ReceivingLoadout</c> is its pre-event: <c>ev.AddItem</c>, <c>ev.ClearItems</c>, <c>ev.AddAmmo</c>, <c>ev.SetAmmo</c>.
    /// Do NOT add items in LabAPI <c>Spawned</c>: it is position-only and runs BEFORE the loadout, so items added
    /// there are wiped when the role change carries the AssignInventory flag. The role is already set when
    /// <c>ReceivingLoadout</c> fires, so read <c>ev.Player.Role</c> (not a "new role"). For a late joiner the
    /// player is not <c>IsReady</c> yet and <c>Nickname</c> is not set: touch neither.
    /// </summary>
    internal sealed class LoadoutRule
    {
        private readonly Func<PairsConfig> _cfg;
        private bool _started;

        public LoadoutRule(Func<PairsConfig> cfg)
        {
            _cfg = cfg;
        }

        public void Start()
        {
            if (_started) return;
            PlayerEvents.ReceivingLoadout += OnReceivingLoadout;   // was ChangingRole.Items / .Ammo
            _started = true;
        }

        public void Stop()
        {
            if (!_started) return;
            PlayerEvents.ReceivingLoadout -= OnReceivingLoadout;
            _started = false;
        }

        private void OnReceivingLoadout(PlayerReceivingLoadoutEventArgs ev)
        {
            ItemType bonus = _cfg().ClassDBonusItem;
            if (bonus == ItemType.None) return;
            if (ev.Player.Role != RoleTypeId.ClassD) return;
            ev.AddItem(bonus);
        }
    }
}
