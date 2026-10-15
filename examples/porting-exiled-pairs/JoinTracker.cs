using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 2 (Verified to Joined, Destroying to Left). The EXILED original:
    /// <code>
    /// Handlers.Player.Verified   += ev => Timing.CallDelayed(1f, () => ev.Player.Broadcast(5, "Welcome"));
    /// Handlers.Player.Destroying += ev => Forget(ev.Player.UserId);
    /// </code>
    /// Three differences drive the port:
    /// 1. LabAPI <c>Joined</c> is LATER than EXILED <c>Verified</c> (after badge and group handling), so the
    ///    player is already <c>IsReady</c> and the one-second delay is not needed.
    /// 2. <c>Joined</c> is skipped when the badge UserID or nickname does not match (the player stays connected
    ///    and never gets it). Whatever you track from Joined is therefore "joined cleanly", not "connected".
    /// 3. LabAPI <c>Left</c> (the hub destroy callback) also fires for never-authenticated hubs, NPCs and
    ///    dummies, unlike EXILED <c>Destroying</c>. A set filled in Joined is the guard. Do not key per-player
    ///    state by user id for dummies: they all share the id "ID_Dummy".
    /// </summary>
    internal sealed class JoinTracker
    {
        private readonly HashSet<string> _joined = new HashSet<string>();
        private readonly Messaging _messaging;
        private bool _started;

        public JoinTracker(Messaging messaging)
        {
            _messaging = messaging;
        }

        /// <summary>Clean joins currently connected. O(1): keep your own count instead of Player.Count (a LINQ filter per call).</summary>
        public int Count => _joined.Count;

        public void Start()
        {
            if (_started) return;
            PlayerEvents.Joined += OnJoined;   // was Verified
            PlayerEvents.Left += OnLeft;       // was Destroying
            _started = true;
        }

        public void Stop()
        {
            if (!_started) return;
            PlayerEvents.Joined -= OnJoined;
            PlayerEvents.Left -= OnLeft;
            _joined.Clear();
            _started = false;
        }

        private void OnJoined(PlayerJoinedEventArgs ev)
        {
            string id = ev.Player.UserId;
            if (string.IsNullOrEmpty(id)) return;
            _joined.Add(id);

            // A late joiner already has a role here (it was assigned before Joined); in the lobby it has none yet.
            _messaging.Welcome(ev.Player);
        }

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            // The wrapper is alive during Left, so UserId and Nickname are readable (IpAddress may throw: capture it earlier).
            string id = ev.Player.UserId;
            if (id == null || !_joined.Remove(id)) return;   // never joined: unauthenticated hub, NPC, dummy, badge mismatch

            // Per-player cleanup of your own state goes here, keyed by the user id.
        }
    }
}
