using System;
using LabApi.Events.Handlers;
using LabApi.Loader.Features.Plugins;
using SlKit;

namespace Examples.LabapiEventsSnippets
{
    /// <summary>
    /// Composition root for the event snippets. It shows the lifecycle rules the wiring classes rely on:
    /// <list type="number">
    /// <item>fallible setup first, subscriptions last, undo and rethrow if anything throws;</item>
    /// <item>Enable is idempotent (a second call adds no second set of handlers);</item>
    /// <item>LabAPI and the game never call <c>Disable()</c>, so the real release happens in
    /// <c>ServerEvents.Shutdown</c> as well.</item>
    /// </list>
    /// Style A (static events) and style B (<c>CustomEventsHandler</c>) are both wired here only so one
    /// project can show both. A real feature picks one style per handler group.
    /// </summary>
    public sealed class EventsSnippetsPlugin : Plugin
    {
        private readonly CancellationRules _rules = new CancellationRules();
        private readonly VoiceActivityGate _voice = new VoiceActivityGate();
        private readonly PreAuthBridge _preAuth = new PreAuthBridge();

        private StaticEventWiring _static;
        private CustomHandlerWiring _custom;
        private bool _enabled;

        public override string Name => "LabapiEventsSnippets";
        public override string Description => "Examples: subscription styles, gated hot handler, cancellation, off-thread events.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            if (_enabled)
                return;

            try
            {
                // 1. Fallible setup. A throw here leaves nothing subscribed.
                SlClock.Install();
                _static = new StaticEventWiring(_rules, _voice);
                _custom = new CustomHandlerWiring(new RoundNotesHandler(_preAuth));
                _preAuth.Start();

                // 2. Subscriptions, last.
                _static.Subscribe();
                _custom.Register();
                _preAuth.Subscribe();
                ServerEvents.Shutdown += OnShutdown;
                _enabled = true;
            }
            catch
            {
                Release(); // undo, then rethrow: swallowing would make the loader print "enabled" for a dead plugin
                throw;
            }
        }

        public override void Disable() => Release(); // never called by LabAPI 1.1.7; Shutdown does the real work

        // Normally the main thread. A legacy central-server restart action can raise Shutdown from the
        // server-list thread, so Release only removes subscriptions and disposes jobs (a flag): it never
        // reads or writes the per-round collections or any Unity object.
        private void OnShutdown() => Release();

        private void Release()
        {
            ServerEvents.Shutdown -= OnShutdown;

            if (_static != null)
                _static.Unsubscribe();
            if (_custom != null)
                _custom.Unregister();

            _preAuth.Release();
            _voice.Detach();
            _enabled = false;
        }
    }
}
