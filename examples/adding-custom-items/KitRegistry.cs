using System.Collections.Generic;
using LabApi.Features.Wrappers;

namespace Examples.AddingCustomItems
{
    /// <summary>What we remember about one tagged item. Plain values only, never a wrapper.</summary>
    internal sealed class KitTag
    {
        public ItemType Type;
        public string OwnerUserId;
        public int MissedSweeps;
    }

    /// <summary>
    /// The tag store: serial -> what that serial is supposed to be. The serial is the only identity
    /// an item keeps through inventory, drop, pick-up and a second owner; the Item and Pickup wrappers
    /// are different objects that come and go, so nothing here holds one.
    /// Main thread only (the wrappers and the game's own serial tables are not thread-safe).
    /// </summary>
    internal sealed class KitRegistry
    {
        // Two sweeps in a row must miss the serial before we forget it. See IsAlive.
        private const int StrikesToPrune = 2;

        private readonly Dictionary<ushort, KitTag> _tags = new Dictionary<ushort, KitTag>();
        private readonly List<ushort> _scratch = new List<ushort>();

        public int Count => _tags.Count;

        public void Mark(ushort serial, ItemType type, string ownerUserId)
        {
            // Serial 0 means "nothing" in the game. Overwrite on purpose: if a stale entry sits on a
            // recycled serial, the new item must win.
            if (serial == 0)
                return;
            _tags[serial] = new KitTag { Type = type, OwnerUserId = ownerUserId };
        }

        /// <summary>
        /// The one lookup every handler uses. The caller passes the type of the object it is holding,
        /// and a tag whose type disagrees is stale (the 16-bit serial counter wrapped and was reused
        /// for another item), so it is dropped on the spot instead of treating a stranger as ours.
        /// </summary>
        public bool TryGet(ushort serial, ItemType actualType, out KitTag tag)
        {
            tag = null;
            // Cheapest test first: with no custom items alive, every handler stops here.
            if (_tags.Count == 0 || serial == 0 || !_tags.TryGetValue(serial, out KitTag found))
                return false;

            if (found.Type != actualType)
            {
                _tags.Remove(serial);
                return false;
            }

            tag = found;
            return true;
        }

        public void Remove(ushort serial) => _tags.Remove(serial);

        /// <summary>
        /// Called at WaitingForPlayers and RoundRestarted. A restart destroys every pickup without a
        /// per-item removal event and the serial counter is not reset, so this is the only reliable
        /// moment to forget everything. Idempotent: RoundRestarted can fire twice.
        /// </summary>
        public void Clear() => _tags.Clear();

        /// <summary>
        /// Forgets serials that no longer exist as an item or a pickup. Returns how many were dropped.
        /// Why a sweep at all: this example caps live kits, so a kit that vanished (owner left, SCP-914,
        /// a cleanup plugin) must free its slot, and no single event covers every way an item dies.
        /// </summary>
        public int Sweep()
        {
            if (_tags.Count == 0)
                return 0;

            _scratch.Clear();
            foreach (KeyValuePair<ushort, KitTag> pair in _tags)
            {
                if (IsAlive(pair.Key, pair.Value.Type))
                    pair.Value.MissedSweeps = 0;
                else if (++pair.Value.MissedSweeps >= StrikesToPrune)
                    _scratch.Add(pair.Key);
            }

            for (int i = 0; i < _scratch.Count; i++)
                _tags.Remove(_scratch[i]);

            int pruned = _scratch.Count;
            _scratch.Clear();
            return pruned;
        }

        /// <summary>
        /// "Does this serial still exist as the thing we tagged?" Two strikes in Sweep because one
        /// miss proves little: a pickup created by game code (the death drop) gets its LabAPI wrapper a
        /// step after the item vanished, so a check in that gap sees neither. A miss that survives a
        /// whole sweep period is real.
        /// </summary>
        private static bool IsAlive(ushort serial, ItemType type)
        {
            // Held: Item.TryGet(serial) only knows inventory items (a serial that is a pickup misses).
            if (Item.TryGet(serial, out Item item) && !item.IsDestroyed && item.Type == type)
                return true;

            // On the floor: pure cache lookup, no rescan.
            if (Pickup.TryGet(serial, out Pickup cached) && !cached.IsDestroyed && cached.Type == type)
                return true;

            // Pickup.TryGet can miss a live pickup after a same-frame pick-up and re-drop, so look at
            // the full list before declaring it dead. Only reached for serials that already missed,
            // and nothing in this loop creates a wrapper, so no snapshot is needed.
            foreach (Pickup pickup in Pickup.List)
            {
                if (pickup.Serial == serial && pickup.Type == type && !pickup.IsDestroyed)
                    return true;
            }

            return false;
        }
    }
}
