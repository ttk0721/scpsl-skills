using System;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;

namespace Examples.AddingPlayerHud
{
    /// <summary>
    /// A stats HUD for every player: a round clock the client ticks by itself plus kills and
    /// deaths, with a per-player show/hide switch. Shows the whole recipe: one hint owner,
    /// change-driven sends with a keep-alive, staggered visits, a one-shot re-assert after spawn
    /// bursts for servers where other plugins also write hints, SSS toggle, clean lifecycle.
    /// </summary>
    public sealed class AddingPlayerHudPlugin : Plugin<HudConfig>
    {
        private HudService _hud;
        private HudToggle _toggle;
        private bool _enabled;

        // Constants: Name picks the config folder, so it must never change at runtime.
        public override string Name => "AddingPlayerHud";
        public override string Description => "Stats HUD: one hint owner, change-driven, per-player toggle.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        // config.yml that is empty or broken deserialises to null.
        private HudConfig Cfg => Config ?? (Config = new HudConfig());

        public override void LoadConfigs()
        {
            base.LoadConfigs();
            Cfg.Normalize();   // runs again on "labapi reload configs"; the HUD reads Cfg at use time
        }

        public override void Enable()
        {
            if (_enabled) return;                 // idempotent
            Cfg.Normalize();
            if (!Cfg.IsEnabled)
            {
                Logger.Info("AddingPlayerHud: disabled by config.");
                return;
            }

            try
            {
                // Fallible work first, subscriptions last, so a throw cannot leave live handlers behind.
                _hud = new HudService(() => Cfg);
                _hud.Start();
                _toggle = new HudToggle(_hud);
                if (Cfg.OfferToggle) _toggle.Install();

                ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
                ServerEvents.RoundStarted += OnRoundStarted;
                ServerEvents.Shutdown += OnShutdown;
                PlayerEvents.Death += OnDeath;      // per-lifecycle in the catalog, not hot
                PlayerEvents.Left += OnLeft;
                PlayerEvents.ChangedRole += OnChangedRole;   // arms the re-assert only, sends nothing
                _enabled = true;
                LogOtherPlugins();
            }
            catch
            {
                Teardown();
                throw;                            // let the loader report the failure
            }
        }

        // Neither LabAPI nor the game calls this. Kept correct for reloads; the real exit is Shutdown.
        public override void Disable() => Teardown();

        private void OnWaitingForPlayers() => _hud?.OnWaitingForPlayers();   // scene reloaded: drop round state
        private void OnRoundStarted() => _hud?.OnRoundStarted();
        private void OnShutdown() => Teardown();

        private void OnDeath(LabApi.Events.Arguments.PlayerEvents.PlayerDeathEventArgs ev)
        {
            try { _hud?.OnDeath(ev); }
            catch (Exception e) { Logger.Error("AddingPlayerHud: death handler failed: " + e); }
        }

        private void OnLeft(LabApi.Events.Arguments.PlayerEvents.PlayerLeftEventArgs ev)
        {
            try { _hud?.OnLeft(ev); }
            catch (Exception e) { Logger.Error("AddingPlayerHud: left handler failed: " + e); }
        }

        private void OnChangedRole(LabApi.Events.Arguments.PlayerEvents.PlayerChangedRoleEventArgs ev)
        {
            try { _hud?.OnRoleChanged(); }
            catch (Exception e) { Logger.Error("AddingPlayerHud: role handler failed: " + e); }
        }

        // LabAPI has no event for hints, so another plugin's hint writes cannot be observed. What
        // can be read is the list of loaded plugins (all are loaded before any is enabled): a hint
        // for the server owner to check the names against "who writes hints". Names only; it cannot
        // say which of them send hints, and EXILED plugins may not appear in it.
        private void LogOtherPlugins()
        {
            if (!Cfg.ReassertAfterSpawn) return;
            try
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var plugin in PluginLoader.Plugins.Keys)
                    if (!ReferenceEquals(plugin, this)) names.Add(plugin.Name);
                if (names.Count > 0)
                    Logger.Info("AddingPlayerHud: other plugins loaded: " + string.Join(", ", names)
                        + ". Assuming some of them write hints; re-asserting the HUD after spawns (ReassertAfterSpawn).");
            }
            catch (Exception e) { Logger.Warn("AddingPlayerHud: plugin list unavailable: " + e.Message); }
        }

        private void Teardown()
        {
            PlayerEvents.ChangedRole -= OnChangedRole;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.Shutdown -= OnShutdown;
            PlayerEvents.Death -= OnDeath;
            PlayerEvents.Left -= OnLeft;
            _toggle?.Uninstall();
            _hud?.Stop();
            _toggle = null;
            _hud = null;
            _enabled = false;
        }
    }
}
