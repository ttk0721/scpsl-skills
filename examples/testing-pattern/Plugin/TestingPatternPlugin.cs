using System;
using System.ComponentModel;
using System.Diagnostics;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Loader.Features.Plugins;
using SlKit;

namespace Examples.TestingPattern
{
    public sealed class TestingPatternConfig
    {
        [Description("Kill switch. Read at use time, so 'labapi reload configs' applies it.")]
        public bool IsEnabled { get; set; } = true;

        [Description("Active seconds of play per reward.")]
        public double SecondsPerReward { get; set; } = 600;

        [Description("Reward size.")]
        public int Amount { get; set; } = 10;

        [Description("Players with Remote Admin access never earn rewards.")]
        public bool ExcludeRemoteAdmin { get; set; } = true;
    }

    /// <summary>
    /// Wiring only: events and one clock job call into RewardCoordinator, which holds every decision
    /// and is covered by Tests/. Whatever is left here (subscribe, unsubscribe, translate an event
    /// argument into a UserId) is checked by compiling, by scpsl-lint, and by the smoke checklist.
    /// </summary>
    public sealed class TestingPatternPlugin : Plugin<TestingPatternConfig>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly RewardRules _rules = new RewardRules();
        private RewardCoordinator _coordinator;
        private IDisposable _tickJob;
        private bool _enabled;

        public override string Name => "TestingPattern";
        public override string Description => "Example: logic behind seams, covered by a plain .NET test runner.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        // An empty or comment-only config.yml deserialises to null.
        private TestingPatternConfig Cfg => Config ??= new TestingPatternConfig();

        public override void Enable()
        {
            if (_enabled)
                return;

            try
            {
                // The coordinator reads the rules through a Func, so a config reload (which replaces
                // the Config object) is picked up on the next tick. No world access here: Enable()
                // runs before the first map exists.
                _coordinator = new RewardCoordinator(
                    new LabApiPlayerSource(() => Cfg.ExcludeRemoteAdmin),
                    new LogRewardSink(),
                    CurrentRules);

                SlClock.Install();

                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.Shutdown += OnShutdown;
                PlayerEvents.Left += OnLeft;
                _tickJob = SlClock.Every(TimeSpan.FromSeconds(1), OnTick, "testing-pattern.tick");
                _enabled = true;
            }
            catch
            {
                Teardown();
                throw;
            }
        }

        public override void Disable() => Teardown();

        private RewardRules CurrentRules()
        {
            // Copy the live config into one reused object: no allocation per tick, and a reload shows up at once.
            TestingPatternConfig c = Cfg;
            _rules.Enabled = c.IsEnabled;
            _rules.SecondsPerReward = c.SecondsPerReward;
            _rules.Amount = c.Amount;
            return _rules;
        }

        private void OnTick() => _coordinator.Tick(_clock.Elapsed.TotalSeconds);

        private void OnWaitingForPlayers() => _coordinator.OnRoundStart();

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            // The hub is being destroyed: read the id here, keep no reference to the Player.
            _coordinator.OnLeft(ev.Player?.UserId);
        }

        // The loader never calls Disable(), so Shutdown is the one place a clean stop can hook in.
        private void OnShutdown() => Teardown();

        private void Teardown()
        {
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            PlayerEvents.Left -= OnLeft;
            _tickJob?.Dispose();
            _tickJob = null;
            _enabled = false;
        }
    }
}
