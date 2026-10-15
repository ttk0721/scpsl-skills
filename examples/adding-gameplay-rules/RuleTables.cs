using System;
using System.Collections.Generic;
using System.Diagnostics;
using PlayerRoles;

namespace Examples.AddingGameplayRules
{
    /// <summary>
    /// Immutable, array-indexed copy of <see cref="RulesConfig"/>. Handlers read one volatile-ish
    /// field (<c>_t</c>) and then only index arrays: no YAML objects, no enum-keyed dictionaries
    /// (their hashing may box on this runtime), no allocation. A config reload builds a new
    /// instance and swaps the reference, so a handler never sees half a reload.
    /// </summary>
    internal sealed class RuleTables
    {
        // RoleTypeId and ItemType are small signed enums starting at None = -1; slot = value + 1.
        // Out-of-range values (a future game build) map to -1 and are ignored, never thrown on.
        public const int RoleSlots = 64;
        public const int ItemSlots = 160;

        public readonly bool FriendlyFireOn;
        public readonly bool FriendlyFireBlock;
        public readonly float FriendlyFireScale;

        public readonly bool DamageOn;
        public readonly float[] VictimMultiplier;    // by role slot, 1 = untouched
        public readonly float[] AttackerMultiplier;

        public readonly bool DoorsOn;
        public readonly Dictionary<int, bool[]> DoorDeny;   // (int)DoorName -> denied role slots
        public readonly bool[] DoorGrant;                   // role slot -> may open without keycard

        public readonly bool ElevatorsOn;
        public readonly bool[] ElevatorDeny;

        public readonly bool PickupsOn;
        public readonly bool[][] PickupDeny;                // role slot -> item slots, null = nothing denied

        public readonly bool EscapeOn;
        public readonly bool[] EscapeBlock;
        public readonly RoleTypeId[] EscapeRole;            // None = no override

        public readonly string DenyMessage;
        public readonly long NoticeTicks;
        public readonly int SummarySeconds;

        // One handler serves two rules, so it is wired when either needs it.
        public bool HurtingNeeded => FriendlyFireOn || DamageOn;

        public RuleTables(RulesConfig c)
        {
            // The master switch is applied here, once: a disabled plugin compiles to "no rules".
            bool on = c.IsEnabled;

            FriendlyFireOn = on && c.FriendlyFire != null && c.FriendlyFire.Enabled;
            FriendlyFireBlock = c.FriendlyFire != null && c.FriendlyFire.Block;
            FriendlyFireScale = Clamp(c.FriendlyFire?.Scale ?? 1f);

            DamageOn = on && c.Damage != null && c.Damage.Enabled;
            VictimMultiplier = BuildMultipliers(c.Damage?.ByVictimRole);
            AttackerMultiplier = BuildMultipliers(c.Damage?.ByAttackerRole);

            DoorsOn = on && c.Doors != null && c.Doors.Enabled;
            DoorDeny = new Dictionary<int, bool[]>();
            if (c.Doors?.Deny != null)
                foreach (var pair in c.Doors.Deny)
                    DoorDeny[(int)pair.Key] = BuildRoleSet(pair.Value);
            DoorGrant = BuildRoleSet(c.Doors?.OpenWithoutKeycard);

            ElevatorsOn = on && c.Elevators != null && c.Elevators.Enabled;
            ElevatorDeny = BuildRoleSet(c.Elevators?.DenyRoles);

            PickupsOn = on && c.Pickups != null && c.Pickups.Enabled;
            PickupDeny = new bool[RoleSlots][];
            if (c.Pickups?.Deny != null)
            {
                foreach (var pair in c.Pickups.Deny)
                {
                    int role = Slot((int)pair.Key, RoleSlots);
                    if (role < 0 || pair.Value == null) continue;
                    var items = new bool[ItemSlots];
                    foreach (ItemType item in pair.Value)
                    {
                        int slot = Slot((int)item, ItemSlots);
                        if (slot >= 0) items[slot] = true;
                    }
                    PickupDeny[role] = items;
                }
            }

            EscapeOn = on && c.Escape != null && c.Escape.Enabled;
            EscapeBlock = BuildRoleSet(c.Escape?.BlockRoles);
            EscapeRole = new RoleTypeId[RoleSlots];
            if (c.Escape?.NewRole != null)
            {
                foreach (var pair in c.Escape.NewRole)
                {
                    int role = Slot((int)pair.Key, RoleSlots);
                    if (role >= 0) EscapeRole[role] = pair.Value;
                }
            }

            DenyMessage = c.DenyMessage ?? string.Empty;
            NoticeTicks = (long)(Math.Max(0f, c.NoticeCooldownSeconds) * Stopwatch.Frequency);
            SummarySeconds = on ? Math.Max(0, c.SummarySeconds) : 0;
        }

        public static int Slot(int value, int size)
        {
            int slot = value + 1;
            return slot >= 0 && slot < size ? slot : -1;
        }

        private static bool[] BuildRoleSet(List<RoleTypeId> roles)
        {
            var set = new bool[RoleSlots];
            if (roles == null) return set;
            foreach (RoleTypeId role in roles)
            {
                int slot = Slot((int)role, RoleSlots);
                if (slot >= 0) set[slot] = true;
            }
            return set;
        }

        private static float[] BuildMultipliers(Dictionary<RoleTypeId, float> table)
        {
            var result = new float[RoleSlots];
            for (int i = 0; i < result.Length; i++) result[i] = 1f;
            if (table == null) return result;
            foreach (var pair in table)
            {
                int slot = Slot((int)pair.Key, RoleSlots);
                if (slot >= 0) result[slot] = Clamp(pair.Value);
            }
            return result;
        }

        // A typo in the YAML (NaN, -3, 9000) must not become a one-shot kill or a heal.
        private static float Clamp(float v) => float.IsNaN(v) ? 1f : Math.Max(0f, Math.Min(10f, v));
    }
}
