using System.Collections.Generic;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operation 5: spawn and move pickups. Pickup.Create already network-spawns, every pickup is
    /// visible to every connection, and a pickup you configure before spawning is yours to spawn or
    /// destroy.
    /// </summary>
    internal static class PickupOps
    {
        /// <summary>
        /// The simple case. Create(type, position) spawns the pickup on the network (networkSpawn
        /// defaults to true), so there is no second step: calling Spawn() afterwards only logs a
        /// warning. Returns null for ItemType.None or a type with no template.
        /// </summary>
        public static Pickup Spawn(ItemType type, Vector3 position) => Pickup.Create(type, position);

        /// <summary>
        /// Configure first, spawn once. With networkSpawn: false the pickup is a simulated
        /// server-only object: no traffic, no out-of-range cleanup. You now own the obligation to
        /// Spawn() or Destroy() it, or it lingers until round restart.
        /// </summary>
        public static Pickup SpawnLocked(ItemType type, Vector3 position)
        {
            Pickup pickup = Pickup.Create(type, position, Quaternion.identity, Vector3.one, networkSpawn: false);
            if (pickup == null)
                return null;

            try
            {
                pickup.IsLocked = true; // blocks pick-up
                pickup.Spawn();
            }
            catch
            {
                pickup.Destroy(); // never leave a half-configured, unspawned object behind
                throw;
            }
            return pickup;
        }

        /// <summary>
        /// Moves a pickup into an inventory. Player.AddItem(Pickup) does NOT destroy the source, so
        /// without the Destroy the player holds the item AND a live pickup with the same serial
        /// exists on the map. Do not use Item.MoveTo: it has the same defect.
        /// </summary>
        public static Item PickUp(Player player, Pickup pickup)
        {
            if (player == null || player.IsDestroyed || pickup == null || pickup.IsDestroyed)
                return null;

            Item item = player.AddItem(pickup);
            if (item == null)
                return null; // inventory full or refused: the pickup stays where it was

            // Exceptions: an SCP-330 pickup keeps the candies that did not fit, and ammo pickups
            // convert to reserve ammo. Do not blindly destroy those.
            if (pickup.Type != ItemType.SCP330 && !IsAmmoPickup(pickup))
                pickup.Destroy(); // the network object vanishes now, the wrapper at end of frame
            return item;
        }

        /// <summary>
        /// Builds a loaded gun with chosen attachments and drops it. A pickup made by Pickup.Create
        /// has no magazine and default attachments, because that state lives in serial-keyed game
        /// tables and Create writes no entry. So give the item to a holder, set the code first (it
        /// changes the magazine maximum), then the ammo, then drop it. Needs a free slot.
        /// </summary>
        public static Pickup DropLoadedGun(Player holder, ItemType gunType, uint attachmentsCode)
        {
            if (holder == null || holder.IsDestroyed || holder.IsInventoryFull)
                return null;

            if (!(holder.AddItem(gunType) is FirearmItem gun))
                return null;

            gun.AttachmentsCode = attachmentsCode;
            // The game may overwrite the code with the player's saved preference during item setup
            // before this setter runs; read it back if the exact attachments matter.
            gun.StoredAmmo = gun.MaxAmmo;
            return gun.DropItem(); // the drop keeps the serial, so the magazine and code travel with it
        }

        private static bool IsAmmoPickup(Pickup pickup) => pickup is AmmoPickup;
    }

    /// <summary>
    /// A budgeted spawn queue. Each spawn is serialised once per connection, so a burst of hundreds
    /// in one frame costs tens of milliseconds at 100 players (an estimate, not a measurement).
    /// Enqueue everything, then let a clock call Pump a few times per second.
    /// </summary>
    internal sealed class PickupBurst
    {
        private struct Request
        {
            public ItemType Type;
            public Vector3 Position;
        }

        // Requests hold plain values only: no wrappers are kept, so nothing here can go stale.
        private readonly Queue<Request> _queue = new Queue<Request>();

        public int Pending => _queue.Count;

        public void Enqueue(ItemType type, Vector3 position)
            => _queue.Enqueue(new Request { Type = type, Position = position });

        /// <summary>Spawns at most budget pickups. Returns how many were created.</summary>
        public int Pump(int budget)
        {
            int created = 0;
            while (created < budget && _queue.Count > 0)
            {
                Request request = _queue.Dequeue();
                Pickup pickup = Pickup.Create(request.Type, request.Position);
                if (pickup == null)
                    continue; // None or no template: skip, do not retry forever

                created++;
            }
            return created;
        }

        /// <summary>
        /// Drops the queue. A round restart destroys every spawned pickup itself (and raises
        /// PickupDestroyed once per pickup), so there is nothing to destroy: only forget the backlog.
        /// </summary>
        public void Clear() => _queue.Clear();
    }
}
