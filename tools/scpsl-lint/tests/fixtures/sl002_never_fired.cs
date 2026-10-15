using LabApi.Events.CustomHandlers;
using LabApi.Events.Handlers;

namespace Fixture
{
    public class NeverFired : CustomEventsHandler
    {
        public void Enable()
        {
            PlayerEvents.SearchedAmmo += ev => { }; // expect: SL002
            ServerEvents.CassieAnnounced += OnCassie; // expect: SL002
            PlayerEvents.ShootingWeapon += OnShot; // fired for real: no finding
        }

        public override void OnPlayerSearchedArmor(PlayerSearchedArmorEventArgs ev) // expect: SL002
        {
        }

        private void OnCassie(CassieAnnouncedEventArgs ev) { }
        private void OnShot(PlayerShootingWeaponEventArgs ev) { }
    }
}
