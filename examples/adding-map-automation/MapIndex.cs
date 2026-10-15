using System.Collections.Generic;
using System.Text;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using MapGeneration;

namespace Examples.AddingMapAutomation
{
    /// <summary>
    /// Everything this plugin needs from the facility, resolved ONCE per round into plain lists.
    /// A round restart reloads the scene, so every wrapper in here is dead afterwards: the plugin
    /// drops the whole object and builds a new one, it never reuses entries.
    /// </summary>
    internal sealed class MapIndex
    {
        public readonly List<Door> DoorsToLock = new List<Door>(8);
        public readonly List<Elevator> Elevators = new List<Elevator>(11);
        public readonly List<Tesla> Teslas = new List<Tesla>(16);
        public readonly Dictionary<FacilityZone, List<LightsController>> LightsByZone =
            new Dictionary<FacilityZone, List<LightsController>>(8);
        public readonly List<Room> MarkerRooms = new List<Room>(16);

        /// <summary>Doors this plugin locked, so only those are touched on unlock.</summary>
        public readonly List<Door> LockedByUs = new List<Door>(8);

        public int SpawnedToys;

        /// <summary>
        /// Resolves the index. Returns it even when incomplete; <paramref name="missing"/> says what
        /// was not registered yet (null when complete). Several collections register in the
        /// objects' Start step, and WaitingForPlayers is only "almost certainly" after it, so the
        /// caller retries a few frames later instead of trusting an empty list.
        /// </summary>
        public static MapIndex Build(AutomationConfig cfg, out string missing)
        {
            var index = new MapIndex();
            StringBuilder why = null;

            if (cfg.LockDoors)
            {
                foreach (DoorName name in cfg.DoorsToLock)
                {
                    // Door.Get(DoorName) can hand back a stale entry from the previous round
                    // until the new nametag extension has run its Start step: check both.
                    Door door = Door.Get(name);
                    if (door == null || door.IsDestroyed)
                        (why ??= new StringBuilder()).Append("door ").Append(name).Append("; ");
                    else
                        index.DoorsToLock.Add(door);
                }
            }

            if (cfg.ParkElevators)
            {
                foreach (Elevator e in Elevator.List)
                    index.Elevators.Add(e);
                if (index.Elevators.Count == 0)
                    (why ??= new StringBuilder()).Append("elevators; ");
            }

            if (cfg.DisableTesla)
            {
                foreach (Tesla t in Tesla.List)
                    index.Teslas.Add(t);
                if (index.Teslas.Count == 0)
                    (why ??= new StringBuilder()).Append("tesla; ");
            }

            if (cfg.WarheadAlertLights)
            {
                foreach (LightsController lc in LightsController.List)
                {
                    Room lightRoom = lc.Room; // wrapper does Room.Get(...)!: null if the room wrapper is not built yet
                    if (lightRoom == null)
                        continue;
                    FacilityZone zone = lightRoom.Zone;
                    if (!cfg.AlertZones.Contains(zone))
                        continue;
                    if (!index.LightsByZone.TryGetValue(zone, out List<LightsController> list))
                        index.LightsByZone[zone] = list = new List<LightsController>(32);
                    list.Add(lc);
                }
                if (index.LightsByZone.Count == 0)
                    (why ??= new StringBuilder()).Append("lights; ");
            }

            if (cfg.RoomMarkers)
            {
                // Room.List is complete from MapGenerated on, but RoomName is a prefab family, not
                // an id: collect every match and cap the count instead of taking the first.
                foreach (Room room in Room.List)
                {
                    if (index.MarkerRooms.Count >= cfg.MaxMarkers)
                        break;
                    if (cfg.MarkerRooms.Contains(room.Name))
                        index.MarkerRooms.Add(room);
                }
            }

            missing = why?.ToString();
            return index;
        }
    }
}
