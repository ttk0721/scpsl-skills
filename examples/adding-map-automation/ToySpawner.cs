using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using UnityEngine;
using PrimitiveFlags = AdminToys.PrimitiveFlags; // AdminToys also defines PrimitiveObjectToy and LightSourceToy: alias instead of the namespace

namespace Examples.AddingMapAutomation
{
    /// <summary>
    /// Spawns room markers in byte-budgeted slices. Every spawn is synchronous on the main thread,
    /// serialises one SpawnMessage per connection and lands in the same reliable stream as player
    /// positions and voice, so a burst delays both. One marker = two identities.
    /// </summary>
    internal sealed class ToySpawner
    {
        private const int BytesPerToy = 134;     // PrimitiveObjectToy spawn, per player (estimate, see skill)
        private const int ToysPerMarker = 2;

        // Room-local offsets, so the marker follows the room's rotation and the seed's layout. The
        // pivot height of a room is not verified here: tune on a test server.
        private static readonly Vector3 LightOffset = new Vector3(0f, 2.2f, 0f);
        private static readonly Vector3 DiscOffset = new Vector3(0f, -2.15f, 0f); // local to the light
        private static readonly Vector3 DiscScale = new Vector3(1.2f, 0.02f, 1.2f);

        // One fixed color: PrimitiveObjectToy keeps a Material per distinct Color for the whole
        // process, so never animate or randomise it.
        private static readonly Color MarkerColor = new Color(0.1f, 0.8f, 1f, 1f);

        private readonly Queue<Room> _pending;

        public ToySpawner(IReadOnlyCollection<Room> rooms)
        {
            _pending = new Queue<Room>(rooms);
        }

        public int Pending => _pending.Count;

        /// <summary>Spawns as many markers as the byte budget allows this tick; returns how many toys were created.</summary>
        public int Tick(int budgetBytes, int players)
        {
            int perMarkerBytes = ToysPerMarker * BytesPerToy * Math.Max(1, players);
            int slice = Math.Max(1, budgetBytes / perMarkerBytes); // at least one, or a big server never finishes
            int created = 0;
            for (int i = 0; i < slice && _pending.Count > 0; i++)
            {
                Room room = _pending.Dequeue();
                if (room == null || room.Base == null) // the scene can change under a queued item
                    continue;
                SpawnMarker(room);
                created += ToysPerMarker;
            }
            return created;
        }

        private static void SpawnMarker(Room room)
        {
            // Create(networkSpawn: false) -> configure everything -> Spawn(): property writes before
            // the first observer exists cost nothing, writes after Spawn() ship as an extra message.
            LightSourceToy light = LightSourceToy.Create(
                room.Transform.TransformPoint(LightOffset), Quaternion.identity, Vector3.one, null, false);
            light.Color = MarkerColor;
            light.Intensity = 1.5f;
            light.Range = 6f;
            light.ShadowType = LightShadows.None; // no shadow maps for a decorative light
            light.IsStatic = true;                // nothing here ever moves: no LateUpdate body, no transform sync
            light.Spawn();                        // the parent must be spawned before its child

            // Parent only to a SPAWNED networked object (another toy), and pass it at Create so the
            // initial state carries the parent id. Parenting a static toy is allowed.
            PrimitiveObjectToy disc = PrimitiveObjectToy.Create(
                DiscOffset, Quaternion.identity, DiscScale, light.Transform, false);
            disc.Type = PrimitiveType.Cylinder;
            disc.Color = MarkerColor;
            disc.Flags = PrimitiveFlags.Visible;  // not Collidable: no collider on the server or on any client
            disc.IsStatic = true;
            disc.Spawn();
        }
    }
}
