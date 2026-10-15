using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;
using static SlKit.Clock.Tests.ClockHarness;

namespace SlKit.Clock.Tests;

/// <summary>Failure handling is per job; the shared clock and MainThread have no setting that one plugin can change for the others.</summary>
internal static class PerJobSettingsTests
{
    internal static void Register()
    {
        // Nothing one plugin can change for another
        Run(nameof(Options_ThresholdIsPerJob), Options_ThresholdIsPerJob);
        Run(nameof(Options_PolicyAndDelaysArePerJobAndCopied), Options_PolicyAndDelaysArePerJobAndCopied);
        Run(nameof(Options_InvalidValuesThrowAtRegistration), Options_InvalidValuesThrowAtRegistration);
        Run(nameof(Shared_NoPublicStaticSetterIsLeft), Shared_NoPublicStaticSetterIsLeft);
        Run(nameof(Shared_BudgetIsTheOwnersValueReadOnce), Shared_BudgetIsTheOwnersValueReadOnce);
        Run(nameof(Shared_NoPublicMethodCancelsEveryPluginsRoundJobs), Shared_NoPublicMethodCancelsEveryPluginsRoundJobs);
        Run(nameof(Reenable_OnlyTheRegisteringAssemblyBringsAJobBack), Reenable_OnlyTheRegisteringAssemblyBringsAJobBack);
    }

    // ---------------------------------------------------------------------------------------
    // Failure handling is per job; the shared clock has no setting one plugin can change.
    // ---------------------------------------------------------------------------------------

    private static void Options_ThresholdIsPerJob()
    {
        int strictRuns = 0;
        int defaultRuns = 0;
        ClockJobHandle strict = SlClock.Every(
            TimeSpan.FromMilliseconds(1),
            () => { strictRuns++; throw new InvalidOperationException("plugin A, failing"); },
            "pluginA.strict",
            options: new ClockJobOptions { MaxConsecutiveFailures = 1, FailurePolicy = JobFailurePolicy.DisablePermanently });
        ClockJobHandle lenient = SlClock.Every(
            TimeSpan.FromMilliseconds(1),
            () => { defaultRuns++; throw new InvalidOperationException("plugin B, transient"); },
            "pluginB.default");

        Equal(1, strict.MaxConsecutiveFailures, "plugin A's threshold");
        Equal(ClockJobOptions.DefaultMaxConsecutiveFailures, lenient.MaxConsecutiveFailures, "plugin B keeps the default");

        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(3);
            NextFrame();
        }

        True(strict.IsDisabled, "plugin A's job is off after its single failure");
        Equal(1, strictRuns, "plugin A's job ran once");
        False(lenient.IsDisabled, "plugin B's job is unaffected by plugin A's threshold after " + defaultRuns + " failures");
        Equal(4, defaultRuns, "plugin B's job kept running");

        Thread.Sleep(3);
        NextFrame();
        True(lenient.IsDisabled, "plugin B's job is off after its own 5th failure");
        strict.Dispose();
        lenient.Dispose();
        NextFrame();
    }

    private static void Options_PolicyAndDelaysArePerJobAndCopied()
    {
        ClockJobOptions options = new ClockJobOptions
        {
            MaxConsecutiveFailures = 1,
            ReenableBaseDelay = TimeSpan.FromSeconds(2),
            ReenableMaxDelay = TimeSpan.FromSeconds(8),
        };
        ClockJobHandle quick = SlClock.Every(TimeSpan.FromMilliseconds(1), () => throw new InvalidOperationException("x"), "pluginA.quick", options: options);

        // Changing the object afterwards must not reach the registered job, nor a later one.
        options.ReenableBaseDelay = TimeSpan.FromHours(1);
        options.MaxConsecutiveFailures = 99;
        ClockJobHandle defaults = SlClock.Every(TimeSpan.FromMilliseconds(1), () => throw new InvalidOperationException("y"), "pluginB.defaults", options: new ClockJobOptions { MaxConsecutiveFailures = 1 });

        Equal(TimeSpan.FromSeconds(2), quick.ReenableBaseDelay, "registered delay is a copy");
        Equal(TimeSpan.FromSeconds(8), quick.ReenableMaxDelay, "registered max delay");
        Equal(1, quick.MaxConsecutiveFailures, "registered threshold is a copy");
        Equal(ClockJobOptions.DefaultReenableBaseDelay, defaults.ReenableBaseDelay, "another job keeps the default delay");
        Equal(JobFailurePolicy.Backoff, defaults.FailurePolicy, "default policy");

        Thread.Sleep(3);
        NextFrame();
        ClockJobStats quickStats = SlClock.Stats.First(s => s.Name == "pluginA.quick");
        ClockJobStats defaultStats = SlClock.Stats.First(s => s.Name == "pluginB.defaults");
        True(quickStats.IsDisabled && quickStats.ReenableIn.HasValue && quickStats.ReenableIn.Value <= TimeSpan.FromSeconds(2),
            "plugin A's job comes back within its own 2 s: " + quickStats);
        True(defaultStats.IsDisabled && defaultStats.ReenableIn.HasValue && defaultStats.ReenableIn.Value > TimeSpan.FromSeconds(20),
            "plugin B's job waits the default 30 s: " + defaultStats);

        quick.Dispose();
        defaults.Dispose();
        NextFrame();
    }

    private static void Options_InvalidValuesThrowAtRegistration()
    {
        int before = SlClock.JobCount;
        Throws<ArgumentOutOfRangeException>(() => SlClock.Every(TimeSpan.FromSeconds(1), () => { }, "bad", options: new ClockJobOptions { MaxConsecutiveFailures = 0 }), "threshold 0");
        Throws<ArgumentOutOfRangeException>(() => SlClock.After(TimeSpan.FromSeconds(1), () => { }, "bad", options: new ClockJobOptions { ReenableBaseDelay = TimeSpan.FromMilliseconds(10) }), "base delay below 1 s");
        Throws<ArgumentOutOfRangeException>(() => SlClock.EveryStaggered<int>(TimeSpan.FromSeconds(1), () => new int[0], _ => { }, "bad", options: new ClockJobOptions { ReenableMaxDelay = TimeSpan.FromSeconds(5) }), "max below base");
        Throws<ArgumentOutOfRangeException>(() => SlClock.Every(TimeSpan.FromSeconds(1), () => { }, "bad", options: new ClockJobOptions { FailurePolicy = (JobFailurePolicy)7 }), "undefined policy");
        Equal(before, SlClock.JobCount, "nothing was registered");
    }

    private static void Shared_NoPublicStaticSetterIsLeft()
    {
        // The clock, its queue and its jobs are shared by every plugin on the server, so a public
        // static setter on them would let one plugin change behaviour for all the others.
        foreach (Type type in new[] { typeof(SlClock), typeof(MainThread) })
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                True(property.SetMethod == null || !property.SetMethod.IsPublic, type.Name + "." + property.Name + " has a public setter");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                True(field.IsLiteral || field.IsInitOnly, type.Name + "." + field.Name + " is a writable public field");
            }
        }
    }

    private static void Shared_BudgetIsTheOwnersValueReadOnce()
    {
        Equal(TimeSpan.FromMilliseconds(2.5), MainThread.Budget, "budget from " + HostBudget.VariableName);

        // A plugin changing the variable at run time changes nothing: it was read once.
        Environment.SetEnvironmentVariable(HostBudget.VariableName, "40");
        Equal(TimeSpan.FromMilliseconds(2.5), MainThread.Budget, "still the owner's value");
    }

    private static void Shared_NoPublicMethodCancelsEveryPluginsRoundJobs()
    {
        // OnRoundRestart cancels the Round jobs of ALL plugins; only the LabAPI event may run it.
        True(typeof(SlClock).GetMethod("OnRoundRestart", BindingFlags.Public | BindingFlags.Static) == null, "SlClock.OnRoundRestart is public");
        True(typeof(SlClock).GetMethod("OnRoundRestart", BindingFlags.NonPublic | BindingFlags.Static) != null, "the event handler still exists");
    }

    private static void Reenable_OnlyTheRegisteringAssemblyBringsAJobBack()
    {
        ClockJobHandle mine = SlClock.Every(
            TimeSpan.FromMilliseconds(1),
            () => throw new InvalidOperationException("failing on purpose"),
            "k1.reenable",
            options: new ClockJobOptions { MaxConsecutiveFailures = 1, FailurePolicy = JobFailurePolicy.DisablePermanently });
        Thread.Sleep(3);
        NextFrame();
        True(mine.IsDisabled, "the job is off after its failure");

        Equal(0, OtherPlugin_Reenable("k1.reenable"), "another assembly cannot bring it back");
        True(mine.IsDisabled, "and it is still off");
        Equal(0, SlClock.Reenable("k1.other-name"), "an unknown name matches nothing");

        Equal(1, SlClock.Reenable("k1.reenable"), "the registering assembly can");
        False(mine.IsDisabled, "now it is on");
        mine.Dispose();
        NextFrame();
    }

    /// <summary>Calls SlClock.Reenable from a separately emitted assembly, as another plugin would.</summary>
    private static int OtherPlugin_Reenable(string name)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OtherPlugin"), AssemblyBuilderAccess.Run);
        TypeBuilder type = assembly.DefineDynamicModule("OtherPlugin").DefineType("OtherPlugin.Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder method = type.DefineMethod("Reenable", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(string) });

        // Not inlined into the reflection stub, so Reenable's caller frame is this assembly.
        method.SetImplementationFlags(MethodImplAttributes.NoInlining);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(SlClock).GetMethod(nameof(SlClock.Reenable)));
        il.Emit(OpCodes.Ret);
        MethodInfo reenable = type.CreateType().GetMethod("Reenable");
        return (int)reenable.Invoke(null, new object[] { name });
    }
}
