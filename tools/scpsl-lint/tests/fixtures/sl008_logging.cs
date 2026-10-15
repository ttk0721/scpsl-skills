using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace Fixture
{
    public class Logging : CustomEventsHandler
    {
        // hot event: logging there is reported by SL018 (see sl018_handler_work.cs), not SL008
        public override void OnPlayerReceivingVoiceMessage(PlayerReceivingVoiceMessageEventArgs ev) // expect: SL001
        {
            Logger.Debug("voice"); // expect: SL018
        }

        // per-action event that is neither hot nor burst
        public override void OnPlayerDryFiringWeapon(PlayerDryFiringWeaponEventArgs ev)
        {
            Logger.Info($"{ev.Player.Nickname} shot"); // expect: SL008
            Logger.Raw("x", System.ConsoleColor.Red); // expect: SL008
            Logger.Error("errors on a per-action event are not flagged");
        }

        // per-lifecycle and per-round events are rare enough
        public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            Logger.Info("joined");
            Logger.Debug("joined");
        }

        public void Dump(string[] lines)
        {
            Logger.Info("starting"); // outside loops: fine
            foreach (var line in lines)
            {
                Logger.Debug(line); // expect: SL008
            }
            for (int i = 0; i < 3; i++)
                Logger.Info("i"); // expect: SL008
            foreach (var line in lines)
            {
                Logger.Warn(line); // warn/error in an ordinary loop is allowed
            }
            foreach (Player p in Player.List)
            {
                Logger.Warn(p.Nickname); // expect: SL008
            }
        }

        private void Update()
        {
            Logger.Info("per frame"); // expect: SL008
        }

        private void Gated(bool debug)
        {
            if (debug)
            {
                Logger.Debug("fine, not in a loop or hot handler");
            }
        }
    }
}
