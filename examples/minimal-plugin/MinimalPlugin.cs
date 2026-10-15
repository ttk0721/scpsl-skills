using System;
using System.ComponentModel;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;

namespace Examples.MinimalPlugin
{
    /// <summary>
    /// Config: public get/set properties only (fields are ignored), defaults as initializers,
    /// [Description] becomes the YAML comment. The file is rewritten on every load.
    /// </summary>
    public sealed class MinimalConfig
    {
        [Description("Turns the whole plugin off without removing it (kill switch).")]
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// The smallest correct LabAPI plugin: constant metadata, idempotent Enable, cleanup on
    /// Shutdown (LabAPI and the game never call Disable), per-round reset at WaitingForPlayers.
    /// </summary>
    public sealed class MinimalPlugin : Plugin<MinimalConfig>
    {
        private bool _subscribed;

        public override string Name => "MinimalPlugin";
        public override string Description => "Template: lifecycle done right.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);

        // A constant: LabApiProperties.CompiledVersion is computed at runtime from the running
        // LabApi.dll, so using it here can never fail the version check.
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            // Config is null when config.yml is empty or broken.
            if (Config == null || !Config.IsEnabled || _subscribed)
                return;

            // Fallible work first, subscriptions last: a throw after subscribing would leave
            // handlers live with no rollback.
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.Shutdown += OnShutdown;
            _subscribed = true;
        }

        public override void Disable() => Unsubscribe();

        private void OnWaitingForPlayers()
        {
            // A round restart reloads the scene: every Player/ReferenceHub from the last round is
            // dead. Rebuild per-round state here.
        }

        private void OnShutdown()
        {
            // The only reliable last call: flush synchronously, then release.
            Logger.Info("MinimalPlugin: shutting down.");
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            _subscribed = false;
        }
    }
}
