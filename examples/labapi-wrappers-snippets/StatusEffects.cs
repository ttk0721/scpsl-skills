using CustomPlayerEffects;
using LabApi.Features.Wrappers;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operation 9: status effects. Use the generic overloads: EnableEffect&lt;T&gt; and GetEffect&lt;T&gt;
    /// are a dictionary lookup by type followed by the effect's own state change, while
    /// TryGetEffect(string name) is a linear scan.
    /// </summary>
    internal static class StatusEffects
    {
        /// <summary>
        /// Duration 0 means the effect never expires on its own: you must DisableEffect it. Pass a
        /// positive duration for anything temporary. addDuration: true stacks onto a running timer
        /// instead of replacing it. What an intensity means differs per effect, so read the
        /// effect's own documentation before choosing a number.
        /// </summary>
        public static bool Slow(Player player, byte intensity, float seconds, bool stack = false)
        {
            if (player == null || player.IsDestroyed || !player.IsAlive)
                return false;
            player.EnableEffect<Slowness>(intensity, seconds, stack);
            return true;
        }

        /// <summary>Reads an effect's state without enabling it. GetEffect/TryGetEffect never create one.</summary>
        public static bool IsSlowed(Player player, out byte intensity)
        {
            intensity = 0;
            if (player == null || player.IsDestroyed)
                return false;
            if (!player.TryGetEffect(out Slowness slowness) || !slowness.IsEnabled)
                return false;
            intensity = slowness.Intensity;
            return true;
        }

        /// <summary>Removes the effect. Safe when it is not active.</summary>
        public static void Unslow(Player player)
        {
            if (player == null || player.IsDestroyed)
                return;
            player.DisableEffect<Slowness>();
        }
    }
}
