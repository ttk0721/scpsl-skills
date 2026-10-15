using System;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerRoles.Spectating;
using PlayerRoles.Voice;
using SlKit;

namespace Examples.VoiceAudioSnippets
{
    /// <summary>
    /// Keeps spectators from receiving the living players' voice, with zero per-packet cost.
    /// </summary>
    /// <remarks>
    /// <para>The pipeline already tests the receiving spectator's <c>ReceiveFlags</c> for every packet,
    /// so setting <c>GroupMuteFlags.Alive</c> removes Proximity, Radio, Intercom and SCP-1576 for that
    /// receiver at no extra cost, and saves the uplink the map-wide spectator delivery would use.</para>
    /// <para>The flags are a client PREFERENCE: written again at every role spawn and every time the
    /// client re-sends its voice settings, and no hook runs after that overwrite. So the value is set in
    /// <c>ChangedRole</c> (after the new role is initialised) and re-applied on a slow timer. Treat the
    /// result as "usually on", never as a guarantee.</para>
    /// </remarks>
    internal sealed class SpectatorFlagKeeper
    {
        private static readonly TimeSpan ReapplyEvery = TimeSpan.FromSeconds(2);

        private IDisposable _job;
        private bool _subscribed;

        public void Start()
        {
            if (_subscribed)
                return;

            // ChangedRole is a per-action event, not a hot one: one type test per role change.
            LabApi.Events.Handlers.PlayerEvents.ChangedRole += OnChangedRole;
            _job = SlClock.Every(ReapplyEvery, ReapplyAll, "voice.spectator-flags");
            _subscribed = true;
        }

        public void Stop()
        {
            LabApi.Events.Handlers.PlayerEvents.ChangedRole -= OnChangedRole;
            if (_job != null)
                _job.Dispose();
            _job = null;
            _subscribed = false;
        }

        private static void OnChangedRole(PlayerChangedRoleEventArgs ev)
        {
            if (ev.Player != null)
                Apply(ev.Player);
        }

        // Slow job: one type test and one OR per spectator. Player.List is a raw dictionary view, so
        // this allocates an enumerator every 2 s, which is irrelevant at this rate.
        private static void ReapplyAll()
        {
            foreach (Player player in Player.List)
                Apply(player);
        }

        private static void Apply(Player player)
        {
            if (!player.IsPlayer || !player.IsReady || player.Role != RoleTypeId.Spectator)
                return;

            SpectatorVoiceModule module = player.VoiceModule as SpectatorVoiceModule;
            if (module == null)
                return;

            // Only the bit this feature owns is touched; the player's other preferences stay.
            if ((module.ReceiveFlags & GroupMuteFlags.Alive) == 0)
                module.ReceiveFlags |= GroupMuteFlags.Alive;
        }
    }
}
