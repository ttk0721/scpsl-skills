using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operations 3 and 4: give an item (and remember which ones are yours), give or drop ammo.
    /// </summary>
    internal sealed class ItemGiver
    {
        // Serials are the identity of a logical item: a held item and the pickup it becomes share one.
        // Never persist them to disk or a database. The counter is a ushort that wraps and is not
        // reset per round, so a long-lived set can eventually give false hits.
        private readonly HashSet<ushort> _tagged = new HashSet<ushort>();

        public int TaggedCount => _tagged.Count;

        /// <summary>Test on both Item and Pickup events: the serial is the same on both sides.</summary>
        public bool IsTagged(ushort serial) => _tagged.Contains(serial);

        /// <summary>
        /// Gives one non-ammo item and tags it. Returns null when nothing usable was added.
        /// </summary>
        /// <remarks>
        /// AddItem returns null when the player already holds 8 non-ammo items or the type is
        /// unknown, and throws if the server is inactive (so never call it in Enable). Category
        /// limits (for example keycards) are enforced only on the pick-up path, so an API give
        /// bypasses them. Item changes sync as one full-list refresh per player per frame: giving
        /// eight items in one frame is one message. A firearm clones a large prefab and broadcasts
        /// a small message to every connection, so batch gun gives instead of looping per tick.
        /// </remarks>
        public Item Give(Player player, ItemType type)
        {
            if (player == null || player.IsDestroyed || !player.IsAlive)
                return null;
            if (IsAmmo(type))
                return null; // AddItem(ammo) returns an item that converts to reserve ammo and removes itself
            if (player.IsInventoryFull)
                return null;

            Item item = player.AddItem(type); // reason defaults to AdminCommand
            if (item == null || item.Serial == 0)
                return null;

            _tagged.Add(item.Serial);
            return item;
        }

        /// <summary>
        /// Run from PlayerEvents.Left. Disconnect removes every held item with no pickup, so a
        /// tagged item does NOT survive its owner on the map, and dead serials would count toward
        /// any cap you keep forever. Whether the inventory is still readable inside Left is
        /// UNVERIFIED; this tolerates an empty one, and the round-boundary Clear is the backstop.
        /// </summary>
        public void ForgetHeldBy(Player leaver)
        {
            if (leaver == null || _tagged.Count == 0)
                return;

            // Snapshot first: Player.Items is a lazy Select that creates wrappers on first sight.
            // Do this on Left only, never on a timer.
            foreach (Item held in leaver.Items.ToArray())
            {
                if (held != null)
                    _tagged.Remove(held.Serial);
            }
        }

        /// <summary>Call from WaitingForPlayers. A round restart raises no per-item removal notice.</summary>
        public void Clear() => _tagged.Clear();

        /// <summary>
        /// Validates a stored serial before acting on it. Item.Get(ushort) is a closure allocation
        /// and a linear scan on a miss, and it only knows inventory items: a serial that is
        /// currently a pickup returns null. Fine per admin action, wrong per frame.
        /// </summary>
        public static bool IsStillHeldAs(ushort serial, ItemType expected)
        {
            Item item = Item.Get(serial);
            return item != null && !item.IsDestroyed && item.Type == expected;
        }

        private static bool IsAmmo(ItemType type)
        {
            switch (type)
            {
                case ItemType.Ammo9x19:
                case ItemType.Ammo12gauge:
                case ItemType.Ammo44cal:
                case ItemType.Ammo556x45:
                case ItemType.Ammo762x39:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Operation 4: ammo has its own API. The live dictionary is not the way in.</summary>
    internal static class AmmoOps
    {
        /// <summary>
        /// AddAmmo and SetAmmo are clamped to 0..65535 with no per-type cap. Player.Ammo is the live
        /// dictionary and a direct write never reaches the client.
        /// </summary>
        public static void Give(Player player, ItemType ammoType, ushort amount)
        {
            if (player == null || player.IsDestroyed)
                return;
            player.AddAmmo(ammoType, amount);
        }

        /// <summary>
        /// DropAmmo defaults checkMinimals to true, which raises a small request to about half a
        /// pickup's worth, so DropAmmo(type, 5) can drop far more than 5. Pass false for exact amounts.
        /// The result is one wrapper per spawned ammo pickup.
        /// </summary>
        public static List<AmmoPickup> DropExactly(Player player, ItemType ammoType, ushort amount)
        {
            var dropped = new List<AmmoPickup>();
            if (player == null || player.IsDestroyed)
                return dropped;
            dropped.AddRange(player.DropAmmo(ammoType, amount, checkMinimals: false));
            return dropped;
        }
    }
}
