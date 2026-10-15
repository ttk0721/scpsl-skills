using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;

namespace Examples.LabapiEventsSnippets
{
    /// <summary>
    /// Style B: a <see cref="CustomEventsHandler"/> subclass. Only methods that OVERRIDE a base virtual
    /// are hooked, so each override below is one subscription at that event's frequency. A method with
    /// the right name but another signature, or declared <c>new</c>, is silently ignored.
    /// </summary>
    internal sealed class RoundNotesHandler : CustomEventsHandler
    {
        private readonly PreAuthBridge _preAuth;

        public RoundNotesHandler(PreAuthBridge preAuth)
        {
            _preAuth = preAuth;
        }

        public int JoinsThisRound { get; private set; }
        public int AnnouncedJoinsThisRound { get; private set; }

        // Name rule: On + class name without "Events" + event name (ServerEvents.WaitingForPlayers).
        public override void OnServerWaitingForPlayers()
        {
            JoinsThisRound = 0;
            AnnouncedJoinsThisRound = 0;
            _preAuth.ResetRound();
        }

        // PlayerEvents.Joined is once per connection, so this is cheap.
        public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            if (ev.Player == null || ev.Player.IsHost)
                return;

            JoinsThisRound++;
            if (_preAuth.TryConsume(ev.Player.UserId))
                AnnouncedJoinsThisRound++; // pre-auth saw this account before the hub existed
        }
    }

    /// <summary>
    /// Registration discipline for style B. Registering costs one reflection pass per event
    /// (377 of them), so do it once from a long-lived owner, never per round or per player. Registering
    /// the SAME instance twice throws partway and leaves one stale subscription that
    /// <c>Unregister</c> cannot remove, so the flag matters.
    /// </summary>
    internal sealed class CustomHandlerWiring
    {
        private readonly RoundNotesHandler _handler;
        private bool _registered;

        public CustomHandlerWiring(RoundNotesHandler handler)
        {
            _handler = handler;
        }

        public void Register()
        {
            if (_registered)
                return;

            CustomHandlersManager.RegisterEventsHandler(_handler);
            _registered = true;
        }

        // Unconditional on purpose: if Register threw halfway, this removes what it did hook.
        public void Unregister()
        {
            CustomHandlersManager.UnregisterEventsHandler(_handler);
            _registered = false;
        }
    }
}
