using System;
using System.Collections.Generic;
using Hints;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using SlKit;
using SlKit.Core;
using UserSettings.ServerSpecific;
using Logger = LabApi.Features.Console.Logger;

namespace Examples.AddingPlayerAbilities
{
    /// <summary>
    /// Keybind abilities through Server-Specific Settings. One handler serves every ability; each press runs
    /// the same guard chain: ownership, press edge, rate limit, permission, role, cooldown, effect.
    ///
    /// Cost at 60 players: idle it does nothing. A key event is one dictionary lookup, one bucket take and
    /// one cooldown compare. The only periodic work is ONE SlClock job every 500 ms that walks a list of
    /// "ready again" notices (at most players x abilities entries, usually empty). No coroutine per player.
    /// </summary>
    public sealed class AbilitiesPlugin : Plugin<AbilitiesConfig>
    {
        // Short on purpose: these hints share one client slot with every other plugin and with the game.
        private const float UsedHintSeconds = 2f;
        private const float ReadyHintSeconds = 3f;
        private const double DeniedHintGapSeconds = 1.5;

        private readonly AbilitySettings _settings = new AbilitySettings();
        private readonly Dictionary<int, Ability> _byId = new Dictionary<int, Ability>(4);
        private readonly AbilityCooldowns _cooldowns = new AbilityCooldowns(AbilityList.All.Length);
        private readonly List<ReadyNotice> _notices = new List<ReadyNotice>(32);
        private IDisposable _readyJob;
        private bool _active;

        public override string Name => "AbilitiesExample";
        public override string Description => "Template: keybind abilities via Server-Specific Settings.";
        public override string Author => "scpsl-skills";
        public override Version Version => new Version(1, 0, 0);

        // A constant, so a LabAPI update cannot silently make the version check vacuous.
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        private struct ReadyNotice
        {
            public string UserId;
            public int Ability;
            public long At;
        }

        public override void Enable()
        {
            // Config is null when config.yml is empty or broken; IsEnabled is the owner's kill switch.
            if (Config == null || !Config.IsEnabled || _active)
                return;

            // Fallible work first, subscriptions last: a throw after subscribing would leave live handlers
            // with no rollback.
            SlClock.Install();
            if (!_settings.Publish(Config, _byId))
                return;

            if (ServerSpecificSettingsSync.SendOnJoinFilter != null)
            {
                // Someone else decides who gets a pack on join, so joiners may not receive ours.
                Logger.Warn("Abilities: SendOnJoinFilter is set by another plugin; joiners may not receive the ability settings.");
            }

            _readyJob = SlClock.Every(TimeSpan.FromMilliseconds(500), FlushReadyNotices, "abilities.ready");
            ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnSettingValue;
            PlayerEvents.Left += OnLeft;
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.Shutdown += OnShutdown;
            _active = true;

            try
            {
                AbilitySettings.SendToConnected();
            }
            catch (Exception e)
            {
                Logger.Warn("Abilities: initial send failed: " + e.Message);
            }
        }

        // LabAPI and the game never call Disable; cleanup lives in Shutdown. Kept so a host that does call it is safe.
        public override void Disable() => Cleanup();

        private void OnShutdown() => Cleanup();

        private void OnWaitingForPlayers()
        {
            // A round restart reloads the scene: every hub is new. Cooldowns are per round by design.
            _cooldowns.Clear();
            _notices.Clear();
        }

        private void OnLeft(PlayerLeftEventArgs ev)
        {
            try
            {
                string userId = ev.Player?.UserId;
                if (!string.IsNullOrEmpty(userId))
                    _cooldowns.DropIfIdle(userId, StopwatchTime.Now);
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("abilities.left", () => "Left handler failed: " + e);
            }
        }

        // SSS raises this for EVERY control of EVERY plugin, on the main thread. A throw here would make Mirror
        // disconnect the player who pressed the key, so the whole body is guarded.
        private void OnSettingValue(ReferenceHub hub, ServerSpecificSettingBase setting)
        {
            try
            {
                AbilitiesConfig config = Config;
                if (config == null || !config.IsEnabled)
                    return;

                // 1. Ownership: ignore ids and types that are not ours.
                if (!(setting is SSKeybindSetting key) || !_byId.TryGetValue(key.SettingId, out Ability ability))
                    return;

                // 2. A keybind sends on press AND on release. Abilities fire on the press edge only.
                if (!key.SyncIsPressed)
                    return;

                Player player = Player.Get(hub);
                if (player == null || player.IsHost || player.IsDummy || player.IsDestroyed)
                    return;

                long now = StopwatchTime.Now;
                PlayerAbilityState state = _cooldowns.GetOrCreate(player.UserId, config.InputBurst, config.InputPerSecond, now);

                // 3. The server never limits SSS traffic; a modified client can send thousands per second.
                if (!state.Input.TryTake(now))
                    return;

                TryUse(player, ability, state, config, now);
            }
            catch (Exception e)
            {
                RateLimitedLog.Error("abilities.handler", () => "Ability handler failed: " + e);
            }
        }

        private void TryUse(Player player, Ability ability, PlayerAbilityState state, AbilitiesConfig config, long now)
        {
            if (!ability.IsOffered(config))
                return;

            // 4. Authorise here, not by "only staff were sent the entry": the server accepts a response from
            //    any player for any defined id.
            if (!string.IsNullOrEmpty(config.RequiredPermission) && !player.HasPermissions(config.RequiredPermission))
            {
                Deny(player, state, now, config, "No permission");
                return;
            }

            // 5. Role. allowSpectatorTrigger:false is a client-side hint; the server check is IsAlive.
            if (!player.IsAlive || (player.IsSCP && !config.AllowScps))
            {
                Deny(player, state, now, config, "Not available in this role");
                return;
            }

            // 6. Cooldown, on the wall clock.
            TimeSpan left = AbilityCooldowns.Remaining(state, ability.Index, now);
            if (left > TimeSpan.Zero)
            {
                DenyCooldown(player, ability, state, left, now, config);
                return;
            }

            // 7. Effect. The cooldown starts only when the effect really happened.
            if (!ability.Apply(player, config, out string refusal))
            {
                Deny(player, state, now, config, refusal);
                return;
            }

            long readyAt = AbilityCooldowns.Start(state, ability.Index, now, ability.CooldownSeconds(config));
            _notices.Add(new ReadyNotice { UserId = player.UserId, Ability = ability.Index, At = readyAt });

            if (config.ShowHints)
                player.SendHint(ability.Label + " used", UsedHintSeconds);
        }

        private static void Deny(Player player, PlayerAbilityState state, long now, AbilitiesConfig config, string text)
        {
            if (!config.ShowHints || now < state.NextDeniedHintAt)
                return;

            state.NextDeniedHintAt = now + StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(DeniedHintGapSeconds));
            player.SendHint(text, 2f);
        }

        private static void DenyCooldown(Player player, Ability ability, PlayerAbilityState state, TimeSpan left, long now, AbilitiesConfig config)
        {
            if (!config.ShowHints || now < state.NextDeniedHintAt)
                return;

            state.NextDeniedHintAt = now + StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(DeniedHintGapSeconds));

            // The client counts the number down itself (TimespanHintParameter), so one hint replaces a hint per
            // second. Duration must not exceed the countdown, or the display runs past zero.
            HintParameter[] countdown = { TimespanHintParameter.FromOffset(left.TotalSeconds, @"m\:ss", false) };
            float seconds = (float)Math.Min(left.TotalSeconds, 2.0);
            player.SendHint(ability.Label + " ready in {0}", countdown, null, seconds);
        }

        /// <summary>The one periodic job: tell players when an ability is usable again.</summary>
        private void FlushReadyNotices()
        {
            if (_notices.Count == 0)
                return;

            long now = StopwatchTime.Now;
            bool showHints = Config != null && Config.ShowHints;

            // Backwards, so swap-removal never skips an entry.
            for (int i = _notices.Count - 1; i >= 0; i--)
            {
                ReadyNotice notice = _notices[i];
                if (notice.At > now)
                    continue;

                int last = _notices.Count - 1;
                _notices[i] = _notices[last];
                _notices.RemoveAt(last);

                if (!showHints)
                    continue;

                // UserId lookup, not a stored Player: the player may have left or the scene may have reloaded.
                Player player = Player.Get(notice.UserId);
                if (player == null || player.IsDestroyed || !player.IsAlive)
                    continue;

                Ability ability = AbilityList.All[notice.Ability];

                // The client resolves the key name from the player's own binding.
                HintParameter[] key = { new SSKeybindHintParameter(ability.SettingId) };
                player.SendHint(ability.Label + " ready {0}", key, null, ReadyHintSeconds);
            }
        }

        private void Cleanup()
        {
            if (!_active)
                return;

            ServerSpecificSettingsSync.ServerOnSettingValueReceived -= OnSettingValue;
            PlayerEvents.Left -= OnLeft;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.Shutdown -= OnShutdown;
            _readyJob?.Dispose();
            _readyJob = null;
            _settings.Withdraw();
            _cooldowns.Clear();
            _notices.Clear();
            _byId.Clear();
            _active = false;
        }
    }
}
