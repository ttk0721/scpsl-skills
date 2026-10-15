// Fixture for SL001 (and negatives). Lines tagged "expect:" must produce exactly those rules.
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Events.Handlers;

namespace Fixture
{
    public class StaticSubscriptions
    {
        public void Enable()
        {
            PlayerEvents.ValidatedVisibility += OnVisibility; // expect: SL001
            PlayerEvents.ReceivingVoiceMessage += ev => { }; // expect: SL001
            LabApi.Events.Handlers.PlayerEvents.TriggeringTesla += new LabEventHandler<PlayerTriggeringTeslaEventArgs>(OnTesla); // expect: SL001
            Scp173Events.AddingObserver += delegate (Scp173AddingObserverEventArgs ev) { }; // expect: SL001

            // Not hot: lifecycle and per-round events are fine.
            PlayerEvents.Joined += OnJoined;
            PlayerEvents.Left += (ev) => { };
            ServerEvents.RoundStarted += OnRoundStarted;
        }

        public void Disable()
        {
            // Unsubscribing is never a finding.
            PlayerEvents.ValidatedVisibility -= OnVisibility;
        }

        private void OnVisibility(PlayerValidatedVisibilityEventArgs ev) { }
        private void OnTesla(PlayerTriggeringTeslaEventArgs ev) { }
        private void OnJoined(PlayerJoinedEventArgs ev) { }
        private void OnRoundStarted() { }
    }

    public class OverrideSubscriptions : CustomEventsHandler
    {
        public override void OnPlayerSendingVoiceMessage(PlayerSendingVoiceMessageEventArgs ev) // expect: SL001
        {
        }

        public override void OnPlayerStayingInHazard(PlayerStayingInHazardEventArgs ev) => System.GC.KeepAlive(ev); // expect: SL001

        public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
        }

        public override void OnPlayerDeath(PlayerDeathEventArgs ev)
        {
        }
    }

    public class MentionsInCommentsAndStrings
    {
        public void Enable()
        {
            // PlayerEvents.ValidatedVisibility += OnVisibility;
            /* PlayerEvents.ReceivingVoiceMessage += OnVoice; */
            string s = "PlayerEvents.ValidatedVisibility += OnVisibility;";
            string v = @"PlayerEvents.ReceivingVoiceMessage += OnVoice;";
        }
    }
}
