using System.Collections.Generic;

namespace Examples.AddingCustomRoles
{
    /// <summary>One live custom role. Plain data: no Player, no ReferenceHub, no definition object.</summary>
    internal sealed class Assignment
    {
        public string RoleId;
        public int LifeId;              // UniqueLifeIdentifier of the role instance this was applied to
        public bool Announced;

        // Appearance bookkeeping, so we undo only what we set and leave other plugins' changes alone.
        public bool TouchedAppearance;
        public string PrevCustomInfo;
        public string AppliedCustomInfo;
        public PlayerInfoArea PrevInfoArea;
        public PlayerInfoArea AppliedInfoArea;
    }

    internal struct PendingGrant
    {
        public string RoleId;
        public int Frame;               // UnityEngine.Time.frameCount of the ChangingRole that decided it
    }

    /// <summary>
    /// The only owner of custom-role state. Keyed by UserId (never by Player/hub/PlayerId, which die or
    /// recycle at a round restart). Pure data and logic: no game calls, so the rules are easy to reason about.
    /// </summary>
    internal sealed class RoleRegistry
    {
        private readonly Dictionary<string, Assignment> _active = new Dictionary<string, Assignment>(16);
        private readonly Dictionary<string, PendingGrant> _pending = new Dictionary<string, PendingGrant>(16);

        public int ActiveCount => _active.Count;
        public int PendingCount => _pending.Count;

        // Read-only view for the 1 Hz tick. The caller must not add or remove while enumerating.
        public IEnumerable<KeyValuePair<string, Assignment>> Active => _active;

        /// <summary>
        /// ServerSetRole runs ChangingRole, the loadout and ChangedRole in ONE call stack, so a pending grant is
        /// only ever valid in the frame that created it. A grant whose role change was cancelled by someone else
        /// is therefore stale one frame later and can never leak onto the player's next, unrelated role.
        /// </summary>
        public void SetPending(string userId, string roleId, int frame)
        {
            _pending[userId] = new PendingGrant { RoleId = roleId, Frame = frame };
        }

        public bool TryGetFreshPending(string userId, int frame, out PendingGrant grant)
        {
            return _pending.TryGetValue(userId, out grant) && grant.Frame == frame;
        }

        public bool TryTakePending(string userId, int frame, out PendingGrant grant)
        {
            if (!_pending.Remove(userId, out grant)) return false;
            return grant.Frame == frame;
        }

        public void ClearPending(string userId) => _pending.Remove(userId);

        /// <summary>
        /// Drops grants from earlier frames (their role change was cancelled by someone). Without it one cancelled
        /// change would keep PendingCount above zero and defeat the cheap early-outs in the handlers.
        /// </summary>
        public void SweepPending(int currentFrame, List<string> scratch)
        {
            if (_pending.Count == 0) return;
            scratch.Clear();
            foreach (KeyValuePair<string, PendingGrant> kv in _pending)
                if (kv.Value.Frame != currentFrame) scratch.Add(kv.Key);
            for (int i = 0; i < scratch.Count; i++) _pending.Remove(scratch[i]);
        }

        public void Activate(string userId, Assignment a) => _active[userId] = a;

        public bool TryGetActive(string userId, out Assignment a) => _active.TryGetValue(userId, out a);

        public bool TryRemoveActive(string userId, out Assignment a) => _active.Remove(userId, out a);

        /// <summary>Holders of one definition. O(holders), and holders are a handful.</summary>
        public int CountAlive(string roleId)
        {
            int n = 0;
            foreach (KeyValuePair<string, Assignment> kv in _active)
                if (kv.Value.RoleId == roleId) n++;
            return n;
        }

        /// <summary>Round boundary: nothing from the old world is valid.</summary>
        public void Clear()
        {
            _active.Clear();
            _pending.Clear();
        }
    }
}
