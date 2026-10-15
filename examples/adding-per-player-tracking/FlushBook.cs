using System.Collections.Generic;
using System.Globalization;

namespace Examples.AddingPerPlayerTracking
{
    /// <summary>
    /// Where every credited second is, from the moment it is earned until the journal has it.
    /// Main thread only, plain .NET only (so the tests run without the game).
    /// </summary>
    /// <remarks>
    /// A second is always in exactly one of these places:
    ///   1. the pending fields of its account (earned, not yet in a batch);
    ///   2. a batch handed to the worker (<see cref="InFlightCount"/>): the outcome is unknown;
    ///   3. a batch whose write failed or was refused (<see cref="RetryCount"/>), waiting to be re-sent;
    ///   4. the journal file.
    /// Moving between 2 and 4 is not atomic. The worker can finish writing and the plugin can start
    /// its shutdown flush before the confirmation reaches the main thread, so for a moment a batch is
    /// in 2 AND 4. A failed write can also leave part of a batch in the file while the batch is back
    /// in 3. Instead of pretending that cannot happen, a batch keeps its id and its content for its
    /// whole life and every re-send uses both unchanged. The reader counts each (batch id, account)
    /// line once, so writing at least once gives totals that count exactly once.
    /// What must never happen is merging a failed batch into new pending seconds: that would give the
    /// retry a new id, and any lines of the old one already in the file would be counted twice.
    /// </remarks>
    internal sealed class FlushBook
    {
        /// <summary>
        /// Most failed batches kept. A dead disk for hours would otherwise grow the list without
        /// bound. At the default 30 s flush this is about 4 hours of outage.
        /// </summary>
        public const int MaxRetained = 512;

        private static readonly Batch[] NoBatches = new Batch[0]; // shared, so an idle tick allocates nothing

        private readonly Dictionary<string, Totals> _totals; // owned by the tracker, shared so a batch can read the pending fields
        private readonly string _idPrefix;
        private readonly HashSet<string> _dirty = new HashSet<string>();
        private readonly List<Batch> _retry = new List<Batch>();
        private readonly List<Batch> _inFlight = new List<Batch>();
        private long _sequence;

        public FlushBook(Dictionary<string, Totals> totals, string idPrefix)
        {
            _totals = totals;
            _idPrefix = idPrefix;
        }

        public int RetryCount => _retry.Count;

        public int InFlightCount => _inFlight.Count;

        /// <summary>Batches discarded because <see cref="MaxRetained"/> was exceeded. Their seconds are lost, and counted.</summary>
        public long DroppedBatches { get; private set; }

        public void MarkDirty(string userId) => _dirty.Add(userId);

        /// <summary>
        /// The batches to write now: every failed one again, unchanged, then a new one from the
        /// pending seconds (if any). They count as in flight until <see cref="Settled"/> or <see cref="Failed"/>.
        /// </summary>
        public Batch[] CollectDue()
        {
            Batch fresh = TakeNew();
            int n = _retry.Count + (fresh != null ? 1 : 0);
            if (n == 0)
                return NoBatches;

            var due = new Batch[n];
            _retry.CopyTo(due);
            if (fresh != null)
                due[n - 1] = fresh;

            _retry.Clear();
            _inFlight.AddRange(due);
            return due;
        }

        /// <summary>The worker wrote these and said so.</summary>
        public void Settled(Batch[] batches)
        {
            foreach (Batch b in batches)
                _inFlight.Remove(b);
        }

        /// <summary>The write threw or the worker queue refused it. The batches stay as they are and are re-sent by the next <see cref="CollectDue"/>.</summary>
        public void Failed(Batch[] batches)
        {
            foreach (Batch b in batches)
            {
                _inFlight.Remove(b);
                _retry.Add(b);
            }

            if (_retry.Count > MaxRetained)
            {
                int drop = _retry.Count - MaxRetained;
                _retry.RemoveRange(0, drop); // the oldest: the newest seconds are the likeliest to still matter
                DroppedBatches += drop;
            }
        }

        /// <summary>
        /// Shutdown: everything that might not be in the file yet, in one list. Includes batches
        /// still in flight on the worker, which may or may not have been written; the duplicates
        /// this can produce are exactly what the batch id is for.
        /// </summary>
        public List<Batch> CollectForShutdown()
        {
            var all = new List<Batch>(_retry.Count + _inFlight.Count + 1);
            all.AddRange(_retry);
            all.AddRange(_inFlight);
            Batch fresh = TakeNew();
            if (fresh != null)
                all.Add(fresh);

            _retry.Clear();
            _inFlight.Clear();
            return all;
        }

        // The pending fields are zeroed as the batch is built. From here on those seconds live in the
        // Batch, and the Batch lives in this book until the worker confirms it.
        private Batch TakeNew()
        {
            if (_dirty.Count == 0)
                return null;

            var deltas = new Delta[_dirty.Count];
            int i = 0;
            foreach (string id in _dirty)
            {
                Totals t = _totals[id];
                deltas[i++] = new Delta(id, t.PendingActive, t.PendingAfk, t.PendingSpectating);
                t.PendingActive = t.PendingAfk = t.PendingSpectating = 0;
            }

            _dirty.Clear();
            return new Batch(_idPrefix + "." + (++_sequence).ToString(CultureInfo.InvariantCulture), deltas);
        }
    }
}
