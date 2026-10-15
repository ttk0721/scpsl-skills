using System;
using System.Collections.Generic;
using LabApi.Features.Console;

namespace Examples.NetworkingSnippets
{
    /// <summary>
    /// Spawns admin toys in slices sized by BYTES, not by count. Every spawn is synchronous on the
    /// main thread and serialises one SpawnMessage per ready connection into the same reliable
    /// stream as positions and voice, so one big slice delays both. Drive <see cref="Tick"/> from
    /// one SlClock job (a few Hz), never from a coroutine per toy.
    /// </summary>
    /// <remarks>
    /// The identity cap is rule 1 of the skill: every spawned toy is a recurring per-tick cost for
    /// every player (Mirror walks connections x observed identities each tick, static or not), so
    /// the queue refuses work past the cap instead of letting a config typo spawn 5000 toys.
    /// </remarks>
    internal sealed class ToySpawnQueue
    {
        /// <summary>Estimate for one PrimitiveObjectToy spawn message, per ready player (see the skill's cost table).</summary>
        public const int BytesPerPrimitiveSpawn = 134;

        private struct Job
        {
            public int Toys;      // identities this job creates (a group counts anchor + children)
            public Action Run;
        }

        private readonly Queue<Job> _jobs = new Queue<Job>(64);
        private readonly int _identityCap;
        private int _reserved;

        public ToySpawnQueue(int identityCap)
        {
            _identityCap = Math.Max(1, identityCap);
        }

        public int Pending => _jobs.Count;

        /// <summary>Identities queued or already spawned this round (what the cap counts).</summary>
        public int Reserved => _reserved;

        /// <summary>Queues one spawn job that creates <paramref name="toys"/> identities. False when the cap would be exceeded.</summary>
        public bool TryEnqueue(int toys, Action spawn)
        {
            if (toys < 1 || spawn == null)
                return false;
            if (_reserved + toys > _identityCap)
                return false;

            _reserved += toys;
            _jobs.Enqueue(new Job { Toys = toys, Run = spawn });
            return true;
        }

        /// <summary>
        /// Runs queued jobs until the byte budget for this tick is used up. Returns the number of
        /// identities created. Pass <c>Player.Count</c> read ONCE per tick (it is a LINQ count).
        /// </summary>
        public int Tick(int budgetBytes, int players)
        {
            long perToy = (long)BytesPerPrimitiveSpawn * Math.Max(1, players);
            long left = budgetBytes;
            int created = 0;

            while (_jobs.Count > 0)
            {
                Job next = _jobs.Peek();
                long cost = next.Toys * perToy;

                // Always run at least one job per tick: a job bigger than the whole budget would
                // otherwise never run and the queue would stall forever.
                if (created > 0 && cost > left)
                    break;

                _jobs.Dequeue();
                created += RunOne(next);
                left -= cost;
            }

            return created;
        }

        /// <summary>Drops everything. Call at WaitingForPlayers: the scene reload already destroyed the toys.</summary>
        public void Clear()
        {
            _jobs.Clear();
            _reserved = 0;
        }

        private static int RunOne(Job job)
        {
            try
            {
                job.Run();
                return job.Toys;
            }
            catch (Exception ex)
            {
                // One bad job must not kill the rest of the queue. Rare path, so a full Logger call is fine.
                Logger.Error("ToySpawnQueue: a spawn job threw and was skipped: " + ex);
                return 0;
            }
        }
    }
}
