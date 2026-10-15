using System;
using CustomPlayerEffects;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace Examples.AddingPlayerAbilities
{
    /// <summary>Returns true when the effect was applied. On false, <paramref name="refusal"/> is the hint text.</summary>
    internal delegate bool AbilityApply(Player player, AbilitiesConfig config, out string refusal);

    /// <summary>
    /// One keybind-triggered ability. The list is data so adding an ability is one entry, not a new
    /// handler: the single handler in the plugin looks the ability up by setting id.
    /// </summary>
    internal sealed class Ability
    {
        public readonly int Index;
        public readonly int SettingId;
        public readonly string Label;
        public readonly KeyCode DefaultKey;
        public readonly Func<AbilitiesConfig, bool> IsOffered;
        public readonly Func<AbilitiesConfig, double> CooldownSeconds;
        public readonly Func<AbilitiesConfig, string> Describe;
        public readonly AbilityApply Apply;

        public Ability(
            int index,
            string key,
            string label,
            KeyCode defaultKey,
            Func<AbilitiesConfig, bool> isOffered,
            Func<AbilitiesConfig, double> cooldownSeconds,
            Func<AbilitiesConfig, string> describe,
            AbilityApply apply)
        {
            Index = index;
            SettingId = AbilityIds.Make("abilities", key);
            Label = label;
            DefaultKey = defaultKey;
            IsOffered = isOffered;
            CooldownSeconds = cooldownSeconds;
            Describe = describe;
            Apply = apply;
        }
    }

    internal static class AbilityIds
    {
        /// <summary>
        /// FNV-1a over "plugin:key". SettingId is one int shared by every plugin on the server, and the
        /// client saves each player's key binding under it. A hash we own never changes between builds
        /// (string.GetHashCode makes no such promise) and cannot land on the small enum ids other
        /// plugins use. Released ids must never change, or players lose their bindings.
        /// </summary>
        public static int Make(string plugin, string key)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in plugin + ":" + key)
                {
                    hash ^= c;
                    hash *= 16777619;
                }

                return (int)hash;
            }
        }
    }

    internal static class AbilityList
    {
        public static readonly Ability[] All =
        {
            new Ability(
                index: 0,
                key: "second_wind",
                label: "Second Wind",
                defaultKey: KeyCode.H,
                isOffered: c => c.SecondWind.Enabled,
                cooldownSeconds: c => c.SecondWind.CooldownSeconds,
                describe: c => "Heals " + Math.Max(0f, c.SecondWind.HealAmount).ToString("0") + " HP. Cooldown " + c.SecondWind.CooldownSeconds.ToString("0") + " s.",
                apply: SecondWind),

            new Ability(
                index: 1,
                key: "speed_burst",
                label: "Speed Burst",
                defaultKey: KeyCode.G,
                isOffered: c => c.SpeedBurst.Enabled,
                cooldownSeconds: c => c.SpeedBurst.CooldownSeconds,
                describe: c => "Move " + c.SpeedBurst.SpeedPercent + "% faster for " + c.SpeedBurst.DurationSeconds.ToString("0.#") + " s. Cooldown " + c.SpeedBurst.CooldownSeconds.ToString("0") + " s.",
                apply: SpeedBurst),
        };

        private static bool SecondWind(Player player, AbilitiesConfig config, out string refusal)
        {
            // Refuse BEFORE the cooldown starts: a press at full health must not cost the player 45 seconds.
            if (player.Health >= player.MaxHealth)
            {
                refusal = "Already at full health";
                return false;
            }

            player.Heal(Math.Max(0f, config.SecondWind.HealAmount));
            refusal = null;
            return true;
        }

        private static bool SpeedBurst(Player player, AbilitiesConfig config, out string refusal)
        {
            // Config values are owner input: clamp to what the effect accepts instead of trusting them.
            int percent = Math.Min(Math.Max(config.SpeedBurst.SpeedPercent, 1), 255);
            float seconds = Math.Min(Math.Max(config.SpeedBurst.DurationSeconds, 0.5f), 60f);
            player.EnableEffect<MovementBoost>((byte)percent, seconds);
            refusal = null;
            return true;
        }
    }
}
