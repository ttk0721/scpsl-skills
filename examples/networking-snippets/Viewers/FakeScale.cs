using System.Collections.Generic;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace Examples.NetworkingSnippets
{
    /// <summary>
    /// Per-viewer player scale: "viewer V sees target T at scale S, everyone else sees the real
    /// scale". This is one of the few per-observer things the game supports without Harmony,
    /// because the scale already travels as its own message type (ScaleMessage) that a vanilla
    /// client handles, and we send it to ONE connection with <c>Connection.Send</c>.
    /// </summary>
    /// <remarks>
    /// What it is NOT: the server's hitbox, camera height and physics keep the real scale. Only the
    /// viewer's client model changes, so use it for disguises and visual effects, never for gameplay rules.
    ///
    /// What can undo it, and what this class does about it:
    ///  - Any later write of the REAL scale (<c>Player.Scale</c>, also a new role's scale
    ///    controller) is broadcast to every ready client and overwrites the fake on all viewers.
    ///    There is no event for scale writes, so we re-send on role change (ChangedRole) and run
    ///    <see cref="Reassert"/> from a slow SlClock job to catch other plugins.
    ///  - A joiner receives the REAL scales. A fake is only stored for a viewer that is already
    ///    connected, so there is nothing to re-send on join; entries are dropped on leave (PlayerIds are recycled).
    ///
    /// Not supported by the API, so not offered here: a per-viewer toy or pickup (needs the internal
    /// spawn-to-one-connection path via reflection), per-viewer SyncVars (hand-built EntityStateMessage,
    /// build-specific component indices) and per-viewer role spoofing (RoleSyncEvent plus
    /// role-specific spawn data; another plugin's data in the writer makes it unsafe). See
    /// toys-and-spawning.md in the skill.
    /// </remarks>
    internal sealed class FakeScale
    {
        // Sanity bounds for what a plugin may ask for. These are OUR guard, not a verified game limit.
        public const float MinFactor = 0.1f;
        public const float MaxFactor = 4f;

        private readonly Dictionary<long, Vector3> _desired = new Dictionary<long, Vector3>(32);
        private readonly List<long> _scratch = new List<long>(32);

        public int Count => _desired.Count;

        /// <summary>
        /// Shows <paramref name="target"/> to <paramref name="viewer"/> at <paramref name="scale"/>.
        /// Returns a refusal reason, or null on success. Main thread only (it sends a Mirror message).
        /// </summary>
        public string Set(Player viewer, Player target, Vector3 scale)
        {
            string problem = CheckPair(viewer, target);
            if (problem != null)
                return problem;
            if (!InRange(scale))
                return "scale must be between " + MinFactor + " and " + MaxFactor + " on every axis";

            long key = Key(viewer, target);
            if (_desired.TryGetValue(key, out Vector3 existing) && existing == scale)
                return null; // same value: do not send (ScaleMessage has no equality check on the wire)

            _desired[key] = scale;
            Send(viewer, target, scale);
            return null;
        }

        /// <summary>Removes the fake and tells this one viewer the real scale again.</summary>
        public void Clear(Player viewer, Player target)
        {
            if (!_desired.Remove(Key(viewer, target)))
                return;
            if (CheckPair(viewer, target) == null)
                Send(viewer, target, target.Scale);
        }

        /// <summary>Call from PlayerEvents.ChangedRole: a new role builds a new scale controller that re-broadcasts the real scale.</summary>
        public void OnTargetRoleChanged(Player target)
        {
            if (target == null || _desired.Count == 0)
                return;

            foreach (KeyValuePair<long, Vector3> pair in Snapshot())
            {
                if (TargetId(pair.Key) != target.PlayerId)
                    continue;
                Player viewer = Player.Get(ViewerId(pair.Key));
                if (viewer != null && CheckPair(viewer, target) == null)
                    Send(viewer, target, pair.Value);
            }
        }

        /// <summary>
        /// Re-sends every stored fake. Run from one SlClock job every few seconds: it is the only way
        /// to recover from another plugin writing the real scale, and costs ~19 B per entry.
        /// </summary>
        public void Reassert()
        {
            if (_desired.Count == 0)
                return;

            foreach (KeyValuePair<long, Vector3> pair in Snapshot())
            {
                Player viewer = Player.Get(ViewerId(pair.Key));
                Player target = Player.Get(TargetId(pair.Key));
                if (viewer == null || target == null || CheckPair(viewer, target) != null)
                {
                    _desired.Remove(pair.Key); // someone left or changed to a role without a scale controller
                    continue;
                }
                Send(viewer, target, pair.Value);
            }
        }

        /// <summary>Call from PlayerEvents.Left. PlayerIds are recycled, so a stale entry would hit the next player who gets the id.</summary>
        public void Forget(int playerId)
        {
            if (_desired.Count == 0)
                return;

            _scratch.Clear();
            foreach (long key in _desired.Keys)
            {
                if (ViewerId(key) == playerId || TargetId(key) == playerId)
                    _scratch.Add(key);
            }
            for (int i = 0; i < _scratch.Count; i++)
                _desired.Remove(_scratch[i]);
        }

        public void ClearAll() => _desired.Clear();

        // ---- internals ----------------------------------------------------------------------

        private static void Send(Player viewer, Player target, Vector3 scale)
        {
            // An existing message type to one connection: a vanilla client already has the handler.
            // A custom NetworkMessage would get the client disconnected.
            viewer.Connection.Send(new SyncedScaleMessages.ScaleMessage(scale, target.ReferenceHub));
        }

        private static string CheckPair(Player viewer, Player target)
        {
            if (viewer == null || target == null || viewer.IsDestroyed || target.IsDestroyed)
                return "unknown player";
            if (viewer == target)
                return "viewer and target must differ";
            if (!viewer.IsPlayer || !viewer.IsReady)
                return "the viewer must be a connected player (not the host, a dummy or an NPC)";
            if (!(target.ReferenceHub.roleManager.CurrentRole is IFpcRole))
                return "the target's role has no first-person scale";
            return null;
        }

        private static bool InRange(Vector3 s) => Ok(s.x) && Ok(s.y) && Ok(s.z);

        private static bool Ok(float v) => v >= MinFactor && v <= MaxFactor;

        // Copy first: Send can throw or remove entries while we iterate.
        private List<KeyValuePair<long, Vector3>> Snapshot() => new List<KeyValuePair<long, Vector3>>(_desired);

        private static long Key(Player viewer, Player target) => ((long)viewer.PlayerId << 32) | (uint)target.PlayerId;

        private static int ViewerId(long key) => (int)(key >> 32);

        private static int TargetId(long key) => (int)(key & 0xFFFFFFFF);
    }
}
