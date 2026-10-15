using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using PlayerRoles.Voice;
using SlKit;
using VoiceChat;

namespace Examples.VoiceAudioSnippets
{
    /// <summary>
    /// A per-speaker policy that really needs <c>SendingVoiceMessage</c>: a radio talk-time budget per
    /// window. One raise per accepted packet (about 100 per second per speaker), so the handler is a
    /// dictionary lookup and an integer compare.
    /// </summary>
    /// <remarks>
    /// <para>If the policy were "this player may not talk", use <c>Mute(true)</c> instead: it is checked
    /// before the event and costs nothing per packet.</para>
    /// <para>Cancelling here means nobody receives that packet, but the game has already set
    /// <c>CurrentChannel</c> and kept <c>ServerIsSending</c> alive, so consumers that read those (radio
    /// position broadcast, 939 detection) still see a talker. Edits to <c>ev.Message.Channel</c> are
    /// ignored, so the channel cannot be rewritten from here either.</para>
    /// </remarks>
    internal sealed class SendingBudget
    {
        // A reference type: the handler does one lookup, then increments a field.
        private sealed class Used
        {
            public int Packets;
        }

        private readonly Dictionary<int, Used> _used = new Dictionary<int, Used>(32);
        private readonly int _maxPacketsPerWindow;
        private readonly TimeSpan _window;
        private IDisposable _job;
        private bool _hooked;

        /// <param name="maxSecondsPerWindow">Radio seconds allowed per window (100 packets = 1 s).</param>
        /// <param name="window">How often the allowance refills.</param>
        public SendingBudget(int maxSecondsPerWindow, TimeSpan window)
        {
            _maxPacketsPerWindow = maxSecondsPerWindow * 100;
            _window = window;
        }

        public void Start()
        {
            if (_hooked)
                return;

            // scpsl-lint: ignore SL001 -- one lookup and one increment per packet; per-speaker policy cannot be done any cheaper
            PlayerEvents.SendingVoiceMessage += OnSending;
            _job = SlClock.Every(_window, Refill, "voice.radio-budget");
            _hooked = true;
        }

        public void Stop()
        {
            PlayerEvents.SendingVoiceMessage -= OnSending;
            if (_job != null)
                _job.Dispose();
            _job = null;
            _used.Clear();
            _hooked = false;
        }

        /// <summary>Drops a player's counter (call from Left). Rejoining starts a fresh allowance.</summary>
        public void Forget(int playerId) => _used.Remove(playerId);

        // Wall-clock window: zero the counters instead of allocating new ones.
        private void Refill()
        {
            foreach (Used used in _used.Values)
                used.Packets = 0;
        }

        private void OnSending(PlayerSendingVoiceMessageEventArgs ev)
        {
            if (ev.Player == null)
                return;

            try
            {
                // Only radio talk is budgeted. Read the channel the game VALIDATED (CurrentChannel is set
                // before this event); ev.Message.Channel is what the client asked for.
                VoiceModuleBase module = ev.Player.VoiceModule;
                if (module == null || module.CurrentChannel != VoiceChatChannel.Radio)
                    return;

                int id = ev.Player.PlayerId;
                Used used;
                if (!_used.TryGetValue(id, out used))
                {
                    used = new Used(); // once per player per round, not per packet
                    _used[id] = used;
                }

                if (++used.Packets > _maxPacketsPerWindow)
                    ev.IsAllowed = false;
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("snippets.send-budget", () => "SendingVoiceMessage handler failed: " + e);
            }
        }
    }
}
