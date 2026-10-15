using System;
using System.Collections.Generic;
using System.Diagnostics;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace Examples.NetworkingSnippets
{
    /// <summary>
    /// Moves toys without per-frame network churn. A static toy ignores transform writes on the
    /// server, so a move is: thaw (IsStatic = false), write the position, and keep the toy
    /// non-static until the update has been captured, sent, delivered, applied and smoothed. Only
    /// then freeze again. Freezing early is the classic bug: with SyncInterval above 0 the dirty
    /// bits stay set, so the position and IsStatic = true ship in ONE message and the client never moves.
    /// </summary>
    /// <remarks>
    /// What keeps the cost down:
    ///  - Targets are quantised and compared exactly, so a caller that says "go to X" every frame
    ///    is a no-op after the first call (and a noisy float source does not defeat the equality check).
    ///  - SyncInterval above 0 coalesces writes: several writes inside one interval become one
    ///    message with the last value. A per-frame write on a throttled toy costs CPU only.
    ///  - Frozen toys cost nothing per frame. Only the moving set is touched by <see cref="Tick"/>.
    ///  - For a whole group, move one non-static anchor (ToyFactory.SpawnGroup) instead of thawing every child.
    /// The settle time below is derived from the code, not measured in game; read it as a safe
    /// lower bound plus a configurable network allowance.
    /// </remarks>
    internal sealed class ToyMover
    {
        private sealed class Entry
        {
            public Vector3 Target;
            public double FreezeAt; // seconds on the Stopwatch clock
        }

        private const float QuantizeStep = 0.02f;       // metres; below this, noise is not worth a message
        private const float ServerSendTicks = 2f / 60f; // two Mirror send ticks (tick rate assumed 60: unverified, log NetworkServer.tickRate)

        private readonly Dictionary<PrimitiveObjectToy, Entry> _moving = new Dictionary<PrimitiveObjectToy, Entry>(16);
        private readonly List<PrimitiveObjectToy> _scratch = new List<PrimitiveObjectToy>(16);

        /// <summary>Seconds between updates for a moving toy. 0.1 or more for many movers; 0 only for one hero object (about 3-5 KB/s per observer).</summary>
        public float SyncInterval { get; set; } = 0.1f;

        /// <summary>
        /// LabAPI wrapper value: 0 = snap, higher = more smoothing (the wrapper inverts the raw byte,
        /// so 255 is the slowest chase). Smoothing is an exponential chase on the client, not
        /// interpolation, and it only runs while the toy is non-static, so it lengthens the settle time.
        /// </summary>
        public byte Smoothing { get; set; } = 0;

        /// <summary>Round-trip allowance in seconds (RTT/2 plus one client frame). A tunable estimate, not a measured value.</summary>
        public float NetworkAllowance { get; set; } = 0.25f;

        public int MovingCount => _moving.Count;

        /// <summary>
        /// Requests a move to a LOCAL position. Returns true when a change was actually written,
        /// false for a no-op (same quantised target, or a destroyed toy). Main thread only.
        /// </summary>
        public bool MoveTo(PrimitiveObjectToy toy, Vector3 localPosition)
        {
            if (toy == null || toy.IsDestroyed)
                return false;

            Vector3 target = Quantize(localPosition);

            if (_moving.TryGetValue(toy, out Entry entry))
            {
                if (entry.Target == target)
                    return false; // the repeated-call case (quantised values differ by a whole step or are identical, so Unity's tolerant == is exact here)

                entry.Target = target;
                toy.Position = target;                 // coalesces with other writes inside one SyncInterval
                entry.FreezeAt = Now() + SettleSeconds(); // a new move restarts the wait
                return true;
            }

            if (toy.Position == target)
                return false; // already there: do not thaw a static toy for nothing

            // Both are no-ops for the wire when the value is unchanged (SyncVar equality), so this
            // is cheap on every thaw; SyncInterval is a plain field, not synced.
            if (toy.SyncInterval != SyncInterval)
                toy.SyncInterval = SyncInterval;
            if (toy.MovementSmoothing != Smoothing)
                toy.MovementSmoothing = Smoothing;

            toy.IsStatic = false;
            toy.Position = target;
            _moving.Add(toy, new Entry { Target = target, FreezeAt = Now() + SettleSeconds() });
            return true;
        }

        /// <summary>
        /// Freezes toys whose move has had time to be sent and applied. Call from one SlClock job
        /// (5-10 Hz is plenty). Being a little late only keeps a toy non-static a little longer,
        /// which is safe; being early is the bug. Returns the number frozen.
        /// </summary>
        public int Tick()
        {
            if (_moving.Count == 0)
                return 0;

            double now = Now();
            _scratch.Clear();
            foreach (KeyValuePair<PrimitiveObjectToy, Entry> pair in _moving)
            {
                if (pair.Key.IsDestroyed || now >= pair.Value.FreezeAt)
                    _scratch.Add(pair.Key);
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                PrimitiveObjectToy toy = _scratch[i];
                if (!toy.IsDestroyed)
                    toy.IsStatic = true;
                _moving.Remove(toy);
            }

            return _scratch.Count;
        }

        /// <summary>Forget everything (WaitingForPlayers). The scene reload destroyed the toys; do not freeze dead wrappers.</summary>
        public void Clear()
        {
            _moving.Clear();
            _scratch.Clear();
        }

        /// <summary>
        /// Time the toy must stay non-static after the last write: up to one SyncInterval before the
        /// message may leave, the send ticks, the network allowance, plus the time the client's
        /// exponential chase needs to get within about 1% of the target.
        /// </summary>
        public float SettleSeconds() => SyncInterval + ServerSendTicks + NetworkAllowance + SmoothingTailSeconds(Smoothing);

        /// <summary>
        /// Client chase per frame is Lerp(current, target, deltaTime * raw * 0.3) with raw = 256 - wrapper
        /// value, so the rate is about raw * 0.3 per second and 1% remains after ln(100) / rate seconds.
        /// Derived from the code (frame-rate independent only approximately), not measured on a client.
        /// </summary>
        public static float SmoothingTailSeconds(byte wrapperValue)
        {
            if (wrapperValue == 0)
                return 0f;
            float raw = 256 - wrapperValue;
            return 4.6f / (raw * 0.3f);
        }

        private static Vector3 Quantize(Vector3 v) => new Vector3(Snap(v.x), Snap(v.y), Snap(v.z));

        private static float Snap(float value) => Mathf.Round(value / QuantizeStep) * QuantizeStep;

        private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }
}
