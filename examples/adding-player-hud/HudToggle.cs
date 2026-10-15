using System;
using LabApi.Features.Wrappers;
using UserSettings.ServerSpecific;
using Log = LabApi.Features.Console.Logger;   // UnityEngine also defines a Logger type

namespace Examples.AddingPlayerHud
{
    /// <summary>
    /// The player's own show/hide switch, as one SSS two-button entry. A server with several SSS
    /// plugins should register through its shared registry (see scpsl-server-specific-settings);
    /// this class only APPENDS to DefinedSettings and never replaces it, so it cannot erase
    /// another plugin's entries.
    /// </summary>
    public sealed class HudToggle
    {
        // FNV-1a over "plugin:key". A hash we own: stable across runs, unlike string.GetHashCode(),
        // and namespaced, so it cannot meet another plugin's small enum ids.
        private static readonly int SettingId = Fnv("adding-player-hud:toggle");

        private readonly HudService _hud;
        private SSTwoButtonsSetting _entry;
        private bool _subscribed;

        public HudToggle(HudService hud) => _hud = hud;

        public void Install()
        {
            if (_entry != null) return;

            ServerSpecificSettingBase[] current = ServerSpecificSettingsSync.DefinedSettings
                                                  ?? new ServerSpecificSettingBase[0];
            foreach (ServerSpecificSettingBase s in current)
                if (s.SettingId == SettingId && s is SSTwoButtonsSetting)
                {
                    Log.Warn("adding-player-hud: SSS id already registered, toggle not offered.");
                    return;
                }
            if (current.Length >= 255)    // the pack header is one byte; more entries wrap silently
            {
                Log.Warn("adding-player-hud: 255 SSS entries already defined, toggle not offered.");
                return;
            }

            _entry = new SSTwoButtonsSetting(SettingId, "Stats HUD", "Show", "Hide",
                defaultIsB: false, hint: "Round time, kills and deaths on your screen.");

            var merged = new ServerSpecificSettingBase[current.Length + 1];
            Array.Copy(current, merged, current.Length);
            merged[current.Length] = _entry;
            ServerSpecificSettingsSync.DefinedSettings = merged;

            ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnValue;
            _subscribed = true;

            // Players already connected (a late enable) are not served by the join hook.
            foreach (Player p in Player.ReadyList)
                if (!p.IsDummy) ServerSpecificSettingsSync.SendToPlayer(p.ReferenceHub);
        }

        public void Uninstall()
        {
            if (_subscribed) ServerSpecificSettingsSync.ServerOnSettingValueReceived -= OnValue;
            _subscribed = false;
            if (_entry == null) return;

            ServerSpecificSettingBase[] current = ServerSpecificSettingsSync.DefinedSettings;
            if (current != null)
            {
                var kept = new System.Collections.Generic.List<ServerSpecificSettingBase>(current.Length);
                foreach (ServerSpecificSettingBase s in current)
                    if (!ReferenceEquals(s, _entry)) kept.Add(s);
                ServerSpecificSettingsSync.DefinedSettings = kept.ToArray();
            }
            _entry = null;
        }

        // Main thread. The event is global (every plugin's settings arrive here) and an exception
        // that escapes makes Mirror disconnect the player who touched the control.
        private void OnValue(ReferenceHub hub, ServerSpecificSettingBase setting)
        {
            try
            {
                if (setting.SettingId != SettingId || !(setting is SSTwoButtonsSetting two)) return;   // not ours
                Player p = Player.Get(hub);
                if (p == null || p.IsDummy) return;
                // Own HUD only, so there is nothing to authorise. The value is client data, but a bool needs no clamp.
                // Before the client's first message this is simply "shown", the default.
                _hud.SetHidden(p.UserId, two.SyncIsB);
            }
            catch (Exception e)
            {
                Log.Error("adding-player-hud: SSS handler failed: " + e);
            }
        }

        private static int Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) { h ^= c; h *= 16777619; }
                return (int)h;
            }
        }
    }
}
