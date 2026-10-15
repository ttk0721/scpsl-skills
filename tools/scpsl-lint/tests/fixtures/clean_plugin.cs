// A plugin that follows the rules: the linter must say nothing about it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using MEC;

namespace Fixture
{
    public class CleanConfig
    {
        [System.ComponentModel.Description("Show join messages.")]
        public bool JoinMessages { get; set; } = true;

        public UnityEngine.Vector3 Spawn { get; set; }
        public UnityEngine.Color Tint { get; set; }
    }

    public class CleanPlugin : Plugin<CleanConfig>
    {
        private readonly Handlers handlers = new Handlers();
        private CoroutineHandle clock;
        private readonly Stopwatch sw = Stopwatch.StartNew();

        public override string Name => "clean";

        public override void Enable()
        {
            CustomHandlersManager.RegisterEventsHandler(handlers);
            ServerEvents.Shutdown += OnShutdown;
            ServerEvents.RoundStarted += OnRoundStarted;
            clock = Timing.RunCoroutine(Tick(), "clean-plugin");
        }

        public override void Disable()
        {
            CustomHandlersManager.UnregisterEventsHandler(handlers);
            ServerEvents.Shutdown -= OnShutdown;
            Timing.KillCoroutines("clean-plugin");
            Timing.KillCoroutines(clock);
        }

        private void OnShutdown() { Flush(); }
        private void OnRoundStarted() { Timing.CallDelayed(2f, () => { }); }
        private void Flush() { System.IO.File.WriteAllText("state.json", "{}"); }

        // one coroutine, one second period, change-driven hints
        private IEnumerator<float> Tick()
        {
            while (true)
            {
                foreach (var p in Player.ReadyList)
                {
                    if (p.Health < 20)
                        p.SendHint("low", 1f);
                }
                yield return Timing.WaitForSeconds(1f);
            }
        }

        private async Task FetchAsync(System.Net.Http.HttpClient client)
        {
            var text = await client.GetStringAsync("http://example.invalid/").ConfigureAwait(false);
            Logger.Info(text.Length);
        }
    }

    public class Handlers : CustomEventsHandler
    {
        public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            if (ev.Player == null) return;
            Logger.Info("joined " + ev.Player.Nickname);
        }

        // Hurting is raised per frame for damage-over-time effects, hence the SL001 warning; the body is a
        // type test and a multiply, which is the shape the warning asks for.
        // scpsl-lint: ignore SL001 -- gated, allocation-free handler
        public override void OnPlayerHurting(PlayerHurtingEventArgs ev)
        {
            if (ev.DamageHandler is StandardDamageHandler dmg)
                dmg.Damage *= 0.5f;
            if (ev.Attacker == null)
                ev.IsAllowed = false;
        }
    }
}
