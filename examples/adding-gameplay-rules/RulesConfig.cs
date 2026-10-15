using System.Collections.Generic;
using System.ComponentModel;
using LabApi.Features.Enums;
using PlayerRoles;

namespace Examples.AddingGameplayRules
{
    // Config is a schema: public get/set properties only, defaults as initializers. Collections
    // REPLACE their defaults when the file lists them. Every rule ships disabled, so dropping the
    // DLL into a server changes nothing until the owner opts in.
    public sealed class RulesConfig
    {
        [Description("Master kill switch. false unsubscribes every handler on 'labapi reload configs'.")]
        public bool IsEnabled { get; set; } = true;

        [Description("Seconds between per-player 'denied' hints. Denials can repeat every frame (escape zone).")]
        public float NoticeCooldownSeconds { get; set; } = 5f;

        [Description("Hint shown when a rule denies an action. Empty = no hint.")]
        public string DenyMessage { get; set; } = "You cannot do that.";

        [Description("Seconds between one-line rule summaries in the server log. 0 = never.")]
        public int SummarySeconds { get; set; } = 300;

        public FriendlyFireRule FriendlyFire { get; set; } = new FriendlyFireRule();
        public DamageRule Damage { get; set; } = new DamageRule();
        public DoorRule Doors { get; set; } = new DoorRule();
        public ElevatorRule Elevators { get; set; } = new ElevatorRule();
        public PickupRule Pickups { get; set; } = new PickupRule();
        public EscapeRule Escape { get; set; } = new EscapeRule();
    }

    public sealed class FriendlyFireRule
    {
        [Description("Same-faction damage between two different players (SCP, Tutorial and dead excluded).")]
        public bool Enabled { get; set; }

        [Description("true = cancel the hit. false = multiply raw damage by Scale (the game's own friendly-fire multiplier still applies afterwards).")]
        public bool Block { get; set; } = true;

        public float Scale { get; set; } = 0.5f;
    }

    public sealed class DamageRule
    {
        [Description("Multipliers for damage dealt BY a player. Environmental damage (warhead, bleeding, tesla) is never touched.")]
        public bool Enabled { get; set; }

        [Description("Multiplier by VICTIM role. Missing role = 1. Composes with the attacker table by multiplication.")]
        public Dictionary<RoleTypeId, float> ByVictimRole { get; set; } = new Dictionary<RoleTypeId, float>
        {
            { RoleTypeId.Scp173, 0.8f },
        };

        [Description("Multiplier by ATTACKER role. Missing role = 1.")]
        public Dictionary<RoleTypeId, float> ByAttackerRole { get; set; } = new Dictionary<RoleTypeId, float>
        {
            { RoleTypeId.ClassD, 1.1f },
        };
    }

    public sealed class DoorRule
    {
        [Description("Deny and grant rules for door interaction.")]
        public bool Enabled { get; set; }

        [Description("Roles that cannot interact with the named door (the player gets the denial hint).")]
        public Dictionary<DoorName, List<RoleTypeId>> Deny { get; set; } = new Dictionary<DoorName, List<RoleTypeId>>
        {
            { DoorName.LczArmory, new List<RoleTypeId> { RoleTypeId.ClassD, RoleTypeId.Scientist } },
        };

        [Description("Roles that open any UNLOCKED door that only fails on keycard permission. Never opens locked doors.")]
        public List<RoleTypeId> OpenWithoutKeycard { get; set; } = new List<RoleTypeId> { RoleTypeId.FacilityGuard };
    }

    public sealed class ElevatorRule
    {
        public bool Enabled { get; set; }

        [Description("Roles that cannot call or ride elevators.")]
        public List<RoleTypeId> DenyRoles { get; set; } = new List<RoleTypeId> { RoleTypeId.ClassD };
    }

    public sealed class PickupRule
    {
        public bool Enabled { get; set; }

        [Description("Items a role cannot pick up. Checked when the search COMPLETES, so the player sees the progress bar first.")]
        public Dictionary<RoleTypeId, List<ItemType>> Deny { get; set; } = new Dictionary<RoleTypeId, List<ItemType>>
        {
            { RoleTypeId.ClassD, new List<ItemType> { ItemType.MicroHID } },
        };
    }

    public sealed class EscapeRule
    {
        [Description("HOT event: subscribed only while this is true.")]
        public bool Enabled { get; set; }

        [Description("Roles that cannot escape. The player stays in the zone, so the handler keeps running every frame until they leave.")]
        public List<RoleTypeId> BlockRoles { get; set; } = new List<RoleTypeId>();

        [Description("Old role -> role granted on escape, replacing the game's choice.")]
        public Dictionary<RoleTypeId, RoleTypeId> NewRole { get; set; } = new Dictionary<RoleTypeId, RoleTypeId>
        {
            { RoleTypeId.Scientist, RoleTypeId.NtfSergeant },
        };
    }
}
