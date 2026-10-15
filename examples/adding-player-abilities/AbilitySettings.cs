using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using SlKit;
using UserSettings.ServerSpecific;
using Logger = LabApi.Features.Console.Logger;

namespace Examples.AddingPlayerAbilities
{
    /// <summary>
    /// Puts this plugin's entries into the shared, global ServerSpecificSettingsSync.DefinedSettings
    /// array without destroying anyone else's.
    ///
    /// DefinedSettings is ONE static array for every plugin (last assignment wins), so this class only ever
    /// APPENDS to what is there and removes exactly the instances it added itself. It never touches
    /// SendOnJoinFilter or Version: both are single static slots owned by whoever runs the server's SSS setup.
    /// If the server runs a dedicated SSS registry plugin, register these entries through it instead and
    /// delete this class (see scpsl-server-specific-settings, sss-registry-pattern.md).
    /// </summary>
    internal sealed class AbilitySettings
    {
        private const int MaxEntriesPerPack = 255;   // the pack count is one byte and wraps silently

        private readonly List<ServerSpecificSettingBase> _mine = new List<ServerSpecificSettingBase>(8);

        /// <summary>
        /// Builds the entries and merges them in. Returns false and changes nothing when an id collides with
        /// an entry we do not own (or the pack would overflow): better no abilities than somebody else's
        /// setting silently merged with ours.
        /// </summary>
        public bool Publish(AbilitiesConfig config, Dictionary<int, Ability> byId)
        {
            ServerSpecificSettingBase[] existing = ServerSpecificSettingsSync.DefinedSettings ?? new ServerSpecificSettingBase[0];
            var merged = new List<ServerSpecificSettingBase>(existing.Length + 4);

            // A hot reload runs Enable() again: drop what the previous run added, keep everything else.
            foreach (ServerSpecificSettingBase entry in existing)
                if (!_mine.Contains(entry))
                    merged.Add(entry);

            var fresh = new List<ServerSpecificSettingBase>(4);
            var ids = new Dictionary<int, Ability>(4);

            int headerId = AbilityIds.Make("abilities", "header");
            fresh.Add(new SSGroupHeader(headerId, "Abilities"));

            foreach (Ability ability in AbilityList.All)
            {
                if (!ability.IsOffered(config))
                    continue;

                // allowSpectatorTrigger defaults to true: a spectator's keypress would reach the handler.
                // That is a client-side hint only, so the handler also checks IsAlive.
                fresh.Add(new SSKeybindSetting(
                    ability.SettingId,
                    ability.Label,
                    ability.DefaultKey,
                    preventInteractionOnGui: true,
                    allowSpectatorTrigger: false,
                    hint: ability.Describe(config)));
                ids[ability.SettingId] = ability;
            }

            foreach (ServerSpecificSettingBase entry in fresh)
            {
                foreach (ServerSpecificSettingBase other in merged)
                {
                    // The server's lookup key is (id, exact type), not the id alone.
                    if (other.SettingId == entry.SettingId && other.GetType() == entry.GetType())
                    {
                        Logger.Error("Abilities: SSS id " + entry.SettingId + " (" + entry.Label + ") collides with '" + other.Label + "'. Not registering.");
                        return false;
                    }
                }
            }

            if (merged.Count + fresh.Count > MaxEntriesPerPack)
            {
                Logger.Error("Abilities: DefinedSettings would exceed " + MaxEntriesPerPack + " entries. Not registering.");
                return false;
            }

            merged.AddRange(fresh);
            _mine.Clear();
            _mine.AddRange(fresh);

            byId.Clear();
            foreach (KeyValuePair<int, Ability> pair in ids)
                byId[pair.Key] = pair.Value;

            ServerSpecificSettingsSync.DefinedSettings = merged.ToArray();
            return true;
        }

        /// <summary>
        /// Players connected BEFORE the assignment are not served by the join hook (late enable, hot reload).
        /// Send to ready players only: IsReady means authenticated AND nickname set, and the host and dummies
        /// have no client to receive a pack.
        /// </summary>
        public static void SendToConnected()
        {
            foreach (Player player in Player.ReadyList)
            {
                if (player.IsHost || player.IsDummy)
                    continue;

                try
                {
                    ServerSpecificSettingsSync.SendToPlayer(player.ReferenceHub);
                }
                catch (Exception e)
                {
                    // One bad connection must not stop the rest of the list, and a systemic failure must not
                    // cost a stack-walking log line per player.
                    RateLimitedLog.Warn("abilities.send", () => "could not send settings to a player: " + e.Message);
                }
            }
        }

        /// <summary>Removes exactly the entries this class added. Idempotent.</summary>
        public void Withdraw()
        {
            if (_mine.Count == 0)
                return;

            ServerSpecificSettingBase[] existing = ServerSpecificSettingsSync.DefinedSettings;
            if (existing != null)
            {
                var kept = new List<ServerSpecificSettingBase>(existing.Length);
                foreach (ServerSpecificSettingBase entry in existing)
                    if (!_mine.Contains(entry))
                        kept.Add(entry);

                ServerSpecificSettingsSync.DefinedSettings = kept.Count == 0 ? null : kept.ToArray();
            }

            _mine.Clear();
        }
    }
}
