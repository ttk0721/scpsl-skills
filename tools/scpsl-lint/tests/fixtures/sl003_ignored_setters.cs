using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Events.Handlers;

namespace Fixture
{
    public class IgnoredSetters : CustomEventsHandler
    {
        public void Enable()
        {
            PlayerEvents.Hurting += OnHurting; // expect: SL001
            PlayerEvents.Dying += ev =>
            {
                ev.DamageHandler = null; // expect: SL003
                ev.IsAllowed = false;
            };
            PlayerEvents.Muting += (PlayerMutingEventArgs e) => e.IsIntercom = true; // expect: SL003
        }

        private void OnHurting(PlayerHurtingEventArgs ev)
        {
            ev.DamageHandler = new CustomReasonDamageHandler("x"); // expect: SL003 SL018
            ev.IsAllowed = false;                                  // fine: IsAllowed is honoured
            if (ev.DamageHandler is StandardDamageHandler std)
            {
                std.Damage = 0f;                                   // fine: mutating the same object
            }
            bool same = ev.DamageHandler == null;                  // comparison, not assignment
            ev.DamageHandler.Foo = 1;                              // member of the handler, not the setter
        }

        public override void OnPlayerUnmuting(PlayerUnmutingEventArgs ev)
        {
            ev.IsIntercom = false; // expect: SL003
        }

        public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            // DamageHandler is only a trap inside the Hurting/Dying/... handlers.
            var ev2 = ev;
        }
    }
}
