using System.Collections.Concurrent;

namespace SlKit;

/// <summary>
/// The fixed set of <see cref="FrameCsvBatch"/> objects that travel between the main thread (which fills one per second)
/// and the CSV writer (which empties them): free ones wait for the main thread, filled ones for the writer. Nothing is
/// allocated while it runs; when no batch is free the writer is behind and the second is dropped. Thread affinity: any thread.
/// </summary>
internal static class FrameCsvBatchPool
{
    private const int PoolSize = 4;

    private static readonly ConcurrentQueue<FrameCsvBatch> Filled = new ConcurrentQueue<FrameCsvBatch>();
    private static readonly ConcurrentQueue<FrameCsvBatch> Free = new ConcurrentQueue<FrameCsvBatch>();

    /// <summary>Creates the batches, each able to hold <paramref name="capacity"/> frames. Main thread.</summary>
    internal static void Prepare(int capacity)
    {
        for (int i = 0; i < PoolSize; i++)
        {
            Free.Enqueue(new FrameCsvBatch(capacity));
        }
    }

    /// <summary>Takes a free batch to fill; false when the writer still holds them all. Main thread.</summary>
    internal static bool TryRent(out FrameCsvBatch batch) => Free.TryDequeue(out batch);

    /// <summary>Hands back a batch that was rented but not filled. Main thread.</summary>
    internal static void Return(FrameCsvBatch batch) => Free.Enqueue(batch);

    /// <summary>Queues a filled batch for the writer. Main thread.</summary>
    internal static void Submit(FrameCsvBatch batch) => Filled.Enqueue(batch);

    /// <summary>Takes the oldest filled batch; false when there is none. Writer thread.</summary>
    internal static bool TryTakeSubmitted(out FrameCsvBatch batch) => Filled.TryDequeue(out batch);

    /// <summary>Makes a written batch available to the main thread again. Writer thread.</summary>
    internal static void Release(FrameCsvBatch batch) => Free.Enqueue(batch);
}
