using System;
using System.Diagnostics;
using CommandSystem;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;

namespace HotBench
{
    /// <summary>
    /// Measures what a subscription to a per-frame-per-pair event costs on a live server.
    /// Use together with sl-probe on a TEST instance: switch modes, reset the probe window,
    /// compare busy time per frame. Never on a production server.
    /// </summary>
    public sealed class HotBenchPlugin : Plugin
    {
        public override string Name => "HotBench";
        public override string Description => "Cost of subscribing to PlayerEvents.ValidatedVisibility.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(0, 1, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            ServerEvents.Shutdown += Bench.Off;
        }

        public override void Disable() => Bench.Off();
    }

    internal static class Bench
    {
        // Main-thread only: ValidatedVisibility is raised from the position sender in LateUpdate.
        private static long _count;
        private static long _sink;
        private static int _mode;
        private static long _lastCount;
        private static long _lastTimestamp = Stopwatch.GetTimestamp();

        internal static string Set(int mode)
        {
            Off();
            if (mode == 1)
                PlayerEvents.ValidatedVisibility += Light;
            else if (mode == 2)
                PlayerEvents.ValidatedVisibility += Heavy;
            else if (mode == 3)
                PlayerEvents.ValidatedVisibility += Costly;
            _mode = mode;
            _lastCount = _count;
            _lastTimestamp = Stopwatch.GetTimestamp();
            return "hotbench mode=" + Describe(mode);
        }

        internal static void Off()
        {
            PlayerEvents.ValidatedVisibility -= Light;
            PlayerEvents.ValidatedVisibility -= Heavy;
            PlayerEvents.ValidatedVisibility -= Costly;
            _mode = 0;
        }

        // The cheapest possible subscriber: measures the subscription itself
        // (delegate list allocation per raise + the call), not the handler's work.
        private static void Light(PlayerValidatedVisibilityEventArgs ev) => _count++;

        // What a typical "rule per pair" plugin touches: both wrappers, a role, a position.
        private static void Heavy(PlayerValidatedVisibilityEventArgs ev)
        {
            _count++;
            Player observer = ev.Player;
            Player target = ev.Target;
            if (observer == null || target == null)
                return;
            if (observer.Role == target.Role)
                _sink++;
            if (target.Position.y > 100000f)
                _sink++;
        }

        // What a careless per-pair rule does: builds a string, allocates a small list and looks the
        // player up again, on every raise. The point is to show the cost of per-raise work next to
        // the subscription alone.
        private static void Costly(PlayerValidatedVisibilityEventArgs ev)
        {
            _count++;
            Player observer = ev.Player;
            Player target = ev.Target;
            if (observer == null || target == null)
                return;
            string key = $"{observer.UserId}->{target.UserId}"; // scpsl-lint: ignore SL018 -- deliberate: this arm measures per-raise allocation
            var tags = new System.Collections.Generic.List<string>(2) { key }; // scpsl-lint: ignore SL018 -- deliberate: this arm measures per-raise allocation
            Player again = Player.Get(target.ReferenceHub);
            if (again != null && tags.Count > 0)
                _sink += key.Length;
        }

        internal static string Stats()
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            long raised = _count - _lastCount;
            _lastCount = _count;
            _lastTimestamp = now;
            double rate = seconds > 0 ? raised / seconds : 0;
            return $"hotbench mode={Describe(_mode)} handler calls={raised} in {seconds:F1} s = {rate:F0}/s (sink {_sink})";
        }

        private static string Describe(int mode) => mode == 1 ? "light" : mode == 2 ? "heavy" : mode == 3 ? "costly" : "off";
    }

    /// <summary><c>hotbench off|light|heavy|costly|stats</c> on the server console.</summary>
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public sealed class HotBenchCommand : ICommand
    {
        public string Command => "hotbench";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "hotbench off | light | heavy | costly | stats";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            string arg = arguments.Count > 0 ? arguments.Array[arguments.Offset] : "stats";
            switch (arg)
            {
                case "off": response = Bench.Set(0); return true;
                case "light": response = Bench.Set(1); return true;
                case "heavy": response = Bench.Set(2); return true;
                case "costly": response = Bench.Set(3); return true;
                case "stats": response = Bench.Stats(); return true;
                default: response = Description; return false;
            }
        }
    }
}
