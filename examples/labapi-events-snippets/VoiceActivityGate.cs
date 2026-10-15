using System;
using System.Collections.Generic;
using System.Diagnostics;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using SlKit;

namespace Examples.LabapiEventsSnippets
{
    /// <summary>
    /// A gated hot-event handler. <c>PlayerEvents.SendingVoiceMessage</c> is raised about 100 times per
    /// second per speaker, and every raise allocates the args and a <c>Delegate[]</c> while anyone is
    /// subscribed. So the subscription exists only while at least one player is watched, and the
    /// handler itself is one early-return test plus one field write.
    /// </summary>
    /// <remarks>
    /// Main thread only (called from other handlers and clock jobs). The gate saves the handler and
    /// the <c>Delegate[]</c> while idle. It does not save the args: the game builds those at its call
    /// site whether or not anyone listens.
    /// </remarks>
    internal sealed class VoiceActivityGate
    {
        // A reference type, so the handler does one lookup and then writes a field: no second
        // dictionary operation, no boxing, nothing allocated per raise by this code.
        private sealed class Stamp
        {
            public long Ticks;
        }

        private readonly Dictionary<int, Stamp> _watched = new Dictionary<int, Stamp>(16);
        private bool _hooked;

        public bool IsHooked => _hooked;

        public void Watch(int playerId)
        {
            if (!_watched.ContainsKey(playerId))
                _watched[playerId] = new Stamp(); // allocated here, once per watch, not per raise

            Hook();
        }

        public void Unwatch(int playerId)
        {
            if (_watched.Remove(playerId) && _watched.Count == 0)
                Unhook(); // idle: stop paying the per-packet cost
        }

        public void Clear()
        {
            _watched.Clear();
            Detach();
        }

        /// <summary>
        /// Removes the subscription and nothing else. Safe from any thread, because event add/remove is
        /// atomic and this touches no collection: use it from a Shutdown handler.
        /// </summary>
        public void Detach() => Unhook();

        /// <summary>True when the watched player spoke within <paramref name="window"/>.</summary>
        public bool SpokeWithin(int playerId, TimeSpan window)
        {
            Stamp stamp;
            if (!_watched.TryGetValue(playerId, out stamp) || stamp.Ticks == 0)
                return false;

            // Stopwatch ticks are not TimeSpan ticks: the unit is 1 / Stopwatch.Frequency seconds.
            // Convert through double rather than multiplying a long, which can overflow.
            double elapsedSeconds = (double)(Stopwatch.GetTimestamp() - stamp.Ticks) / Stopwatch.Frequency;
            return elapsedSeconds <= window.TotalSeconds;
        }

        private void Hook()
        {
            if (_hooked)
                return;

            // scpsl-lint: ignore SL001 -- gated: subscribed only while a player is watched, handler is one lookup and one write
            PlayerEvents.SendingVoiceMessage += OnSending;
            _hooked = true;
        }

        private void Unhook()
        {
            if (!_hooked)
                return;

            PlayerEvents.SendingVoiceMessage -= OnSending;
            _hooked = false;
        }

        // About 100 raises per second per speaker. No logging, LINQ, closures or string work in the
        // body. Still not allocation-free overall: the game and LabAPI allocate per raise, not us.
        private void OnSending(PlayerSendingVoiceMessageEventArgs ev)
        {
            // Cheap early return. Unhook() already stops raises when idle, but LabAPI invokes from a
            // snapshot: if an earlier handler in the same raise called Unwatch, we are still called once.
            if (_watched.Count == 0)
                return;

            try
            {
                Stamp stamp;
                if (ev.Player != null && _watched.TryGetValue(ev.Player.PlayerId, out stamp))
                    stamp.Ticks = Stopwatch.GetTimestamp();
            }
            catch (Exception e)
            {
                // A throw here would cost a stack trace and regexes per packet. Keyed token bucket.
                RateLimitedLog.Error("snippets.voice", () => "SendingVoiceMessage handler failed: " + e);
            }
        }
    }
}
