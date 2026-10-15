using System;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of the per-job ClockJobOptions and the owner's main-thread budget (HostBudget).</summary>
internal static class JobOptionsTests
{
    internal static void Register()
    {
        // Per-job clock options and the owner's main-thread budget (nothing shared is settable)
        Run(nameof(JobOptions_NullGivesTheDocumentedDefaults), JobOptions_NullGivesTheDocumentedDefaults);
        Run(nameof(JobOptions_ValidatesAndCopies), JobOptions_ValidatesAndCopies);
        Run(nameof(HostBudget_ParsesTheOwnersValue), HostBudget_ParsesTheOwnersValue);
        Run(nameof(HostBudget_UnsetOrInvalidFallsBackToDefault), HostBudget_UnsetOrInvalidFallsBackToDefault);
    }

    private static void JobOptions_NullGivesTheDocumentedDefaults()
    {
        JobFailureSettings d = JobFailureSettings.From(null);
        Equal(5, d.MaxConsecutiveFailures, "threshold");
        Equal((int)JobFailurePolicy.Backoff, (int)d.Policy, "policy");
        Equal(StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(30)), d.BaseDelayTicks, "base delay");
        Equal(StopwatchTime.FromTimeSpan(TimeSpan.FromMinutes(30)), d.MaxDelayTicks, "max delay");

        // A fresh options object means the same as no options.
        JobFailureSettings fresh = JobFailureSettings.From(new ClockJobOptions());
        Equal(d.MaxConsecutiveFailures, fresh.MaxConsecutiveFailures);
        Equal(d.BaseDelayTicks, fresh.BaseDelayTicks);
        Equal(d.MaxDelayTicks, fresh.MaxDelayTicks);
    }

    private static void JobOptions_ValidatesAndCopies()
    {
        ClockJobOptions o = new ClockJobOptions
        {
            MaxConsecutiveFailures = 2,
            FailurePolicy = JobFailurePolicy.DisablePermanently,
            ReenableBaseDelay = TimeSpan.FromSeconds(1),
            ReenableMaxDelay = TimeSpan.FromSeconds(1),
        };
        JobFailureSettings s = JobFailureSettings.From(o);
        o.MaxConsecutiveFailures = 50;
        Equal(2, s.MaxConsecutiveFailures, "a copy, not a reference to the options");
        Equal((int)JobFailurePolicy.DisablePermanently, (int)s.Policy);
        Equal(s.BaseDelayTicks, s.MaxDelayTicks, "max equal to base is allowed");

        Throws<ArgumentOutOfRangeException>(() => JobFailureSettings.From(new ClockJobOptions { MaxConsecutiveFailures = 0 }), "threshold 0");
        Throws<ArgumentOutOfRangeException>(() => JobFailureSettings.From(new ClockJobOptions { MaxConsecutiveFailures = -3 }), "negative threshold");
        Throws<ArgumentOutOfRangeException>(() => JobFailureSettings.From(new ClockJobOptions { ReenableBaseDelay = TimeSpan.FromMilliseconds(999) }), "base below 1 s");
        Throws<ArgumentOutOfRangeException>(() => JobFailureSettings.From(new ClockJobOptions { ReenableMaxDelay = TimeSpan.FromSeconds(29) }), "max below the default base");
        Throws<ArgumentOutOfRangeException>(() => JobFailureSettings.From(new ClockJobOptions { FailurePolicy = (JobFailurePolicy)2 }), "undefined policy");
    }

    private static void HostBudget_ParsesTheOwnersValue()
    {
        True(HostBudget.TryParse("2.5", out double ms, out string problem), "2.5 accepted");
        Near(2.5, ms);
        True(problem == null, "no problem");
        True(HostBudget.TryParse(" 0.05 ", out ms, out _), "lower bound, trimmed");
        True(HostBudget.TryParse("50", out ms, out _), "upper bound");
        True(HostBudget.TryParse("1e0", out ms, out _), "exponent form");
        Near(1.0, ms);
    }

    private static void HostBudget_UnsetOrInvalidFallsBackToDefault()
    {
        False(HostBudget.TryParse(null, out double ms, out string problem), "unset");
        Near(HostBudget.DefaultMilliseconds, ms);
        True(problem == null, "unset is not a problem");
        False(HostBudget.TryParse("   ", out ms, out problem), "blank");
        True(problem == null, "blank is not a problem");

        foreach (string bad in new[] { "abc", "2,5", "NaN", "Infinity", "0", "0.01", "-1", "50.5", "1000" })
        {
            False(HostBudget.TryParse(bad, out ms, out problem), "'" + bad + "' rejected");
            Near(HostBudget.DefaultMilliseconds, ms, 1e-12, "'" + bad + "' gives the default");
            True(problem != null && problem.Contains(HostBudget.VariableName), "'" + bad + "' explains itself: " + problem);
        }
    }
}
