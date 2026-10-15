using System.Globalization;

namespace SlKit.Core;

/// <summary>
/// Parses the server owner's main-thread budget for <c>MainThread.Post</c> callbacks, read once
/// from the environment variable <see cref="VariableName"/>.
/// </summary>
/// <remarks>
/// Why an environment variable and not a property: the budget caps how much of each server frame
/// the posted callbacks of ALL plugins together may use. It is a property of the server, like its
/// frame rate, so only the person who starts the server (start script, systemd unit, container
/// definition) sets it; no plugin can change it for the others through SlKit's API.
/// Thread affinity: pure and stateless.
/// </remarks>
public static class HostBudget
{
    /// <summary>The environment variable: milliseconds per frame, invariant culture, e.g. <c>2</c> or <c>0.5</c>.</summary>
    public const string VariableName = "SLKIT_MAIN_THREAD_BUDGET_MS";

    /// <summary>Budget used when the variable is unset or invalid: 1 ms per frame.</summary>
    public const double DefaultMilliseconds = 1.0;

    /// <summary>Smallest accepted value, 0.05 ms. At least one callback runs per frame whatever the budget.</summary>
    public const double MinimumMilliseconds = 0.05;

    /// <summary>Largest accepted value, 50 ms: one whole frame at 20 TPS. More would let callbacks starve the game.</summary>
    public const double MaximumMilliseconds = 50.0;

    /// <summary>
    /// Interprets the raw variable value. Unset or blank gives the default with no problem; anything
    /// unparsable or out of range gives the default and a problem description for one warning.
    /// </summary>
    /// <param name="raw">The variable's value, or null when unset.</param>
    /// <param name="milliseconds">The budget to use.</param>
    /// <param name="problem">Why <paramref name="raw"/> was ignored, or null.</param>
    /// <returns>True when <paramref name="raw"/> was set and accepted.</returns>
    public static bool TryParse(string raw, out double milliseconds, out string problem)
    {
        milliseconds = DefaultMilliseconds;
        problem = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            problem = VariableName + "='" + raw + "' is not a number (use invariant culture, e.g. 2 or 0.5)";
            return false;
        }

        if (value < MinimumMilliseconds || value > MaximumMilliseconds)
        {
            problem = VariableName + "=" + value.ToString(CultureInfo.InvariantCulture) + " is outside "
                + MinimumMilliseconds.ToString(CultureInfo.InvariantCulture) + ".."
                + MaximumMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
            return false;
        }

        milliseconds = value;
        return true;
    }
}
