using System;
using System.Threading;
using System.Threading.Tasks;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of BoundedWorkQueue, RunningStats and the Stopwatch tick conversions.</summary>
internal static class QueueStatsAndTimeTests
{
    internal static void Register()
    {
        // Supporting pure pieces
        Run(nameof(Queue_RejectsWhenFullAndCountsRejections), Queue_RejectsWhenFullAndCountsRejections);
        Run(nameof(Queue_IsFifoAndTimesOutWhenEmpty), Queue_IsFifoAndTimesOutWhenEmpty);
        Run(nameof(Queue_ConcurrentProducersNeverExceedCapacity), Queue_ConcurrentProducersNeverExceedCapacity);
        Run(nameof(Queue_ConcurrentProducersAndConsumerLoseNothing), Queue_ConcurrentProducersAndConsumerLoseNothing);
        Run(nameof(Stats_TrackLastMaxAverage), Stats_TrackLastMaxAverage);
        Run(nameof(Time_ConversionsRoundTrip), Time_ConversionsRoundTrip);
        Run(nameof(Time_HugeDurationsClampInsteadOfOverflowing), Time_HugeDurationsClampInsteadOfOverflowing);
    }

    // ---------------------------------------------------------------------------------------
    // Supporting pure pieces (queue, running stats, time conversions)
    // ---------------------------------------------------------------------------------------

    private static void Queue_RejectsWhenFullAndCountsRejections()
    {
        BoundedWorkQueue<int> queue = new BoundedWorkQueue<int>(3);
        Equal(3, queue.Capacity);
        True(queue.TryEnqueue(1));
        True(queue.TryEnqueue(2));
        True(queue.TryEnqueue(3));
        False(queue.TryEnqueue(4), "full");
        False(queue.TryEnqueue(5), "still full");
        Equal(3, queue.Count);
        Equal(2L, queue.RejectedCount);

        int item;
        True(queue.TryDequeue(out item, 0));
        Equal(1, item);
        True(queue.TryEnqueue(6), "room again after a dequeue");
        Equal(3, queue.Count);
        Throws<ArgumentOutOfRangeException>(() => new BoundedWorkQueue<int>(0), "capacity 0");
    }

    private static void Queue_IsFifoAndTimesOutWhenEmpty()
    {
        BoundedWorkQueue<string> queue = new BoundedWorkQueue<string>(8);
        queue.TryEnqueue("a");
        queue.TryEnqueue("b");

        string item;
        True(queue.TryDequeue(out item, 0));
        Equal("a", item);
        True(queue.TryDequeue(out item, 0));
        Equal("b", item);
        False(queue.TryDequeue(out item, 0), "empty, no wait");
        True(item == null, "item is default on failure");
        False(queue.TryDequeue(out item, 20), "empty, short wait times out");
        Equal(0, queue.Count);
    }

    private static void Queue_ConcurrentProducersNeverExceedCapacity()
    {
        const int Capacity = 100;
        const int Producers = 8;
        const int PerProducer = 1000;
        BoundedWorkQueue<int> queue = new BoundedWorkQueue<int>(Capacity);
        long accepted = 0;
        Task[] tasks = new Task[Producers];
        for (int p = 0; p < Producers; p++)
        {
            tasks[p] = Task.Run(() =>
            {
                for (int i = 0; i < PerProducer; i++)
                {
                    if (queue.TryEnqueue(i))
                    {
                        Interlocked.Increment(ref accepted);
                    }
                }
            });
        }

        Task.WaitAll(tasks);
        Equal((long)Capacity, accepted, "exactly capacity accepted with no consumer");
        Equal(Capacity, queue.Count);
        Equal(Producers * PerProducer - (long)Capacity, queue.RejectedCount, "every other call counted as rejected");

        int drained = 0;
        int item;
        while (queue.TryDequeue(out item, 0))
        {
            drained++;
        }

        Equal(Capacity, drained);
        Equal(0, queue.Count);
    }

    // The one test that runs the queue with a live consumer: BackgroundWorker uses this queue for all
    // SlKit background work. A producer that publishes the semaphore before the item, a lost wake-up
    // or a double dequeue shows up as a consumer that never finishes or as a wrong count or sum. The
    // producers pause now and then so that the consumer keeps catching up and waiting on an empty
    // queue, which is the moment a wrong publication order bites; several rounds make it reliable.
    private static void Queue_ConcurrentProducersAndConsumerLoseNothing()
    {
        const int Rounds = 6;
        const int Producers = 4;
        const int PerProducer = 2000;
        for (int round = 0; round < Rounds; round++)
        {
            BoundedWorkQueue<int> queue = new BoundedWorkQueue<int>(8);
            long consumed = 0;
            long sum = 0;
            using CancellationTokenSource done = new CancellationTokenSource();

            Thread consumer = new Thread(() =>
            {
                while (!done.IsCancellationRequested || queue.Count > 0)
                {
                    int item;
                    if (queue.TryDequeue(out item, 10))
                    {
                        consumed++;
                        sum += item;
                    }
                }
            })
            {
                IsBackground = true,
            };
            consumer.Start();

            Task[] tasks = new Task[Producers];
            long expectedSum = 0;
            object sumLock = new object();
            for (int p = 0; p < Producers; p++)
            {
                tasks[p] = Task.Run(() =>
                {
                    long local = 0;
                    for (int i = 1; i <= PerProducer; i++)
                    {
                        while (!queue.TryEnqueue(i))
                        {
                            Thread.SpinWait(20);
                        }

                        local += i;
                        if (i % 4 == 0)
                        {
                            Thread.Yield();
                        }
                    }

                    lock (sumLock)
                    {
                        expectedSum += local;
                    }
                });
            }

            Task.WaitAll(tasks);
            done.Cancel();
            bool finished = consumer.Join(TimeSpan.FromSeconds(10));
            True(finished, "round " + round + ": the consumer drained the queue and stopped");
            if (!finished)
            {
                return;
            }

            Equal((long)Producers * PerProducer, consumed, "round " + round + ": every item was consumed");
            Equal(expectedSum, sum, "round " + round + ": the right items");
            Equal(0, queue.Count);
        }
    }

    private static void Stats_TrackLastMaxAverage()
    {
        RunningStats stats = default;
        Equal(0L, stats.Count);
        Near(0, stats.AverageMicroseconds, 0, "empty average");

        long ms = StopwatchTime.FromMilliseconds(1);
        stats.Add(ms);
        stats.Add(3 * ms);
        stats.Add(2 * ms);
        Equal(3L, stats.Count);
        Equal(2 * ms, stats.LastTicks);
        Equal(3 * ms, stats.MaxTicks);
        Equal(6 * ms, stats.TotalTicks);
        Near(2000, stats.AverageMicroseconds, 1.0, "average 2 ms in microseconds");
        Near(3000, stats.MaxMicroseconds, 1.0);
        Near(2000, stats.LastMicroseconds, 1.0);

        stats.Reset();
        Equal(0L, stats.Count);
        Equal(0L, stats.MaxTicks);
    }

    private static void Time_ConversionsRoundTrip()
    {
        True(StopwatchTime.Frequency > 0, "positive frequency");
        Equal(StopwatchTime.Frequency, StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(1)), "1 s");
        Equal(StopwatchTime.Frequency, StopwatchTime.FromMilliseconds(1000), "1000 ms");
        Equal(0L, StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(-5)), "negative span");
        Equal(0L, StopwatchTime.FromMilliseconds(-1), "negative ms");
        Equal(0L, StopwatchTime.FromTimeSpan(TimeSpan.Zero));

        TimeSpan span = TimeSpan.FromMilliseconds(250);
        long ticks = StopwatchTime.FromTimeSpan(span);
        True(Math.Abs((StopwatchTime.ToTimeSpan(ticks) - span).TotalMilliseconds) < 0.001, "TimeSpan round trip");
        Near(250, StopwatchTime.ToMilliseconds(ticks), 0.001, "ToMilliseconds");
        Near(250_000, StopwatchTime.ToMicroseconds(ticks), 1.0, "ToMicroseconds");

        long before = StopwatchTime.Now;
        long after = StopwatchTime.Now;
        True(after >= before, "Now is monotonic");
    }

    private static void Time_HugeDurationsClampInsteadOfOverflowing()
    {
        // (long)double is unspecified out of range (long.MinValue on x86): "never" must not become "long ago".
        long max = StopwatchTime.FromTimeSpan(TimeSpan.MaxValue);
        Equal(StopwatchTime.MaxDurationTicks, max, "TimeSpan.MaxValue clamps");
        Equal(StopwatchTime.MaxDurationTicks, StopwatchTime.FromMilliseconds(double.MaxValue), "huge ms clamps");
        Equal(StopwatchTime.MaxDurationTicks, StopwatchTime.FromMilliseconds(double.PositiveInfinity), "infinite ms clamps");
        True(max > 0, "positive");

        // The clamped value is safe to add to a timestamp and to double, as DeadlineTimer does.
        long now = StopwatchTime.Now;
        DeadlineTimer once = DeadlineTimer.OneShot(now, max);
        True(once.Deadline > now, "one-shot deadline is in the future, not wrapped");
        False(once.Poll(now + StopwatchTime.Frequency * 3600), "an hour later it has still not fired");

        DeadlineTimer periodic = DeadlineTimer.Periodic(now, max);
        False(periodic.Poll(now + StopwatchTime.Frequency * 3600), "huge period does not fire early");
        True(StopwatchTime.ToTimeSpan(max) > TimeSpan.FromDays(365), "converts back to a long span");
    }
}
