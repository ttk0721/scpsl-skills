using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using PlayerRoles;
using SlKit;

namespace Examples.NetworkingSnippets
{
    internal sealed class GhostOptions
    {
        /// <summary>Seconds between "ghost requested" and "removed from the position stream". Sounds that are playing are not cut mid-clip (hiding-players.md: hide only after about 1 s).</summary>
        public float HideDelaySeconds { get; set; } = 1f;

        /// <summary>Seconds a revealed player's voice and shots stay blocked, so the first packets are not played at the parked transform. About 35 ms is the estimate in the skill; unverified in game.</summary>
        public float VoiceLeadSeconds { get; set; } = 0.04f;

        /// <summary>A hidden shooter's shot plays at the parked transform with volume 0 (a silent kill). Blocking shots is the safe default; allowing them needs the Harmony patch described in hiding-players.md.</summary>
        public bool BlockShots { get; set; } = true;

        /// <summary>Cancel the ghost's voice. Radio voice is not range-gated, so a hidden speaker on a radio would still be heard by every receiver.</summary>
        public bool MuteVoice { get; set; } = true;

        /// <summary>SCP receivers keep seeing ghosts: 939, 096 and 106 have their own client-checked perception, and hiding a target from them breaks mechanics (hiding-players.md).</summary>
        public bool ExemptScps { get; set; } = true;
    }

    /// <summary>
    /// A correct, deliberately small way to hide chosen players from other players ("ghost").
    /// It is the position-stream half of hiding plus every audio caveat that can be handled without
    /// Harmony. It is not an anti-ESP layer: that needs the full send-set rules in hiding-players.md.
    /// </summary>
    /// <remarks>
    /// What the vanilla client does with a player missing from its position message: the model is
    /// parked at (0, 6000, 0) every frame, instantly, with no grace period. Everything positioned on
    /// that transform goes with it: footsteps, item sounds, proximity voice, gunshots. So:
    ///  - ValidatedVisibility is only the position stream. Voice and gunshots use the VANILLA check,
    ///    so a ghost is vanilla-visible but absent on the client. We therefore also block the ghost's
    ///    voice and shots instead of pretending the layer handles audio.
    ///  - Footsteps and item sounds cannot be fixed from a plugin: a ghost is silent by design.
    ///  - A reveal is instant and the client snaps (no interpolation from the sky), but the first
    ///    1-3 voice packets of a just-revealed speaker are lost, hence the voice lead.
    ///  - A noclip target is frozen VISIBLE at its old position by the client. Never hide one:
    ///    admission is refused, and turning noclip on ends the ghost state.
    ///  - Human roles only. Hiding SCPs breaks 173/096/049/zombie mechanics, which are server-side
    ///    visibility checks.
    ///  - Known limit: the real position still leaks through role-sync data (RoleSyncInfo) to a
    ///    client that reads raw packets. Closing that needs a Harmony patch (hiding-players.md).
    ///
    /// Cost: ValidatedVisibility fires for every receiver x target pair on every position tick and
    /// SendingVoiceMessage fires per voice packet. The game allocates their args even with no
    /// subscriber, so the only saving is ours: we subscribe only while at least one ghost exists, and
    /// the handlers are hash lookups on precomputed sets (no geometry, no allocation, no logging).
    /// </remarks>
    internal sealed class GhostGate : IDisposable
    {
        private readonly GhostOptions _options;

        private readonly HashSet<int> _hidden = new HashSet<int>();        // targets removed from the position stream
        private readonly HashSet<int> _silent = new HashSet<int>();        // ghosts plus pending and just-revealed: voice and shots blocked
        private readonly HashSet<int> _exemptViewers = new HashSet<int>(); // receivers that always get everyone (role based)
        private readonly HashSet<int> _staffViewers = new HashSet<int>();  // receivers the operator chose to exempt
        private readonly HashSet<int> _releasing = new HashSet<int>();     // revealed, voice and shots still blocked for the lead time
        private readonly Dictionary<int, IDisposable> _timers = new Dictionary<int, IDisposable>();

        private bool _active;

        public GhostGate(GhostOptions options)
        {
            _options = options ?? new GhostOptions();
        }

        public int Count => _silent.Count;

        public bool IsGhost(Player player) => player != null && _silent.Contains(player.PlayerId);

        /// <summary>Receivers who still see every ghost (for example the admin who made them). Cleared on round reset and when the receiver leaves.</summary>
        public void ExemptViewer(Player viewer)
        {
            if (viewer != null)
                _staffViewers.Add(viewer.PlayerId);
        }

        /// <summary>Starts hiding <paramref name="player"/>. Returns false with a reason when the player must not be hidden.</summary>
        public bool TryGhost(Player player, out string refusal)
        {
            refusal = null;
            if (player == null || player.IsDestroyed || player.IsHost || !player.IsReady)
                refusal = "not a connected player";
            else if (!player.Role.IsHuman())
                refusal = "only human roles can be hidden (SCP mechanics depend on normal visibility)";
            else if (player.IsNoclipEnabled)
                refusal = "a player in noclip cannot be hidden: the client freezes the model visible";
            if (refusal != null)
                return false;

            int id = player.PlayerId;
            if (_silent.Contains(id) && !_releasing.Contains(id))
                return true; // already a ghost or about to be

            Activate();
            CancelTimer(id); // a reveal's voice-lead timer may still be running: ghosting again takes over from it
            _releasing.Remove(id);
            _silent.Add(id);

            if (_options.HideDelaySeconds <= 0f)
                _hidden.Add(id);
            else
                _timers[id] = SlClock.After(TimeSpan.FromSeconds(_options.HideDelaySeconds), () => Hide(id), "ghost.hide");
            return true;
        }

        /// <summary>Stops hiding. Position returns on the next tick; voice and shots follow after the lead.</summary>
        public bool Reveal(Player player) => player != null && RevealId(player.PlayerId);

        /// <summary>Drops everything. Call at WaitingForPlayers and on shutdown: PlayerIds do not survive a round.</summary>
        public void ResetRound()
        {
            foreach (IDisposable timer in _timers.Values)
                timer.Dispose();
            _timers.Clear();
            _hidden.Clear();
            _silent.Clear();
            _releasing.Clear();
            _staffViewers.Clear();
            MaybeDeactivate();
        }

        public void Dispose() => ResetRound();

        // ---- state changes --------------------------------------------------------------------

        private void Hide(int id)
        {
            _timers.Remove(id);
            if (_silent.Contains(id))
                _hidden.Add(id); // only if it was not revealed while the timer ran
        }

        private bool RevealId(int id)
        {
            if (!_silent.Contains(id))
                return false;

            CancelTimer(id);
            bool wasHidden = _hidden.Remove(id);

            if (wasHidden && _options.VoiceLeadSeconds > 0f)
            {
                _releasing.Add(id);
                _timers[id] = SlClock.After(TimeSpan.FromSeconds(_options.VoiceLeadSeconds), () => ReleaseVoice(id), "ghost.voice-lead");
            }
            else
            {
                _releasing.Remove(id);
                _silent.Remove(id);
            }

            MaybeDeactivate();
            return true;
        }

        private void ReleaseVoice(int id)
        {
            _timers.Remove(id);
            _releasing.Remove(id);
            _silent.Remove(id);
            MaybeDeactivate();
        }

        private void Forget(int id)
        {
            CancelTimer(id);
            _hidden.Remove(id);
            _silent.Remove(id);
            _releasing.Remove(id);
            _exemptViewers.Remove(id);
            _staffViewers.Remove(id);
            MaybeDeactivate();
        }

        private void CancelTimer(int id)
        {
            if (_timers.TryGetValue(id, out IDisposable timer))
            {
                timer.Dispose();
                _timers.Remove(id);
            }
        }

        // ---- subscriptions: only while a ghost exists ------------------------------------------

        private void Activate()
        {
            if (_active)
                return;

            _exemptViewers.Clear();
            foreach (Player player in Player.List)
                RefreshViewer(player);

            // scpsl-lint: ignore SL001 -- subscribed only while a ghost exists; the handler is two hash lookups on precomputed sets
            PlayerEvents.ValidatedVisibility += OnValidatedVisibility;
            if (_options.MuteVoice)
            {
                // scpsl-lint: ignore SL001 -- same gate; one hash lookup per voice packet, and the cancel saves forwarding the packet
                PlayerEvents.SendingVoiceMessage += OnSendingVoiceMessage;
            }
            if (_options.BlockShots)
                PlayerEvents.ShootingWeapon += OnShootingWeapon;
            PlayerEvents.TogglingNoclip += OnTogglingNoclip;
            PlayerEvents.ChangedRole += OnChangedRole;
            PlayerEvents.Left += OnLeft;
            _active = true;
        }

        private void MaybeDeactivate()
        {
            if (!_active || _silent.Count != 0 || _hidden.Count != 0)
                return;

            // -= of a handler that was never added is a no-op, so this is safe for every option combination.
            PlayerEvents.ValidatedVisibility -= OnValidatedVisibility;
            PlayerEvents.SendingVoiceMessage -= OnSendingVoiceMessage;
            PlayerEvents.ShootingWeapon -= OnShootingWeapon;
            PlayerEvents.TogglingNoclip -= OnTogglingNoclip;
            PlayerEvents.ChangedRole -= OnChangedRole;
            PlayerEvents.Left -= OnLeft;
            _exemptViewers.Clear();
            _active = false;
        }

        // ---- handlers --------------------------------------------------------------------------

        // Hot: every receiver x target pair, every position tick. Flag tests only. It never sets
        // IsVisible = true: the layer narrows the vanilla result and must not reveal a culled target.
        private void OnValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
        {
            if (!ev.IsVisible)
                return;

            Player target = ev.Target;
            Player viewer = ev.Player;
            if (target == null || viewer == null || !_hidden.Contains(target.PlayerId))
                return;

            int viewerId = viewer.PlayerId;
            if (_exemptViewers.Contains(viewerId) || _staffViewers.Contains(viewerId))
                return;

            ev.IsVisible = false;
        }

        // Hot: per voice packet from each talking client.
        private void OnSendingVoiceMessage(PlayerSendingVoiceMessageEventArgs ev)
        {
            Player speaker = ev.Player;
            if (speaker != null && _silent.Contains(speaker.PlayerId))
                ev.IsAllowed = false;
        }

        private void OnShootingWeapon(PlayerShootingWeaponEventArgs ev)
        {
            Player shooter = ev.Player;
            if (shooter != null && _silent.Contains(shooter.PlayerId))
                ev.IsAllowed = false;
        }

        private void OnTogglingNoclip(PlayerTogglingNoclipEventArgs ev)
        {
            Player player = ev.Player;
            if (player == null || !ev.NewNoclipState || !_silent.Contains(player.PlayerId))
                return;

            RevealId(player.PlayerId); // never hide noclip targets
            player.SendHint("Ghost mode ended: a player in noclip cannot be hidden.", 4f);
        }

        private void OnChangedRole(PlayerChangedRoleEventArgs ev)
        {
            Player player = ev.Player;
            if (player == null)
                return;

            RefreshViewer(player);
            if (_silent.Contains(player.PlayerId) && !player.Role.IsHuman())
                RevealId(player.PlayerId); // died or became an SCP: nothing left to hide
        }

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            if (ev.Player != null)
                Forget(ev.Player.PlayerId); // ids are recycled: never keep one past the player
        }

        private void RefreshViewer(Player player)
        {
            if (player == null)
                return;

            RoleTypeId role = player.Role;
            bool exempt = role == RoleTypeId.Spectator
                || role == RoleTypeId.Overwatch
                || role == RoleTypeId.Filmmaker
                || role == RoleTypeId.Scp079
                || (_options.ExemptScps && role.GetTeam() == Team.SCPs);

            if (exempt)
                _exemptViewers.Add(player.PlayerId);
            else
                _exemptViewers.Remove(player.PlayerId);
        }
    }
}
