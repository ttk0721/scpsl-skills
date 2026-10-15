using System;
using System.Collections.Generic;

namespace Examples.ActivityTime.Core
{
    /// <summary>A position without UnityEngine.Vector3, so Core stays testable on plain .NET.</summary>
    public readonly struct Pos3
    {
        public Pos3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public double DistanceTo(Pos3 o)
        {
            double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>
    /// Active seconds per UserId. Single-threaded: call it from the main thread only.
    /// The one owner of this state is the plugin class; nothing else writes to it.
    /// </summary>
    public sealed class ActivityLedger
    {
        private sealed class Entry
        {
            public double Seconds;
            public bool HasMotion;
            public Pos3 LastPos;
            public double LastMoveAt;
            public double LastSampleAt;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(64);

        public int Count => _entries.Count;

        /// <summary>
        /// Records one sample. Returns the seconds credited (0 when idle, dead, first seen or after
        /// a motion reset). <paramref name="now"/> is a monotonic clock in seconds.
        /// </summary>
        public double Sample(string userId, Pos3 pos, bool alive, double now, ActivityPolicy policy)
        {
            if (string.IsNullOrEmpty(userId) || policy == null) return 0;
            if (!_entries.TryGetValue(userId, out Entry e))
            {
                e = new Entry();
                _entries[userId] = e;
            }

            if (!e.HasMotion)
            {
                // First sample, or the first one on a new map: nothing to compare against yet.
                e.HasMotion = true;
                e.LastPos = pos;
                e.LastMoveAt = now;
                e.LastSampleAt = now;
                return 0;
            }

            double elapsed = now - e.LastSampleAt;
            e.LastSampleAt = now;
            if (elapsed <= 0) return 0;

            if (pos.DistanceTo(e.LastPos) >= policy.MinMoveMeters)
            {
                e.LastPos = pos;
                e.LastMoveAt = now;
            }

            if (!alive || now - e.LastMoveAt > policy.IdleWindowSeconds) return 0;

            double credit = Math.Min(elapsed, policy.MaxCreditSeconds);
            e.Seconds += credit;
            return credit;
        }

        /// <summary>Player left: keep the total, forget the position.</summary>
        public void ForgetMotion(string userId)
        {
            if (userId != null && _entries.TryGetValue(userId, out Entry e)) e.HasMotion = false;
        }

        /// <summary>New map after a round restart: positions from the old scene mean nothing.</summary>
        public void ResetMotion()
        {
            foreach (Entry e in _entries.Values) e.HasMotion = false;
        }

        public double SecondsOf(string userId) =>
            userId != null && _entries.TryGetValue(userId, out Entry e) ? e.Seconds : 0;

        /// <summary>Copies totals into a caller-owned list (main thread) for a flush or snapshot.</summary>
        public void CopyTotals(List<KeyValuePair<string, double>> into)
        {
            into.Clear();
            foreach (KeyValuePair<string, Entry> kv in _entries)
                into.Add(new KeyValuePair<string, double>(kv.Key, kv.Value.Seconds));
        }
    }
}
