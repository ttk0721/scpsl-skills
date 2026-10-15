using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MapGeneration;
using UnityEngine;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operation 7: rooms. Coordinates depend on the map seed, so a world position never survives a
    /// round. Store a position relative to a room and re-resolve the room each round.
    /// </summary>
    internal static class RoomOps
    {
        /// <summary>
        /// Per-tick "where is this player". Player.Room does an uncached lookup per call (one
        /// dictionary hit inside a room's main cell, otherwise raycasts); CachedRoom reuses the
        /// game's once-per-frame cache. It is null for dead players (spectators have Position
        /// zero), so a null result means "not in a room", not an error.
        /// </summary>
        public static Room Of(Player player)
            => player == null || player.IsDestroyed ? null : player.CachedRoom;

        /// <summary>
        /// Per-room headcount in ONE pass over the players. Room.Players runs an uncached lookup
        /// for every player on every call, so asking it once per room is N x rooms lookups.
        /// </summary>
        public static void CountByRoom(IReadOnlyList<Player> humans, Dictionary<Room, int> into)
        {
            into.Clear();
            for (int i = 0; i < humans.Count; i++)
            {
                Room room = Of(humans[i]);
                if (room == null)
                    continue;
                into.TryGetValue(room, out int count);
                into[room] = count + 1;
            }
        }

        /// <summary>
        /// Picks exactly one room by name and zone from the per-round index, or null when there are
        /// zero or several. "Several" is the normal case for repeated rooms; the caller decides
        /// what to do instead of silently taking the first.
        /// </summary>
        public static Room PickUnique(FacilityIndex index, RoomName name, FacilityZone zone)
        {
            Room found = null;
            foreach (Room room in index.RoomsNamed(name))
            {
                if (room.IsDestroyed || room.Zone != zone)
                    continue;
                if (found != null)
                    return null;
                found = room;
            }
            return found;
        }

        /// <summary>Stores a spot as an offset inside a room. Survives a new seed, unlike world coordinates.</summary>
        public static Vector3 ToLocal(Room room, Vector3 world) => room.Transform.InverseTransformPoint(world);

        /// <summary>Turns a stored offset back into a world position in this round's copy of the room.</summary>
        public static Vector3 ToWorld(Room room, Vector3 local) => room.Transform.TransformPoint(local);
    }
}
