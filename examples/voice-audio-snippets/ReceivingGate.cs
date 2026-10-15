using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using SlKit;
using VoiceChat;

namespace Examples.VoiceAudioSnippets
{
    /// <summary>
    /// Per-listener routing ("A must not hear B", "this player hears nobody") as a last resort,
    /// built so the handler allocates nothing and does at most two set lookups.
    /// </summary>
    /// <remarks>
    /// <para><c>ReceivingVoiceMessage</c> runs once per receiver per packet (talkers x 100 x hubs per
    /// second) and the game builds its args for every hub even with no subscriber. So the handler is
    /// only subscribed while at least one rule exists, returns first on a channel the game already
    /// dropped, and touches only the listener (<c>ev.Player</c>, already resolved by the args
    /// constructor) and the speaker's hub id. It never calls <c>ev.Sender</c> (an extra
    /// <c>Player.Get</c>), never logs, never throws.</para>
    /// <para>Never edit <c>ev.Message</c>: the data buffer is shared by every later receiver. And do not
    /// deny a whole channel, least of all Radio: the client plays radio talkers to nearby bystanders
    /// from the Radio-labelled packet, so denying Radio silences them for the people next to them.</para>
    /// </remarks>
    internal sealed class ReceivingGate
    {
        private readonly HashSet<int> _deaf = new HashSet<int>();
        private readonly HashSet<long> _blockedPairs = new HashSet<long>();
        private bool _hooked;

        public int RuleCount => _deaf.Count + _blockedPairs.Count;

        /// <summary>The listener hears no voice at all.</summary>
        public void Deafen(Player listener)
        {
            if (_deaf.Add(listener.PlayerId))
                Hook();
        }

        public void Undeafen(Player listener)
        {
            _deaf.Remove(listener.PlayerId);
            UnhookIfIdle();
        }

        /// <summary><paramref name="listener"/> stops hearing <paramref name="speaker"/>.</summary>
        public void Block(Player listener, Player speaker)
        {
            if (_blockedPairs.Add(Pair(listener.PlayerId, speaker.PlayerId)))
                Hook();
        }

        public void Unblock(Player listener, Player speaker)
        {
            _blockedPairs.Remove(Pair(listener.PlayerId, speaker.PlayerId));
            UnhookIfIdle();
        }

        /// <summary>A player left: forget every rule that names their id (ids are recycled from a free queue, so a stale rule could hit a later player).</summary>
        public void Forget(int playerId)
        {
            _deaf.Remove(playerId);
            // Rare event, so the O(n) rebuild is fine here and never runs per packet.
            _blockedPairs.RemoveWhere(key => (int)(key >> 32) == playerId || (int)key == playerId);
            UnhookIfIdle();
        }

        public void Clear()
        {
            _deaf.Clear();
            _blockedPairs.Clear();
            UnhookIfIdle();
        }

        private static long Pair(int listenerId, int speakerId) => ((long)listenerId << 32) | (uint)speakerId;

        private void Hook()
        {
            if (_hooked)
                return;

            // scpsl-lint: ignore SL001 -- gated: subscribed only while a rule exists, handler is two set lookups
            PlayerEvents.ReceivingVoiceMessage += OnReceiving;
            _hooked = true;
        }

        private void UnhookIfIdle()
        {
            if (!_hooked || RuleCount != 0)
                return;

            PlayerEvents.ReceivingVoiceMessage -= OnReceiving;
            _hooked = false;
        }

        // Per receiver per packet. Order matters: cheapest and most selective test first.
        private void OnReceiving(PlayerReceivingVoiceMessageEventArgs ev)
        {
            // A channel the game already dropped: nothing to decide, and most calls end here.
            if (ev.Message.Channel == VoiceChatChannel.None)
                return;

            try
            {
                Player listener = ev.Player;
                if (listener == null)
                    return;

                int listenerId = listener.PlayerId;
                if (_deaf.Contains(listenerId))
                {
                    ev.IsAllowed = false;
                    return;
                }

                if (_blockedPairs.Count != 0 && ev.Message.Speaker != null
                    && _blockedPairs.Contains(Pair(listenerId, ev.Message.Speaker.PlayerId)))
                    ev.IsAllowed = false;
            }
            catch (Exception e)
            {
                // A throw per packet would log a stack trace per packet: hand it to the token bucket.
                // (The closure allocates only on this failure path.)
                RateLimitedLog.Error("snippets.recv-gate", () => "ReceivingVoiceMessage handler failed: " + e);
            }
        }
    }

    /// <summary>
    /// Counts actual voice deliveries without any event args: the plain C# event
    /// <c>VoiceTransceiver.OnVoiceMessageReceiving</c> fires once per delivery that really goes out.
    /// Use it to compare the live rate with talkers x receivers before and after a routing change.
    /// </summary>
    /// <remarks>The handler is one integer increment. Roughly 100 000 calls per second at 10 talkers
    /// and ~100 hubs, so subscribe only while measuring.</remarks>
    internal sealed class DeliveryCounter
    {
        private long _deliveries;
        private long _lastSeen;
        private IDisposable _job;

        public bool IsRunning => _job != null;

        public void Start()
        {
            if (_job != null)
                return;

            _deliveries = 0;
            _lastSeen = 0;
            VoiceChat.Networking.VoiceTransceiver.OnVoiceMessageReceiving += OnDelivery;
            _job = SlClock.Every(TimeSpan.FromSeconds(10), Report, "voice.delivery-counter");
        }

        public void Stop()
        {
            VoiceChat.Networking.VoiceTransceiver.OnVoiceMessageReceiving -= OnDelivery;
            if (_job != null)
                _job.Dispose();
            _job = null;
        }

        // Main thread only, so a plain increment is exact.
        private void OnDelivery(VoiceChat.Networking.VoiceMessage message, ReferenceHub receiver) => _deliveries++;

        private void Report()
        {
            long delta = _deliveries - _lastSeen;
            _lastSeen = _deliveries;
            RateLimitedLog.Info("snippets.deliveries", () => "voice deliveries in the last 10 s: " + delta);
        }
    }
}
