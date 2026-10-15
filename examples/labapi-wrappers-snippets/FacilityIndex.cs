using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MapGeneration;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Shared base of operations 6 (doors) and 7 (rooms). A round restart reloads the scene, so every
    /// Room and Door wrapper from the last round is dead. This index is rebuilt once per round at
    /// WaitingForPlayers, then every lookup is a dictionary hit instead of a LINQ pass.
    /// </summary>
    internal sealed class FacilityIndex
    {
        private readonly Dictionary<RoomName, List<Room>> _roomsByName = new Dictionary<RoomName, List<Room>>();
        private readonly Dictionary<Room, List<Door>> _doorsByRoom = new Dictionary<Room, List<Door>>();
        private readonly HashSet<Door> _subDoors = new HashSet<Door>();

        public bool IsBuilt { get; private set; }
        public int RoomCount => _doorsByRoom.Count;

        /// <summary>
        /// Call from ServerEvents.WaitingForPlayers. At MapGenerated the room coordinate lookup,
        /// Door.Rooms, Room.Doors and door nametags are not ready yet. Returns false when the map
        /// looks empty: retry one frame later rather than caching an empty index.
        /// </summary>
        public bool Rebuild()
        {
            Clear();

            // Checkpoint sub-doors are damageable-door wrappers, so a type test cannot exclude them.
            // Collect every CheckpointDoor.SubDoors first and skip those instances below.
            foreach (CheckpointDoor checkpoint in CheckpointDoor.List)
            {
                foreach (Door sub in checkpoint.SubDoors)
                    _subDoors.Add(sub);
            }

            foreach (Room room in Room.List)
            {
                if (room.IsDestroyed)
                    continue;

                if (!_roomsByName.TryGetValue(room.Name, out List<Room> sameName))
                {
                    sameName = new List<Room>(1);
                    _roomsByName[room.Name] = sameName;
                }
                sameName.Add(room);

                // Room.Doors allocates per call: once per room per round is the right price.
                var doors = new List<Door>();
                foreach (Door door in room.Doors)
                {
                    if (IsPlainDoor(door))
                        doors.Add(door);
                }
                _doorsByRoom[room] = doors;
            }

            IsBuilt = _doorsByRoom.Count > 0;
            return IsBuilt;
        }

        public void Clear()
        {
            _roomsByName.Clear();
            _doorsByRoom.Clear();
            _subDoors.Clear();
            IsBuilt = false;
        }

        /// <summary>
        /// RoomName is NOT unique: Unnamed is a catch-all, checkpoint rooms exist once per zone
        /// side, tesla and office rooms repeat. This returns every match; narrow with Zone and
        /// Shape and handle 0, 1 and many.
        /// </summary>
        public IReadOnlyList<Room> RoomsNamed(RoomName name)
            => _roomsByName.TryGetValue(name, out List<Room> rooms) ? rooms : (IReadOnlyList<Room>)System.Array.Empty<Room>();

        /// <summary>The doors of a room, without elevator, dummy, non-interactable or checkpoint sub-doors.</summary>
        public IReadOnlyList<Door> DoorsOf(Room room)
            => room != null && _doorsByRoom.TryGetValue(room, out List<Door> doors) ? doors : (IReadOnlyList<Door>)System.Array.Empty<Door>();

        // Door.List includes elevator doors, dummy doors, non-interactable doors, checkpoint doors AND
        // each checkpoint's sub-doors. A door destroyed before room registration can linger in the
        // dictionary, so skip IsDestroyed. Bulk operations want only the plain ones.
        private bool IsPlainDoor(Door door)
            => door != null
               && !door.IsDestroyed
               && !(door is ElevatorDoor)
               && !(door is DummyDoor)
               && !(door is NonInteractableDoor)
               && !_subDoors.Contains(door);
    }
}
