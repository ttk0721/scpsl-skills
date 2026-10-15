using System.Collections.Generic;
using System.ComponentModel;
using LabApi.Features.Enums;
using MapGeneration;

namespace Examples.AddingMapAutomation
{
    /// <summary>
    /// Public get/set properties only (fields are ignored). Read <c>Config.X</c> at use time, never
    /// cache the object: a config reload replaces it, and an empty file gives null.
    /// </summary>
    public sealed class AutomationConfig
    {
        [Description("Master kill switch. false: the plugin does nothing. Read at every round boundary, so a config reload takes effect from the next WaitingForPlayers.")]
        public bool IsEnabled { get; set; } = true;

        // --- doors ---
        [Description("Lock the named doors with the AdminCommand bit at round start.")]
        public bool LockDoors { get; set; } = true;

        [Description("Doors to lock. Only about 38 doors have a name; every other door needs a room-based lookup.")]
        public List<DoorName> DoorsToLock { get; set; } = new List<DoorName> { DoorName.LczArmory, DoorName.HczArmory };

        [Description("Seconds after round start until those doors are unlocked.")]
        public int DoorLockSeconds { get; set; } = 120;

        // --- tesla ---
        [Description("Switch every tesla gate off at round start.")]
        public bool DisableTesla { get; set; } = true;

        [Description("Seconds after round start until the gates work again.")]
        public int TeslaOffSeconds { get; set; } = 60;

        // --- elevators ---
        [Description("Elevators pick a random floor at round start. Send them all to ParkLevel once.")]
        public bool ParkElevators { get; set; } = true;

        [Description("Level index to park at (0 is the first floor of that elevator).")]
        public int ParkLevel { get; set; } = 0;

        // --- lights ---
        [Description("Tint the lights of AlertZones red while the warhead countdown runs.")]
        public bool WarheadAlertLights { get; set; } = true;

        [Description("Zones that get the red tint.")]
        public List<FacilityZone> AlertZones { get; set; } = new List<FacilityZone>
        {
            FacilityZone.LightContainment, FacilityZone.HeavyContainment, FacilityZone.Entrance,
        };

        // --- admin toys ---
        [Description("Spawn a static marker (light plus floor disc) in every room with one of these names. RoomName is not unique: HczTesla and the checkpoint rooms exist several times.")]
        public bool RoomMarkers { get; set; } = true;

        [Description("Room names that get a marker.")]
        public List<RoomName> MarkerRooms { get; set; } = new List<RoomName> { RoomName.Lcz914, RoomName.Hcz079, RoomName.HczWarhead };

        [Description("Hard cap on markers per round. Each marker is two network identities held for the whole round.")]
        public int MaxMarkers { get; set; } = 12;

        [Description("Spawn byte budget per 100 ms tick: markers x 2 toys x ~134 B x players must stay below it.")]
        public int SpawnBytesPerTick { get; set; } = 150000;
    }
}
