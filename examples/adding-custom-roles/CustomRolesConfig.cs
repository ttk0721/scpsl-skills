using System;
using System.Collections.Generic;
using System.ComponentModel;
using PlayerRoles;

namespace Examples.AddingCustomRoles
{
    /// <summary>Public get/set properties only; [Description] becomes the YAML comment.</summary>
    public sealed class CustomRolesConfig
    {
        [Description("Kill switch. false = no new assignments, no ability ticks, and current holders are released within a second. Read at use time, so a config reload applies it.")]
        public bool IsEnabled { get; set; } = true;

        [Description("Seconds the 'you are X' hint stays up (0 = no hint). Replace with your HUD owner's channel if the server already runs a HUD.")]
        public float AnnounceHintSeconds { get; set; } = 5f;

        [Description("Log every assignment. Assignments are rare, but keep it off in normal operation.")]
        public bool Debug { get; set; } = false;

        [Description("Custom role definitions. A definition with Chance 0 is only reachable through the 'customrole' command.")]
        public List<CustomRoleDefinition> Roles { get; set; } = DefaultRoles();

        /// <summary>
        /// Clamps edited values. Every limit has a reason: stats go on the wire as ushort, effect intensity
        /// is a byte, and an out-of-range intensity changes the meaning of the effect, it is not clamped by the game.
        /// </summary>
        public void Normalize()
        {
            if (Roles == null) Roles = new List<CustomRoleDefinition>();
            if (!(AnnounceHintSeconds >= 0f)) AnnounceHintSeconds = 0f;   // the negated test also catches NaN
            if (AnnounceHintSeconds > 30f) AnnounceHintSeconds = 30f;

            foreach (CustomRoleDefinition d in Roles)
            {
                if (d == null) continue;
                if (d.Id == null) d.Id = string.Empty;
                if (d.Items == null) d.Items = new List<ItemType>();
                if (d.Ammo == null) d.Ammo = new Dictionary<ItemType, ushort>();
                if (!(d.Chance >= 0f)) d.Chance = 0f;
                if (d.Chance > 1f) d.Chance = 1f;
                if (d.MaxAlive < 0) d.MaxAlive = 0;
                d.MaxHealth = Clamp(d.MaxHealth, 0f, 5000f);
                d.ArtificialHealth = Clamp(d.ArtificialHealth, 0f, 1000f);
                d.DamageReduction = Clamp(d.DamageReduction, 0, 100);   // 0.5 % per step; >= 200 would zero all damage
                d.MovementBoost = Clamp(d.MovementBoost, 0, 100);       // + percent
                d.RegenPerSecond = Clamp(d.RegenPerSecond, 0, 20);
                d.LeechPercent = Clamp(d.LeechPercent, 0, 100);
            }
        }

        private static float Clamp(float v, float min, float max)
        {
            if (!(v >= min)) return min;
            return v > max ? max : v;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private static List<CustomRoleDefinition> DefaultRoles()
        {
            return new List<CustomRoleDefinition>
            {
                new CustomRoleDefinition
                {
                    Id = "field-medic",
                    DisplayName = "Field Medic",
                    BaseRole = RoleTypeId.NtfSpecialist,
                    Chance = 0.25f,
                    MaxAlive = 2,
                    MaxHealth = 125f,
                    MovementBoost = 10,
                    RegenPerSecond = 1,
                    Items = new List<ItemType>
                    {
                        ItemType.KeycardMTFOperative, ItemType.Radio, ItemType.ArmorCombat, ItemType.GunFSP9,
                        ItemType.Medkit, ItemType.Medkit, ItemType.Painkillers, ItemType.Adrenaline,
                    },
                    Ammo = new Dictionary<ItemType, ushort> { { ItemType.Ammo9x19, 120 } },
                },
                new CustomRoleDefinition
                {
                    Id = "heavy-guard",
                    DisplayName = "Heavy Guard",
                    HideVanillaRole = true,
                    BaseRole = RoleTypeId.FacilityGuard,
                    Chance = 0.2f,
                    MaxAlive = 1,
                    MaxHealth = 150f,
                    ArtificialHealth = 50f,
                    DamageReduction = 20,
                    LeechPercent = 15,
                    Items = new List<ItemType>
                    {
                        ItemType.KeycardGuard, ItemType.Radio, ItemType.ArmorHeavy, ItemType.GunE11SR, ItemType.Medkit,
                    },
                    Ammo = new Dictionary<ItemType, ushort> { { ItemType.Ammo556x45, 120 } },
                },
            };
        }
    }

    public sealed class CustomRoleDefinition
    {
        [Description("Unique key, used by the command and by other plugins. Never shown to players.")]
        public string Id { get; set; } = string.Empty;

        [Description("Shown over the player's head and in the hint. Subject to the game's custom-info rules (length, allowed tags); invalid text is skipped with a warning.")]
        public string DisplayName { get; set; } = string.Empty;

        [Description("Hide the vanilla role name over the head (the custom text replaces it). Teammates then identify the role by text only.")]
        public bool HideVanillaRole { get; set; } = false;

        [Description("The game role the player really has. Team, faction, spawnpoint, SCP/human rules and every other plugin see THIS role.")]
        public RoleTypeId BaseRole { get; set; } = RoleTypeId.ClassD;

        [Description("Probability (0-1) that a vanilla spawn into BaseRole (round start, wave, late join) becomes this role. 0 = command only.")]
        public float Chance { get; set; } = 0f;

        [Description("Most holders alive at once (0 = unlimited).")]
        public int MaxAlive { get; set; } = 1;

        [Description("Max and starting health (0 = keep the role's default).")]
        public float MaxHealth { get; set; } = 0f;

        [Description("Starting artificial health (0 = none).")]
        public float ArtificialHealth { get; set; } = 0f;

        [Description("Replaces the base role's loadout. Empty = keep the vanilla loadout.")]
        public List<ItemType> Items { get; set; } = new List<ItemType>();

        [Description("Replaces the base role's ammo (only used when Items is not empty).")]
        public Dictionary<ItemType, ushort> Ammo { get; set; } = new Dictionary<ItemType, ushort>();

        [Description("Permanent damage reduction effect, 0.5 percent per step (20 = 10 percent). 0 = off.")]
        public int DamageReduction { get; set; } = 0;

        [Description("Permanent movement boost effect, in percent. 0 = off.")]
        public int MovementBoost { get; set; } = 0;

        [Description("Health regained per second, in whole points. 0 = off.")]
        public int RegenPerSecond { get; set; } = 0;

        [Description("Percent of dealt health damage returned to the holder as healing. 0 = off.")]
        public int LeechPercent { get; set; } = 0;

        [Description("Disable this definition without deleting it.")]
        public bool Enabled { get; set; } = true;

        /// <summary>True when a vanilla spawn flow may roll this definition.</summary>
        public bool IsRollable(RoleTypeId newRole) => Enabled && Chance > 0f && BaseRole == newRole;
    }
}
