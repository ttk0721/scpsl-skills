// Fixture for SL001 through `using static` and `using Alias = ...` (resolved per file).
using static LabApi.Events.Handlers.PlayerEvents;
using Scp173 = LabApi.Events.Handlers.Scp173Events;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Console;

namespace Fixture
{
    public class BareSubscriptions
    {
        public void Enable()
        {
            ValidatedVisibility += OnVisibility; // expect: SL001
            ReceivingVoiceMessage += ev => { }; // expect: SL001
            Hurting += OnHurting; // expect: SL001
            Scp173.AddingObserver += delegate (Scp173AddingObserverEventArgs ev) { }; // expect: SL001

            // Not hot, or not an event subscription at all.
            Joined += OnJoined;
            ValidatedVisibility -= OnVisibility;
            owner.ValidatedVisibility += OnVisibility;
            this.ReceivingVoiceMessage += OnVisibility;
        }

        // The handler body is found through the bare subscription, so per-raise work inside is reported.
        private void OnVisibility(PlayerValidatedVisibilityEventArgs ev)
        {
            Logger.Debug("pair"); // expect: SL018
        }

        private void OnHurting(PlayerHurtingEventArgs ev) { }
        private void OnJoined(PlayerJoinedEventArgs ev) { }
        private Owner owner;
    }
}
