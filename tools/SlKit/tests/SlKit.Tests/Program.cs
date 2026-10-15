using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>
/// Self-contained tests for the pure logic in SlKit.Core, run by the plain-console runner in
/// Common/TestKit.cs instead of a test framework, which keeps the toolkit free of NuGet
/// dependencies. Each topic has its own class in this folder that registers its tests in
/// <c>Register</c>; this file runs the classes in a fixed order.
/// Exit code 0 means every test passed.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        DeadlineTimerTests.Register();
        StaggerPlannerTests.Register();
        PercentilesTests.Register();
        RingBufferTests.Register();
        TokenBucketTests.Register();
        QueueStatsAndTimeTests.Register();
        IntervalGateTests.Register();
        FailureBackoffTests.Register();
        JobOptionsTests.Register();
        BoundedReadTests.Register();
        AdmissionLimitTests.Register();
        CopyReportTests.Register();
        OwnerRegistryTests.Register();
        ConnectionLimitTests.Register();
        FrameCsvLineTests.Register();

        return Finish();
    }
}
