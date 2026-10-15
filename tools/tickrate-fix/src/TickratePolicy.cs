namespace TickrateFix
{
    /// <summary>Why the plugin chose the value it assigned (or did not assign one).</summary>
    internal enum TickrateReason
    {
        /// <summary><c>server_tickrate</c> is between 1 and the cap: re-applied as configured.</summary>
        Configured = 0,

        /// <summary><c>server_tickrate</c> is -1 or 0 (unlimited): the cap is applied instead.</summary>
        UnlimitedReplacedByCap = 1,

        /// <summary><c>server_tickrate</c> is above the cap: re-applied as configured, never lowered; warned once.</summary>
        AboveCapKept = 2,
    }

    /// <summary>What to assign to <c>Server.MaxTps</c>, and why.</summary>
    internal readonly struct TickrateDecision
    {
        internal TickrateDecision(int target, int cap, TickrateReason reason)
        {
            Target = target;
            Cap = cap;
            Reason = reason;
        }

        /// <summary>The frame-rate target to assign.</summary>
        internal int Target { get; }

        /// <summary>The cap after <see cref="TickratePolicy.EffectiveCap"/>.</summary>
        internal int Cap { get; }

        internal TickrateReason Reason { get; }

        /// <summary>Short token for the log line's <c>reason=</c> field, stable for grep.</summary>
        internal string ReasonToken => TickratePolicy.Token(Reason);
    }

    /// <summary>
    /// The one rule of this plugin, without any game reference so it can be tested on plain .NET:
    /// make the owner's <c>server_tickrate</c> take effect, and never lower it.
    /// </summary>
    /// <remarks>
    /// Why "never lower": a <c>server_tickrate</c> above the cap is an explicit choice by the server
    /// owner. Replacing it would make this plugin a second, hidden tickrate setting. The cap therefore
    /// only replaces -1 and 0, which mean "unlimited" and would let the frame loop spin as fast as the
    /// CPU allows. Thread affinity: pure, any thread.
    /// </remarks>
    internal static class TickratePolicy
    {
        /// <summary>Default cap: the rate Mirror already sets, and the one the game's position distributor clamps to.</summary>
        internal const int DefaultCap = 60;

        /// <summary>The cap to use: <paramref name="cap"/> when it is 1 to 32767, otherwise <see cref="DefaultCap"/>.</summary>
        /// <param name="cap">The configured cap.</param>
        /// <returns>A usable cap.</returns>
        internal static int EffectiveCap(int cap) => cap < 1 || cap > short.MaxValue ? DefaultCap : cap;

        /// <summary>Decides what to assign for a configured tickrate.</summary>
        /// <param name="configured">The value of <c>server_tickrate</c> (what <c>Server.MaxTps</c> returns).</param>
        /// <param name="cap">The configured cap; invalid values mean <see cref="DefaultCap"/>.</param>
        /// <returns>The value to assign and the reason.</returns>
        internal static TickrateDecision Decide(int configured, int cap)
        {
            cap = EffectiveCap(cap);
            if (configured < 1)
                return new TickrateDecision(cap, cap, TickrateReason.UnlimitedReplacedByCap);

            if (configured > cap)
                return new TickrateDecision(configured, cap, TickrateReason.AboveCapKept);

            return new TickrateDecision(configured, cap, TickrateReason.Configured);
        }

        /// <summary>Log token for a reason.</summary>
        /// <param name="reason">The reason.</param>
        /// <returns>A lowercase token without spaces.</returns>
        internal static string Token(TickrateReason reason)
        {
            switch (reason)
            {
                case TickrateReason.UnlimitedReplacedByCap:
                    return "unlimited-replaced-by-cap";
                case TickrateReason.AboveCapKept:
                    return "above-cap-kept";
                default:
                    return "configured";
            }
        }

        /// <summary>
        /// The measurement line, one per <c>WaitingForPlayers</c>. LabAPI's logger prefixes it with the
        /// assembly tag, so on the console it reads <c>[TickrateFix] targetFrameRate before=60 after=20
        /// reason=configured ...</c>, which is what to grep for.
        /// </summary>
        /// <param name="before"><c>Application.targetFrameRate</c> before the plugin acted.</param>
        /// <param name="after"><c>Application.targetFrameRate</c> afterwards (equal to before when nothing was assigned).</param>
        /// <param name="reason">A reason token.</param>
        /// <param name="configured">The configured <c>server_tickrate</c>, or null when it could not be read.</param>
        /// <param name="cap">The effective cap.</param>
        /// <returns>The line.</returns>
        internal static string MeasurementLine(int before, int after, string reason, int? configured, int cap)
        {
            // Invariant culture: the line is parsed by scripts, whatever the server's locale.
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "targetFrameRate before={0} after={1} reason={2} server_tickrate={3} cap={4}",
                before,
                after,
                reason,
                configured.HasValue ? configured.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?",
                cap);
        }
    }
}
