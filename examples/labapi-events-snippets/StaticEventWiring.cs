using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;

namespace Examples.LabapiEventsSnippets
{
    /// <summary>
    /// Style A: plain C# events. The owner object is long-lived (one per plugin), every handler is a
    /// method group, and every <c>+=</c> has a <c>-=</c> on the same line of the other method.
    /// </summary>
    /// <remarks>
    /// Why method groups: delegate equality is (target, method), so <c>-= OnLeft</c> removes the
    /// delegate that <c>+= OnLeft</c> added. A lambda would create a new delegate each time and could
    /// never be removed. The flag makes Subscribe idempotent: a second call adds nothing, so the
    /// handlers cannot run twice.
    /// </remarks>
    internal sealed class StaticEventWiring
    {
        private readonly CancellationRules _rules;
        private readonly VoiceActivityGate _voice;
        private bool _subscribed;

        public StaticEventWiring(CancellationRules rules, VoiceActivityGate voice)
        {
            _rules = rules;
            _voice = voice;
        }

        public void Subscribe()
        {
            if (_subscribed)
                return;

            // Per-lifecycle events: one dictionary operation each, cost does not matter.
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            PlayerEvents.Left += OnLeft;

            // Per-action events: the rules class owns the handlers, we only wire them.
            PlayerEvents.Hurting += _rules.OnHurting;
            PlayerEvents.InteractingDoor += _rules.OnInteractingDoor;
            PlayerEvents.ChangingRole += _rules.OnChangingRole;

            _subscribed = true;
        }

        // Safe to call when Subscribe never ran or threw halfway: removing a delegate that is not
        // subscribed is a no-op, which is also what makes this the undo path of a failed Enable.
        public void Unsubscribe()
        {
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            PlayerEvents.Left -= OnLeft;
            PlayerEvents.Hurting -= _rules.OnHurting;
            PlayerEvents.InteractingDoor -= _rules.OnInteractingDoor;
            PlayerEvents.ChangingRole -= _rules.OnChangingRole;
            _subscribed = false;
        }

        // Fires every round, after the old scene is gone: per-round state is rebuilt here.
        private void OnWaitingForPlayers()
        {
            _rules.ResetRound();
            _voice.Clear(); // also drops the hot subscription: nobody is watched in a new round
        }

        // Fires for every hub that disconnects, including ones that never completed Joined.
        private void OnLeft(PlayerLeftEventArgs ev)
        {
            if (ev.Player == null)
                return;

            _voice.Unwatch(ev.Player.PlayerId);
            _rules.Forget(ev.Player.PlayerId);
        }
    }
}
