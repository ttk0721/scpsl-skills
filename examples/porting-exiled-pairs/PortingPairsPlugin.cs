using System;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 1 (plugin shell and event subscription). The EXILED original:
    /// <code>
    /// public sealed class Plugin : Plugin&lt;Config&gt;
    /// {
    ///     public override void OnEnabled()  { Handlers.Player.Hurting += OnHurting; base.OnEnabled(); }
    ///     public override void OnDisabled() { Handlers.Player.Hurting -= OnHurting; base.OnDisabled(); }
    /// }
    /// </code>
    /// What changes: Enable/Disable instead of OnEnabled/OnDisabled, constant metadata, a static event class
    /// (PlayerEvents, ServerEvents, ...) instead of Handlers.*, and above all Disable() is NEVER called by
    /// LabAPI or the game. Cleanup that must survive a server stop goes on ServerEvents.Shutdown.
    /// </summary>
    public sealed class PortingPairsPlugin : Plugin<PairsConfig>
    {
        private JoinTracker _joins;
        private Messaging _messaging;
        private PeriodicJobs _jobs;
        private DamageRules _damage;
        private LoadoutRule _loadout;
        private bool _enabled;

        public override string Name => "PortingPairs";
        public override string Description => "EXILED to LabAPI porting pairs, LabAPI side.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        internal static PortingPairsPlugin Instance { get; private set; }

        /// <summary>Translation strings, never null.</summary>
        internal PairsTranslation Text { get; private set; } = new PairsTranslation();

        /// <summary>A config.yml that is empty or comment-only deserialises to null: never trust Config directly.</summary>
        internal PairsConfig Cfg => Config ?? (Config = new PairsConfig());

        /// <summary>
        /// Pair 7. LabAPI calls this before Enable and again on "labapi reload configs", and it REPLACES the
        /// Config object: caches derived from it must be rebuilt here or read through <see cref="Cfg"/> at use time.
        /// </summary>
        public override void LoadConfigs()
        {
            base.LoadConfigs();
            Cfg.Normalize();

            // No translation concept in LabAPI: a second file, loaded the same way.
            PairsTranslation text;
            Text = this.TryLoadConfig("translation.yml", out text) && text != null ? text : new PairsTranslation();
        }

        public override void Enable()
        {
            if (_enabled) return;   // idempotent: a host that re-enables must not double-subscribe
            if (!Cfg.IsEnabled)
            {
                Logger.Info("PortingPairs: disabled by config.");
                return;   // Instance stays null, so the command reports "disabled"
            }

            Instance = this;

            try
            {
                // Fallible work first, subscriptions last, so a throw cannot leave live handlers behind.
                _messaging = new Messaging(() => Text);
                _joins = new JoinTracker(_messaging);
                _jobs = new PeriodicJobs(() => Cfg, _messaging, _joins);
                _damage = new DamageRules(() => Cfg);
                _loadout = new LoadoutRule(() => Cfg);

                _joins.Start();
                _jobs.Start();
                _damage.Start();
                _loadout.Start();
                ServerEvents.Shutdown += OnShutdown;
                _enabled = true;
            }
            catch
            {
                Teardown();
                throw;   // let the loader report the failure
            }
        }

        // Neither LabAPI nor the game calls this. Kept correct for hosts that do; the real exit is Shutdown.
        public override void Disable() => Teardown();

        private void OnShutdown() => Teardown();

        private void Teardown()
        {
            ServerEvents.Shutdown -= OnShutdown;
            _loadout?.Stop();
            _damage?.Stop();
            _jobs?.Stop();
            _joins?.Stop();
            _loadout = null;
            _damage = null;
            _jobs = null;
            _joins = null;
            _messaging = null;
            if (ReferenceEquals(Instance, this)) Instance = null;
            _enabled = false;
        }
    }
}
