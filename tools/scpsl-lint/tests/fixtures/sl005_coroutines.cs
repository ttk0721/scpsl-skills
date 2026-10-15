using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using MEC;

namespace Fixture
{
    public class CoroutinePerEvent : CustomEventsHandler
    {
        public void Enable()
        {
            PlayerEvents.ShootingWeapon += ev =>
            {
                Timing.CallDelayed(0.5f, () => { }); // expect: SL019   (burst event: reported by the handler-work rule)
            };
            PlayerEvents.Joined += OnJoined;
        }

        // per-action event: one coroutine per interaction
        public override void OnPlayerInteractingDoor(PlayerInteractingDoorEventArgs ev)
        {
            Timing.RunCoroutine(CloseLater(ev)); // expect: SL005
        }

        // per-lifecycle event: a welcome coroutine per join is idiomatic and cheap, no finding
        private void OnJoined(PlayerJoinedEventArgs ev)
        {
            Timing.RunCoroutine(Welcome(ev));
        }

        // per-round event: no finding
        public override void OnServerRoundStarted()
        {
            Timing.CallDelayed(5f, () => { });
        }

        public void ScheduleAll()
        {
            foreach (Player p in Player.List)
            {
                Timing.RunCoroutine(PerPlayer(p)); // expect: SL005
            }
            foreach (var hub in ReferenceHub.AllHubs)
                Timing.CallDelayed(1f, () => { }); // expect: SL005
            foreach (var item in someItems)
            {
                Timing.RunCoroutine(Other(item));
            }
        }

        private IEnumerator<float> CloseLater(PlayerInteractingDoorEventArgs ev) { yield break; }
        private IEnumerator<float> Welcome(PlayerJoinedEventArgs ev) { yield break; }
        private IEnumerator<float> PerPlayer(Player p) { yield break; }
        private IEnumerator<float> Other(object o) { yield break; }
    }
}
