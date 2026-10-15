using System;
using SlKit.Core;

namespace SlKit;

/// <summary>The owner's main-thread budget, read once from the environment. See <see cref="MainThread.Budget"/>.</summary>
internal readonly struct MainThreadBudgetSetting
{
    private MainThreadBudgetSetting(double milliseconds, bool fromEnvironment, string problem)
    {
        Milliseconds = milliseconds;
        Ticks = Math.Max(1, StopwatchTime.FromMilliseconds(milliseconds));
        FromEnvironment = fromEnvironment;
        Problem = problem;
    }

    internal double Milliseconds { get; }

    internal long Ticks { get; }

    internal bool FromEnvironment { get; }

    internal string Problem { get; }

    internal static MainThreadBudgetSetting Read()
    {
        string raw;
        try
        {
            raw = Environment.GetEnvironmentVariable(HostBudget.VariableName);
        }
        catch (Exception e)
        {
            return new MainThreadBudgetSetting(HostBudget.DefaultMilliseconds, false, "could not read " + HostBudget.VariableName + ": " + e.Message);
        }

        bool accepted = HostBudget.TryParse(raw, out double milliseconds, out string problem);
        return new MainThreadBudgetSetting(milliseconds, accepted, problem);
    }
}
