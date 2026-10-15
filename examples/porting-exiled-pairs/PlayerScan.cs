using System.Collections.Generic;
using System.Text;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using PlayerRoles;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 5 (Player.List iteration). The EXILED original:
    /// <code>
    /// foreach (Player p in Player.List)                       // a COPY of the verified players per call
    ///     if (p.Role.Team == Team.FoundationForces) p.Heal(20f);
    /// int online = Player.List.Count;                        // another copy
    /// IEnumerable&lt;Player&gt; mtf = Player.Get(Side.Mtf);        // helper by side
    /// </code>
    /// LabAPI differences that bite when this is renamed:
    /// - <c>Player.List</c> is the LIVE dictionary view and also holds the host, NPCs, dummies and hubs that
    ///   have not authenticated. A foreach over it that sends a message can hit the host (SendHint throws there).
    /// - <c>Player.ReadyList</c> = authenticated players plus dummies. <c>GetAll(PlayerSearchFlags.AuthenticatedPlayers)</c>
    ///   = real players only. Both are lazy LINQ with no snapshot; <c>Player.Count</c> re-runs the filter per call.
    /// - Nothing is copied for you, so take your own snapshot before acting on players (heal, kick, set role)
    ///   and never hold the list across a <c>yield</c> or a frame.
    /// </summary>
    internal static class PlayerScan
    {
        /// <summary>Fills <paramref name="into"/> with authenticated, non-dummy players. Returns the count. Main thread only.</summary>
        public static int Snapshot(List<Player> into)
        {
            into.Clear();
            foreach (Player p in Player.GetAll(PlayerSearchFlags.AuthenticatedPlayers))
                into.Add(p);
            return into.Count;
        }

        /// <summary>Alive players of one faction. EXILED <c>Player.Get(Side)</c> equivalent; friendly fire uses faction, not team.</summary>
        public static int SnapshotFaction(List<Player> into, Faction faction)
        {
            into.Clear();
            foreach (Player p in Player.GetAll(PlayerSearchFlags.AuthenticatedPlayers))
            {
                if (p.IsAlive && p.Faction == faction)
                    into.Add(p);
            }
            return into.Count;
        }

        /// <summary>Counts alive players per team in one pass over an existing snapshot. <paramref name="counts"/> is reused by the caller.</summary>
        public static void CountAliveByTeam(IReadOnlyList<Player> players, Dictionary<Team, int> counts)
        {
            counts.Clear();
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                if (p.IsDestroyed || !p.IsAlive) continue;
                int n;
                counts.TryGetValue(p.Team, out n);
                counts[p.Team] = n + 1;
            }
        }

        public static string Describe(Dictionary<Team, int> counts)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<Team, int> kv in counts)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.Length == 0 ? "nobody alive" : sb.ToString();
        }
    }
}
