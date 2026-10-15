using LabApi.Events.Handlers;
using LabApi.Loader.Features.Plugins;

namespace Fixture
{
    // Disable() does real cleanup, but nothing in this project subscribes to ServerEvents.Shutdown.
    public class LeakyPlugin : Plugin
    {
        public override string Name => "leaky";

        public override void Enable()
        {
            PlayerEvents.Joined += OnJoined;
        }

        public override void Disable() // expect: SL015
        {
            PlayerEvents.Joined -= OnJoined;
            SaveState();
        }

        private void OnJoined(LabApi.Events.Arguments.PlayerEvents.PlayerJoinedEventArgs ev) { }
        private void SaveState() { }
    }

    // An empty Disable() has nothing to lose.
    public class EmptyDisable : Plugin
    {
        public override string Name => "empty";
        public override void Enable() { }
        public override void Disable() { }
    }
}
