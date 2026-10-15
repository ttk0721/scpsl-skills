using System;
using System.IO;
using System.Text;

namespace SlKit;

/// <summary>
/// The CSV file of the frame profiler and everything done to it on the profiler's own <see cref="BackgroundWorker"/>:
/// writing the filled batches, reopening when the path changes, closing on request. Writer thread only.
/// </summary>
internal static class FrameCsvWriter
{
    /// <summary>The job that writes whatever is queued; the main thread hands it to the worker once per second.</summary>
    internal static readonly Action WriteAction = WriteFilledBatches;

    /// <summary>The job that writes what is still queued and closes the file (see <see cref="FrameProfiler.StopCsv"/>).</summary>
    internal static readonly Action CloseAction = FlushAndClose;

    private static StreamWriter _writer;
    private static string _writerPath;

    private static void WriteFilledBatches()
    {
        while (FrameCsvBatchPool.TryTakeSubmitted(out FrameCsvBatch batch))
        {
            try
            {
                WriteBatch(batch);
            }
            catch (Exception e)
            {
                // A full disk or a locked file fails once per second for as long as the sink is on:
                // rate-limit it (the bucket is shared with every other SlKit caller and thread-safe).
                string path = batch.Path;
                string reason = e.Message;
                RateLimitedLog.Error("slkit.csv", () => "FrameProfiler CSV write failed for '" + path + "': " + reason);
                CloseWriter();
            }
            finally
            {
                FrameCsvBatchPool.Release(batch);
            }
        }
    }

    // Close request from StopCsv. Batches whose write request was refused by a full worker queue are
    // still waiting in the pool's filled queue; writing them first keeps the last seconds, and not leaving them
    // behind stops a later write from reopening a file the user has already stopped logging to.
    private static void FlushAndClose()
    {
        WriteFilledBatches();
        CloseWriter();
    }

    private static void WriteBatch(FrameCsvBatch batch)
    {
        if (batch.Path == null)
        {
            return;
        }

        if (_writer == null || !string.Equals(_writerPath, batch.Path, StringComparison.Ordinal))
        {
            CloseWriter();
            string directory = Path.GetDirectoryName(batch.Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bool needsHeader = !File.Exists(batch.Path) || new FileInfo(batch.Path).Length == 0;
            _writer = new StreamWriter(new FileStream(batch.Path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _writerPath = batch.Path;
            if (needsHeader)
            {
                _writer.WriteLine(FrameCsvLine.Header);
            }
        }

        _writer.WriteLine(FrameCsvLine.Format(batch));
        _writer.Flush();
    }

    private static void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // Closing is best effort; the next write reopens.
        }

        _writer = null;
        _writerPath = null;
    }
}
