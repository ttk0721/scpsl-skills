using System;
using System.ComponentModel;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Loader.Features.Plugins;
using SlKit;

namespace Examples.LabapiWrappersSnippets
{
    public sealed class WrapperSnippetsConfig
    {
        [Description("Turns the whole plugin off without removing it (kill switch).")]
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// Wires the snippets the way a real plugin would: per-round state rebuilt at WaitingForPlayers,
    /// per-player state keyed by UserId and dropped on Left, one clock for budgeted work. The
    /// snippet classes themselves are what the labapi-wrappers skill shows.
    /// </summary>
    public sealed class WrapperSnippetsPlugin : Plugin<WrapperSnippetsConfig>
    {
        private readonly SessionIndex _sessions = new SessionIndex();
        private readonly ItemGiver _items = new ItemGiver();
        private readonly FacilityIndex _facility = new FacilityIndex();
        private readonly PickupBurst _burst = new PickupBurst();
        private readonly Messaging _messaging = new Messaging();
        private IDisposable _burstJob;
        private bool _subscribed;

        public override string Name => "LabapiWrappersSnippets";
        public override string Description => "Ten common wrapper operations done right.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            if (Config == null || !Config.IsEnabled || _subscribed)
                return;

            SlClock.Install();
            _burstJob = SlClock.Every(TimeSpan.FromMilliseconds(100), PumpBurst, "wrapper-snippets.burst");

            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.Shutdown += OnShutdown;
            PlayerEvents.Joined += OnJoined;
            PlayerEvents.Left += OnLeft;
            _subscribed = true;
        }

        public override void Disable() => Unsubscribe();

        private void PumpBurst() => _burst.Pump(5);

        private void OnWaitingForPlayers()
        {
            _items.Clear();
            _burst.Clear();
            _messaging.Clear();

            // Retry later when the map is not complete yet (an empty index is not a result).
            _facility.Rebuild();
        }

        private void OnJoined(PlayerJoinedEventArgs ev) => _sessions.OnJoined(ev.Player);

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            if (ev.Player == null)
                return;
            _sessions.OnLeft(ev.Player);
            _items.ForgetHeldBy(ev.Player);
            _messaging.OnLeft(ev.Player);
        }

        private void OnShutdown() => Unsubscribe();

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            PlayerEvents.Joined -= OnJoined;
            PlayerEvents.Left -= OnLeft;
            _burstJob?.Dispose();
            _burstJob = null;
            _subscribed = false;
        }
    }
}
