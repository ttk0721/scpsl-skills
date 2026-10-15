using System;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using SlKit;

namespace Examples.DebugDoctor
{
    /// <summary>
    /// A diagnostic plugin for the "plugin not loaded" and "handler never runs" families of bugs.
    /// It does nothing until an admin runs the <c>sldoctor</c> command, so it is safe to leave installed
    /// on a test server. It subscribes to nothing at boot.
    /// </summary>
    public sealed class DebugDoctorPlugin : Plugin
    {
        public override string Name => "DebugDoctor";
        public override string Description => "Diagnostics: loader state, event subscribers, event raise counts.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);

        // A literal, never LabApiProperties.CompiledVersion (that one is computed from the running
        // LabApi.dll and can never fail the check).
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            // Enable runs on the main thread inside server start, the one place Install is allowed
            // (it throws on any other thread). Installing here records the main thread, so
            // MainThread.IsMainThread is a single read afterwards, which is what the raise-counting
            // probe uses to detect events raised off the main thread.
            SlClock.Install();
            Logger.Info("DebugDoctor ready: sldoctor plugins | subs <Class.Event> | count <Class.Event> [seconds] [force]");
        }

        // Never called by LabAPI or the game. There is nothing to flush; running probes end on their own timer.
        public override void Disable()
        {
        }
    }
}
