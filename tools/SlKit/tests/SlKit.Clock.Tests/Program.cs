using System;
using System.Threading;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Clock.Tests;

/// <summary>
/// Runs the real SlClock and MainThread against the stand-ins in Stubs.cs. The clock is static
/// state shared by the whole process (as on a server), so the tests run in a fixed order: the
/// thread checks first, before anything has captured the main thread. Each topic has its own
/// class in this folder that registers its tests in <c>Register</c>; this file sets up the
/// owner's environment and runs the classes in that order.
/// Exit code 0 means every test passed.
/// </summary>
internal static class Program
{
    private const string OwnerBudget = "2.5";

    private static int Main()
    {
        // The server owner's environment, set before SlKit is first used (as a start script would).
        Environment.SetEnvironmentVariable(HostBudget.VariableName, OwnerBudget);

        ClockHarness.MainThreadId = Thread.CurrentThread.ManagedThreadId;
        UnityEngine.Awaitable.MainThreadId = ClockHarness.MainThreadId;

        ThreadChecksTests.Register();
        InstallAndRoundTests.Register();
        PerJobSettingsTests.Register();
        ClockWorkTests.Register();

        return Finish();
    }
}
