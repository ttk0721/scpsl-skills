using System;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using SlKit;

namespace Examples.VoiceAudioSnippets
{
    /// <summary>
    /// Owns the pieces and wires the lifecycle events they need. One long-lived instance per plugin;
    /// Subscribe is idempotent and Unsubscribe is the undo path for a failed Enable and for Shutdown.
    /// </summary>
    /// <remarks>
    /// The hot hooks (<c>ReceivingGate</c>, <c>SendingBudget</c>, <c>DeliveryCounter</c>) are NOT started
    /// here: each is started only when its feature is switched on, and stops itself when idle.
    /// </remarks>
    internal sealed class VoiceAudioWiring
    {
        public readonly MuteLedger Mutes = new MuteLedger();
        public readonly SpectatorFlagKeeper Spectators = new SpectatorFlagKeeper();
        public readonly ReceivingGate Gate = new ReceivingGate();
        public readonly SendingBudget RadioBudget = new SendingBudget(30, TimeSpan.FromSeconds(60));
        public readonly AnnouncementSpeakers Speakers = new AnnouncementSpeakers();

        private bool _subscribed;

        public void Subscribe()
        {
            if (_subscribed)
                return;

            SlClock.Install(); // also ends ClockScope.Round jobs at RoundRestarted

            Mutes.Subscribe();
            Spectators.Start();
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.Shutdown += OnShutdown;
            PlayerEvents.Left += OnLeft;
            _subscribed = true;
        }

        public void Unsubscribe()
        {
            Mutes.Unsubscribe();
            Spectators.Stop();
            Gate.Clear();
            RadioBudget.Stop();
            Speakers.StopAll();

            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            PlayerEvents.Left -= OnLeft;
            _subscribed = false;
        }

        /// <summary>A short chime for everyone within 20 m of <paramref name="player"/>.</summary>
        public bool Chime(Player player)
        {
            float[] clip = AudioClips.Tone(880f, 0.4f);
            return Speakers.PlayNear(player.Position, clip, 20f);
        }

        private void OnWaitingForPlayers()
        {
            // New scene: every hub, toy and flag from the last round is gone.
            Mutes.ResetRound();
            Gate.Clear();
            Speakers.ResetRound();
        }

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            if (ev.Player == null)
                return;

            Gate.Forget(ev.Player.PlayerId);
            RadioBudget.Forget(ev.Player.PlayerId);
        }

        private void OnShutdown() => Unsubscribe();
    }
}
