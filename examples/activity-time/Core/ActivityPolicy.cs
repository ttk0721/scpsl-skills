using System;
using System.Collections.Generic;

namespace Examples.ActivityTime.Core
{
    /// <summary>
    /// Rules for what counts as active play. Pure C#: no LabAPI, Unity or Mirror types, so the
    /// unit tests compile this file on plain .NET.
    /// </summary>
    public sealed class ActivityPolicy
    {
        private readonly HashSet<string> _excludedGroups;

        public ActivityPolicy(double minMoveMeters, double idleWindowSeconds, double maxCreditSeconds,
            IEnumerable<string> excludedGroups)
        {
            MinMoveMeters = Math.Max(0.05, minMoveMeters);
            IdleWindowSeconds = Math.Max(1.0, idleWindowSeconds);
            MaxCreditSeconds = Math.Max(0.1, maxCreditSeconds);
            _excludedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedGroups != null)
                foreach (string g in excludedGroups)
                    if (!string.IsNullOrWhiteSpace(g))
                        _excludedGroups.Add(g.Trim());
        }

        /// <summary>Displacement since the last move that counts as moving again.</summary>
        public double MinMoveMeters { get; }

        /// <summary>A player who has not moved for longer than this earns nothing.</summary>
        public double IdleWindowSeconds { get; }

        /// <summary>Upper bound on one sample's credit, so a stall or a late sample cannot over-credit.</summary>
        public double MaxCreditSeconds { get; }

        /// <summary>Members of these permission groups earn nothing.</summary>
        public bool IsExcluded(string permissionsGroup) =>
            permissionsGroup != null && _excludedGroups.Contains(permissionsGroup);

        /// <summary>
        /// Which connections are tracked at all. The wiring passes plain values and decides nothing:
        /// the server host and dummies never earn, nor does a client still authenticating, nor staff.
        /// </summary>
        public bool ShouldTrack(bool isHostOrDummy, bool isReady, string permissionsGroup) =>
            !isHostOrDummy && isReady && !IsExcluded(permissionsGroup);

        /// <summary>
        /// Whether a UserId names a real account and can key data that outlives the session.
        /// Offline mode gives per-connection ids (ID_Offline_...) and every dummy shares ID_Dummy,
        /// so a null/empty check alone would merge strangers into one ledger row.
        /// </summary>
        public static bool IsAccountId(string userId) =>
            !string.IsNullOrEmpty(userId) && !userId.StartsWith("ID_", System.StringComparison.Ordinal);
    }
}
