using System.Collections.Generic;
using LabApi.Features.Wrappers;
using UnityEngine;
using PrimitiveFlags = AdminToys.PrimitiveFlags; // AdminToys also defines PrimitiveObjectToy: alias instead of the namespace

namespace Examples.NetworkingSnippets
{
    /// <summary>A group of static toys that moves as one by moving a single anchor.</summary>
    internal sealed class ToyGroup
    {
        public PrimitiveObjectToy Anchor;
        public readonly List<PrimitiveObjectToy> Children = new List<PrimitiveObjectToy>();
    }

    /// <summary>
    /// Creates toys the cheap way. Three habits, all verified in the skill's toys-and-spawning.md:
    ///   1. Create(networkSpawn: false), configure everything, then Spawn(). Property writes before
    ///      the first observer exists cost nothing; writes after Spawn() ship one more
    ///      EntityStateMessage per toy per player.
    ///   2. IsStatic = true on everything that does not move (no server LateUpdate body, and the
    ///      client skips position updates). It does NOT shrink the spawn message or the per-tick
    ///      identity walk, so fewer toys is still the bigger lever.
    ///   3. Few distinct colours. PrimitiveObjectToy keeps one Material per exact Color for the
    ///      whole process on the server and on every client, so never animate or randomise Color.
    /// </summary>
    internal static class ToyFactory
    {
        public static readonly Color Cyan = new Color(0.1f, 0.8f, 1f, 1f);
        public static readonly Color Amber = new Color(1f, 0.7f, 0.1f, 1f);

        /// <summary>A visible, non-collidable, static primitive. Call from a ToySpawnQueue job.</summary>
        public static PrimitiveObjectToy SpawnStatic(Vector3 position, Vector3 scale, PrimitiveType type, Color color)
        {
            PrimitiveObjectToy toy = PrimitiveObjectToy.Create(position, Quaternion.identity, scale, null, false);
            toy.Type = type;
            toy.Color = color;
            toy.Flags = PrimitiveFlags.Visible; // no Collidable: no collider on the server or on any client
            toy.IsStatic = true;
            toy.Spawn();
            return toy;
        }

        /// <summary>
        /// One invisible anchor plus static children parented to it. Moving the anchor (non-static,
        /// see ToyMover) moves every child; the children stay static and cost no LateUpdate.
        /// Counts as 1 + offsets.Count identities for the spawn budget.
        /// </summary>
        public static ToyGroup SpawnGroup(Vector3 anchorPosition, IReadOnlyList<Vector3> localOffsets, Vector3 childScale, Color color)
        {
            var group = new ToyGroup();

            // The anchor carries no renderer and no collider (Flags None) and is spawned FIRST:
            // a parent that is not spawned yet has netId 0, and clients would attach the child to nothing.
            PrimitiveObjectToy anchor = PrimitiveObjectToy.Create(anchorPosition, Quaternion.identity, Vector3.one, null, false);
            anchor.Flags = PrimitiveFlags.None;
            anchor.IsStatic = true;
            anchor.Spawn();
            group.Anchor = anchor;

            for (int i = 0; i < localOffsets.Count; i++)
            {
                // Parent passed at Create so the initial state already carries the parent id; the
                // position is LOCAL to the anchor.
                PrimitiveObjectToy child = PrimitiveObjectToy.Create(localOffsets[i], Quaternion.identity, childScale, anchor.Transform, false);
                child.Type = PrimitiveType.Cube;
                child.Color = color;
                child.Flags = PrimitiveFlags.Visible;
                child.IsStatic = true; // allowed under a moving parent: static only freezes the LOCAL transform
                child.Spawn();         // Spawn() does not cascade to children
                group.Children.Add(child);
            }

            return group;
        }
    }
}
