using LabApi.Events.Handlers;
using LabApi.Loader.Features.Plugins;

namespace Fixture
{
    // Same cleanup as sl015_disable_no_shutdown.cs, but the Shutdown event is wired: no finding.
    public class SafePlugin : Plugin
    {
        public override string Name => "safe";

        public override void Enable()
        {
            PlayerEvents.Joined += OnJoined;
            ServerEvents.Shutdown += OnShutdown;
        }

        public override void Disable()
        {
            PlayerEvents.Joined -= OnJoined;
            ServerEvents.Shutdown -= OnShutdown;
            SaveState();
        }

        private void OnShutdown() { SaveState(); }
        private void OnJoined(LabApi.Events.Arguments.PlayerEvents.PlayerJoinedEventArgs ev) { }
        private void SaveState() { }
    }
}
