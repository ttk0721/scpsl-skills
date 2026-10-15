using Interactables.Interobjects.DoorUtils;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operation 6: doors. Locks are independent bits; the wrapper's friendly members each do less
    /// than they look like they do.
    /// </summary>
    internal static class DoorOps
    {
        /// <summary>
        /// Looks up a named door. Only about 38 doors carry a nametag, and the nametag table is not
        /// ready at MapGenerated, so call this at WaitingForPlayers or later. It can still return
        /// null for a frame because last round's entries linger. Cache the result for the round,
        /// not for the plugin's lifetime.
        /// </summary>
        public static Door Find(DoorName name)
        {
            Door door = Door.Get(name);
            return door == null || door.IsDestroyed ? null : door;
        }

        /// <summary>
        /// Sets or clears the AdminCommand bit, which is what Door.IsLocked = x also does, but says
        /// so. It is the same bit RemoteAdmin and every other plugin uses, so unlocking here also
        /// clears their lock. Lock(reason, true/false) is the call that touches exactly one bit.
        /// </summary>
        public static void SetPluginLock(Door door, bool locked)
        {
            if (door == null || door.IsDestroyed)
                return;
            door.Lock(DoorLockReason.AdminCommand, locked);
        }

        /// <summary>
        /// Door.IsLocked is "ActiveLocks > 0", but its setter only changes the AdminCommand bit, so
        /// IsLocked = false does NOT unlock a door still held by warhead, decontamination, SCP-079 or
        /// isolation bits. Read LockReason to see what remains. Do not clear game-owned bits: the
        /// game re-adds them.
        /// </summary>
        public static bool UnlockAndReport(Door door, out DoorLockReason remaining)
        {
            remaining = DoorLockReason.None;
            if (door == null || door.IsDestroyed)
                return false;

            door.Lock(DoorLockReason.AdminCommand, false);
            remaining = door.LockReason;
            return remaining == DoorLockReason.None;
        }

        /// <summary>
        /// Breaks a door the supported way. TryDamage returns false if the door has no broken prefab
        /// or the damage type is ignored, so check the result. Do not write Health = 0 (it only
        /// writes a property) or IsBroken = true (it deals exactly max health and fails when the
        /// remaining health is higher).
        /// </summary>
        public static bool Break(Door door)
        {
            if (door == null || door.IsDestroyed)
                return false;
            if (!(door is BreakableDoor breakable))
                return false; // gates, checkpoints and bulkheads have their own wrappers

            return breakable.TryDamage(float.MaxValue, DoorDamageType.ServerCommand);
        }

        // Deliberately absent: Door.IsOpened = true. It writes the target state directly and ignores
        // locks, permissions and the animation cooldown, so it is not "open this for the player".
        // To let a player through a locked door, set CanOpen = true in the InteractingDoor event.
    }
}
