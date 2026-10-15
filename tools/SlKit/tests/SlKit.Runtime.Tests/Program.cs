using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Runtime.Tests;

/// <summary>
/// Behaviour tests for SlHttpClient (against a real loopback server) and Background.ForOwner
/// (called from a second, emitted assembly). Exit code 0 means
/// every test passed. Timings use generous margins: they check which phase a wait landed in,
/// not how fast the machine is.
/// </summary>
internal static partial class Program
{
    private static LocalHttpServer _server;

    private static int Main()
    {
        using (_server = new LocalHttpServer())
        {
            // Queue wait and request timeout are separate phases
            Run(nameof(Http_QueueTimeoutIsReportedAsNotSentNotAsTimedOut), Http_QueueTimeoutIsReportedAsNotSentNotAsTimedOut);
            Run(nameof(Http_RequestTimeoutCountsFromTheSlotNotFromTheCall), Http_RequestTimeoutCountsFromTheSlotNotFromTheCall);
            Run(nameof(Http_SlowEndpointIsTimedOutAndSent), Http_SlowEndpointIsTimedOutAndSent);
            Run(nameof(Http_ZeroQueueTimeoutNeverWaits), Http_ZeroQueueTimeoutNeverWaits);
            Run(nameof(Http_CancelWhileQueuedIsNotSentAndNotATimeout), Http_CancelWhileQueuedIsNotSentAndNotATimeout);
            Run(nameof(Http_RejectedIsNotSent), Http_RejectedIsNotSent);
            Run(nameof(Http_QueueTimeoutValidation), Http_QueueTimeoutValidation);

            // Per-host connection limit of the shared transport
            Run(nameof(Http_TransportAddsNoPerHostLimitAndLogsItOnce), Http_TransportAddsNoPerHostLimitAndLogsItOnce);
            Run(nameof(Http_FourRequestsToOneHostRunAtOnce), Http_FourRequestsToOneHostRunAtOnce);
        }

        // Workers keyed by name and calling assembly
        Run(nameof(ForOwner_SameAssemblySameNameIsTheSameWorker), ForOwner_SameAssemblySameNameIsTheSameWorker);
        Run(nameof(ForOwner_SameNameFromAnotherAssemblyGetsItsOwnWorkerAndAWarning), ForOwner_SameNameFromAnotherAssemblyGetsItsOwnWorkerAndAWarning);
        Run(nameof(ForOwner_DefaultNameIsReserved), ForOwner_DefaultNameIsReserved);
        Run(nameof(ForOwner_AnIgnoredCapacityIsReportedOncePerWorker), ForOwner_AnIgnoredCapacityIsReportedOncePerWorker);

        // Nothing shared can be changed by one plugin for the others
        Run(nameof(Shared_NoPublicStaticSetterIsLeft), Shared_NoPublicStaticSetterIsLeft);
        Run(nameof(Background_CapacityIsReadOnlyAndOwnersSizeTheirOwn), Background_CapacityIsReadOnlyAndOwnersSizeTheirOwn);
        Run(nameof(Log_SharedTableHasFixedDefaults), Log_SharedTableHasFixedDefaults);
        Run(nameof(Log_SharedTableAllowsTheBurstThenSuppressesAndSummarises), Log_SharedTableAllowsTheBurstThenSuppressesAndSummarises);
        Run(nameof(Log_OwnLoggerHasItsOwnLimitsAndKeys), Log_OwnLoggerHasItsOwnLimitsAndKeys);
        Run(nameof(Log_ResetOfOneLoggerLeavesOthersAlone), Log_ResetOfOneLoggerLeavesOthersAlone);
        Run(nameof(Log_SuppressedCallsSkipTheFactoryAndAllocateNothing), Log_SuppressedCallsSkipTheFactoryAndAllocateNothing);
        Run(nameof(Log_ConcurrentCallsNeverExceedTheBurst), Log_ConcurrentCallsNeverExceedTheBurst);
        Run(nameof(Log_KeyTableIsBoundedPerLogger), Log_KeyTableIsBoundedPerLogger);
        Run(nameof(Log_FactoryThatThrowsIsLoggedNotThrown), Log_FactoryThatThrowsIsLoggedNotThrown);
        Run(nameof(Log_ConstructorRejectsInvalidLimitsInsteadOfClamping), Log_ConstructorRejectsInvalidLimitsInsteadOfClamping);
        Run(nameof(Log_PeriodicSummaryRegistersOneJobPerLogger), Log_PeriodicSummaryRegistersOneJobPerLogger);

        return Finish();
    }

    // ---------------------------------------------------------------------------------------
    // Queue wait and request timeout are separate phases. A request that times out while QUEUED
    // was never sent, so it must not report TimedOut, and the server must have seen 0 requests
    // for its path.
    // ---------------------------------------------------------------------------------------

    private static void Http_QueueTimeoutIsReportedAsNotSentNotAsTimedOut()
    {
        SlHttpClient client = SlHttp.CreateClient("n3-queue", new SlHttpOptions
        {
            MaxConcurrency = 1,
            Timeout = TimeSpan.FromSeconds(5),
            QueueTimeout = TimeSpan.FromMilliseconds(200),
        });

        Task<HttpResult> busy = client.GetStringAsync(_server.Url("/slow/1500"));
        WaitUntil(() => client.InFlightCount == 1, "first request holds the only slot");
        HttpResult queued = client.GetStringAsync(_server.Url("/n3/queued")).Result;

        True(queued.QueueTimedOut, "queued request: QueueTimedOut");
        False(queued.TimedOut, "queued request: not TimedOut (the endpoint was never involved)");
        False(queued.Sent, "queued request: not sent");
        Equal(0, queued.StatusCode);
        Equal(0, _server.Arrived("/n3/queued"), "the server never saw it");
        True(queued.QueueTime.TotalMilliseconds >= 150, "waited about the queue timeout, got " + queued.QueueTime.TotalMilliseconds);
        True(queued.RequestTime == TimeSpan.Zero, "no time on the wire");
        True(queued.Error is TimeoutException, "error describes the queue: " + queued.Error);
        True(queued.ToString().StartsWith("not sent"), "log text says not sent: " + queued);
        Equal(1L, client.QueueTimedOutCount);

        HttpResult first = busy.Result;
        True(first.Success, "the running request is unaffected: " + first);
        True(first.Sent, "and was sent");
        True(first.QueueTime.TotalMilliseconds < 100, "it got a slot at once");
    }

    private static void Http_RequestTimeoutCountsFromTheSlotNotFromTheCall()
    {
        // Two 300 ms requests through one slot with a 450 ms request timeout. Counted from the call
        // (the old rule) the second one would need 600 ms and time out; counted from its slot it
        // needs 300 ms and succeeds.
        SlHttpClient client = SlHttp.CreateClient("n3-slot", new SlHttpOptions
        {
            MaxConcurrency = 1,
            Timeout = TimeSpan.FromMilliseconds(450),
            QueueTimeout = Timeout.InfiniteTimeSpan,
        });

        Task<HttpResult> a = client.GetStringAsync(_server.Url("/slow/300"));
        Task<HttpResult> b = client.GetStringAsync(_server.Url("/slow/300"));
        HttpResult first = a.Result;
        HttpResult second = b.Result;
        HttpResult queued = first.QueueTime > second.QueueTime ? first : second;

        True(first.Success && second.Success, "both succeed: " + first + " / " + second);
        True(queued.QueueTime.TotalMilliseconds >= 200, "one of them waited for the slot: " + queued.QueueTime.TotalMilliseconds);
        True(queued.Elapsed.TotalMilliseconds > 450, "and took longer than the request timeout in total: " + queued.Elapsed.TotalMilliseconds);
        True(queued.RequestTime.TotalMilliseconds < 450, "but its time on the wire was within it: " + queued.RequestTime.TotalMilliseconds);
    }

    private static void Http_SlowEndpointIsTimedOutAndSent()
    {
        SlHttpClient client = SlHttp.CreateClient("n3-slow", new SlHttpOptions { Timeout = TimeSpan.FromMilliseconds(200) });
        HttpResult result = client.GetStringAsync(_server.Url("/slow/2000")).Result;

        True(result.TimedOut, "TimedOut: " + result);
        False(result.QueueTimedOut, "not a queue timeout");
        True(result.Sent, "sent");
        Equal(1, _server.Arrived("/slow/2000"), "the server saw it");
        True(result.RequestTime.TotalMilliseconds >= 150, "about the timeout on the wire: " + result.RequestTime.TotalMilliseconds);
        True(result.ToString().Contains("on the wire"), "log text: " + result);
    }

    private static void Http_ZeroQueueTimeoutNeverWaits()
    {
        SlHttpClient client = SlHttp.CreateClient("n3-zero", new SlHttpOptions { MaxConcurrency = 1, QueueTimeout = TimeSpan.Zero });
        Task<HttpResult> busy = client.GetStringAsync(_server.Url("/slow/500"));
        WaitUntil(() => client.InFlightCount == 1, "slot taken");

        Stopwatch watch = Stopwatch.StartNew();
        HttpResult refused = client.GetStringAsync(_server.Url("/n3/zero")).Result;
        True(refused.QueueTimedOut && !refused.Sent, "no free slot ends it at once: " + refused);
        True(watch.ElapsedMilliseconds < 250, "without waiting: " + watch.ElapsedMilliseconds + " ms");
        Equal(0, _server.Arrived("/n3/zero"));
        True(busy.Result.Success, "the running one completes");

        // With a free slot, zero queue timeout sends normally.
        True(client.GetStringAsync(_server.Url("/n3/zero-free")).Result.Success, "free slot: sent");
    }

    private static void Http_CancelWhileQueuedIsNotSentAndNotATimeout()
    {
        SlHttpClient client = SlHttp.CreateClient("n3-cancel", new SlHttpOptions { MaxConcurrency = 1, QueueTimeout = Timeout.InfiniteTimeSpan });
        Task<HttpResult> busy = client.GetStringAsync(_server.Url("/slow/800"));
        WaitUntil(() => client.InFlightCount == 1, "slot taken");

        using (CancellationTokenSource cancel = new CancellationTokenSource(150))
        {
            HttpResult result = client.GetStringAsync(_server.Url("/n3/cancel"), cancellationToken: cancel.Token).Result;
            True(result.Error is OperationCanceledException, "cancel is an error result: " + result.Error);
            False(result.TimedOut || result.QueueTimedOut, "a cancel is neither kind of timeout");
            False(result.Sent, "not sent");
            Equal(0, _server.Arrived("/n3/cancel"));
        }

        True(busy.Result.Success, "the running one completes");
    }

    private static void Http_RejectedIsNotSent()
    {
        SlHttpClient client = SlHttp.CreateClient("n3-reject", new SlHttpOptions { MaxConcurrency = 1, MaxQueued = 0 });
        Task<HttpResult> busy = client.GetStringAsync(_server.Url("/slow/300"));
        WaitUntil(() => client.InFlightCount == 1, "slot taken");
        HttpResult rejected = client.GetStringAsync(_server.Url("/n3/rejected")).Result;
        True(rejected.Rejected && !rejected.Sent && !rejected.QueueTimedOut && !rejected.TimedOut, "rejected only: " + rejected);
        True(busy.Result.Success);
    }

    private static void Http_QueueTimeoutValidation()
    {
        Throws<ArgumentOutOfRangeException>(() => SlHttp.CreateClient("bad", new SlHttpOptions { QueueTimeout = TimeSpan.FromMilliseconds(-5) }), "negative queue timeout");
        Equal(Timeout.InfiniteTimeSpan, SlHttp.CreateClient("inf", new SlHttpOptions { QueueTimeout = Timeout.InfiniteTimeSpan }).QueueTimeout);
        Equal(TimeSpan.Zero, SlHttp.CreateClient("zero", new SlHttpOptions { QueueTimeout = TimeSpan.Zero }).QueueTimeout);
        Equal(SlHttpOptions.DefaultQueueTimeout, SlHttp.Default.QueueTimeout, "default client has the default");
        Equal(TimeSpan.FromDays(30), SlHttp.CreateClient("huge", new SlHttpOptions { QueueTimeout = TimeSpan.FromDays(30) }).QueueTimeout, "huge value accepted (clamped internally)");
    }

    // ---------------------------------------------------------------------------------------
    // The transport must add no per-host limit below what the clients allow, and say once
    // what applies. On this runtime (and on the game's, by its decompiled handler) the handler
    // default is already unlimited; these tests pin that SlKit keeps it so and logs it once.
    // ---------------------------------------------------------------------------------------

    private static void Http_TransportAddsNoPerHostLimitAndLogsItOnce()
    {
        SlHttp.CreateClient("n4-a");
        SlHttp.CreateClient("n4-b");
        Equal(int.MaxValue, SlHttp.TransportConnectionLimit, "no per-host limit on the managed handler");
        True(SlHttp.TransportDescription.Contains("unlimited"), "description: " + SlHttp.TransportDescription);

        int lines = 0;
        foreach (string line in SlLog.Take())
        {
            if (line.Contains("SlHttp transport"))
            {
                lines++;
            }
        }

        Equal(1, lines, "logged exactly once for the process");
    }

    private static void Http_FourRequestsToOneHostRunAtOnce()
    {
        // With a hidden 2-per-host limit, four 600 ms requests would take about 1200 ms.
        SlHttpClient client = SlHttp.CreateClient("n4-parallel", new SlHttpOptions { MaxConcurrency = 4 });
        Stopwatch watch = Stopwatch.StartNew();
        Task<HttpResult>[] all = new Task<HttpResult>[4];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = client.GetStringAsync(_server.Url("/slow/600"));
        }

        Task.WaitAll(all);
        foreach (Task<HttpResult> t in all)
        {
            True(t.Result.Success, "each succeeds: " + t.Result);
        }

        True(watch.ElapsedMilliseconds < 1100, "all four ran at once: " + watch.ElapsedMilliseconds + " ms");
    }

    // ---------------------------------------------------------------------------------------
    // Workers are keyed by name AND calling assembly: two plugins passing the same name must not
    // share one worker (the second gets its own and a warning), and ForOwner("default") must not
    // hand out the shared default worker.
    // ---------------------------------------------------------------------------------------

    private static Func<string, int, BackgroundWorker> _otherPlugin;

    private static void ForOwner_SameAssemblySameNameIsTheSameWorker()
    {
        BackgroundWorker first = Background.ForOwner("n5-same", 8);
        BackgroundWorker again = Background.ForOwner("n5-same", 8);
        True(ReferenceEquals(first, again), "same assembly, same name: one worker, no leaked thread");
        Equal("SlKit.Runtime.Tests", first.Owner);
        Equal("SlKit.Background.n5-same", first.ThreadName, "first claimant keeps the plain thread name");
    }

    private static void ForOwner_AnIgnoredCapacityIsReportedOncePerWorker()
    {
        BackgroundWorker worker = Background.ForOwner("n5-late", 8);
        True(worker.TryRun(() => { }), "the first use starts the thread");
        SlLog.Take();

        for (int i = 0; i < 3; i++)
        {
            True(ReferenceEquals(worker, Background.ForOwner("n5-late", 16)), "still the same worker");
        }

        List<string> logged = SlLog.Take();
        Equal(1, logged.Count, "one warning, not one per call: " + string.Join(" | ", logged));
        True(logged[0].Contains("capacity is ignored"), logged[0]);
        Equal(8, worker.Capacity, "the started worker keeps its size");
    }

    private static void ForOwner_SameNameFromAnotherAssemblyGetsItsOwnWorkerAndAWarning()
    {
        SlLog.Take();
        BackgroundWorker mine = Background.ForOwner("Shared", 8);
        BackgroundWorker theirs = OtherPlugin("Shared", 8);

        False(ReferenceEquals(mine, theirs), "another assembly gets its own worker");
        Equal("OtherPlugin", theirs.Owner);
        Equal("SlKit.Background.Shared@OtherPlugin", theirs.ThreadName, "thread name tells them apart");

        List<string> logged = SlLog.Take();
        Equal(1, logged.Count, "one warning: " + string.Join(" | ", logged));
        True(logged[0].StartsWith("WARN") && logged[0].Contains("OtherPlugin") && logged[0].Contains("SlKit.Runtime.Tests"), "names both assemblies: " + logged[0]);

        True(ReferenceEquals(theirs, OtherPlugin("Shared", 8)), "the other assembly's repeat call finds its worker");
        True(ReferenceEquals(mine, Background.ForOwner("Shared", 8)), "and so does ours");
        Equal(0, SlLog.Take().Count, "no repeated warning");

        // Separate threads, really: each runs its own item.
        string mineThread = null;
        string theirsThread = null;
        using (CountdownEvent done = new CountdownEvent(2))
        {
            mine.TryRun(() => { mineThread = Thread.CurrentThread.Name; done.Signal(); });
            theirs.TryRun(() => { theirsThread = Thread.CurrentThread.Name; done.Signal(); });
            True(done.Wait(5000), "both workers ran their item");
        }

        Equal("SlKit.Background.Shared", mineThread);
        Equal("SlKit.Background.Shared@OtherPlugin", theirsThread);

        IReadOnlyList<BackgroundWorker> workers = Background.Workers;
        True(ReferenceEquals(Background.Default, workers[0]), "default listed first");
        True(Contains(workers, mine) && Contains(workers, theirs), "both listed");
    }

    private static void ForOwner_DefaultNameIsReserved()
    {
        Throws<ArgumentException>(() => Background.ForOwner("default"), "\"default\"");
        Throws<ArgumentException>(() => Background.ForOwner("Default"), "\"Default\"");
        Throws<ArgumentException>(() => OtherPlugin("default", 8), "also from another assembly");
        Throws<ArgumentException>(() => Background.ForOwner(""), "empty");
        Equal("default", Background.Default.Name);
    }

    /// <summary>Calls Background.ForOwner from a separately emitted assembly, as another plugin would.</summary>
    private static BackgroundWorker OtherPlugin(string name, int capacity)
    {
        if (_otherPlugin == null)
        {
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OtherPlugin"), AssemblyBuilderAccess.Run);
            TypeBuilder type = assembly.DefineDynamicModule("OtherPlugin").DefineType("OtherPlugin.Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            MethodBuilder method = type.DefineMethod("Get", MethodAttributes.Public | MethodAttributes.Static, typeof(BackgroundWorker), new[] { typeof(string), typeof(int) });

            // Not inlined into the reflection stub, so ForOwner's caller frame is this assembly.
            method.SetImplementationFlags(MethodImplAttributes.NoInlining);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, typeof(Background).GetMethod(nameof(Background.ForOwner)));
            il.Emit(OpCodes.Ret);
            MethodInfo get = type.CreateType().GetMethod("Get");
            _otherPlugin = (n, c) =>
            {
                try
                {
                    return (BackgroundWorker)get.Invoke(null, new object[] { n, c });
                }
                catch (TargetInvocationException e)
                {
                    throw e.InnerException;
                }
            };
        }

        return _otherPlugin(name, capacity);
    }

    // ---------------------------------------------------------------------------------------
    // Tiny assertion runner
    // ---------------------------------------------------------------------------------------

    private static bool Contains(IReadOnlyList<BackgroundWorker> list, BackgroundWorker worker)
    {
        foreach (BackgroundWorker w in list)
        {
            if (ReferenceEquals(w, worker))
            {
                return true;
            }
        }

        return false;
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000)
            {
                throw new Exception("timed out waiting: " + what);
            }

            Thread.Sleep(5);
        }
    }
}
