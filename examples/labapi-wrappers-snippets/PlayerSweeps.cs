using System.Collections.Generic;
using LabApi.Features.Wrappers;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operation 2: iterate players. There is no snapshot anywhere in LabAPI: Player.List is the live
    /// dictionary values view and ReadyList, GetAll, NpcList and Count are lazy LINQ filters over it.
    /// </summary>
    internal sealed class PlayerSweeps
    {
        // Reused between sweeps so a sweep allocates nothing after warm-up.
        private readonly List<Player> _humans = new List<Player>(64);

        /// <summary>
        /// Copies the authenticated humans into a reused buffer and returns it. Iterate the buffer
        /// by index: joins, leaves, lazy wrapper creation and a yield in the loop body cannot
        /// invalidate it. The buffer is overwritten by the next call, so do not keep it across
        /// frames and do not call this re-entrantly.
        /// </summary>
        /// <remarks>
        /// A foreach over Player.List allocates one boxed enumerator, which is cheaper than
        /// ReadyList's iterator chain. The IsPlayer and IsReady tests reproduce the authenticated
        /// filter: host, dummies and connecting clients are skipped.
        /// </remarks>
        public IReadOnlyList<Player> SnapshotHumans()
        {
            _humans.Clear();
            foreach (Player player in Player.List)
            {
                if (player.IsPlayer && player.IsReady)
                    _humans.Add(player);
            }
            return _humans;
        }

        /// <summary>
        /// Counts living humans in one pass. A plain synchronous foreach is acceptable here because
        /// the body only reads: no yield, no Player.Get on a hub that has no wrapper yet, no kick.
        /// </summary>
        public static int CountAliveHumans()
        {
            int alive = 0;
            foreach (Player player in Player.List)
            {
                if (player.IsPlayer && player.IsReady && player.IsAlive)
                    alive++;
            }
            return alive;
        }

        /// <summary>
        /// Player.Count (and Server.PlayerCount) is a full LINQ filter per call, includes dummies,
        /// excludes the host and excludes connecting clients. For "how many people are playing" use
        /// this, and read it once per tick, not once per loop iteration.
        /// </summary>
        public static int HumanCount()
        {
            int count = 0;
            foreach (Player player in Player.List)
            {
                if (player.IsPlayer && player.IsReady)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// The readable form when you do not care about allocations (a command, a once-per-round
        /// report). GetAll is a lazy iterator: ToArray() is what makes it a snapshot.
        /// </summary>
        public static Player[] HumansOnce()
            => System.Linq.Enumerable.ToArray(Player.GetAll(LabApi.Features.Enums.PlayerSearchFlags.AuthenticatedPlayers));
    }
}
