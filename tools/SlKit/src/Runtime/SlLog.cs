using System;
using LabApi.Features.Console;

namespace SlKit;

/// <summary>Internal logging funnel for SlKit's own messages.</summary>
/// <remarks>
/// LabAPI's <c>Logger</c> ends in <c>ServerConsole.AddLog</c>, whose sinks are concurrent queues,
/// so it is safe from any thread (the game itself logs from its background threads). It is not
/// cheap (stack walk for the calling assembly, two regexes per line), so every caller in SlKit
/// rate-limits before reaching here. <c>UnityEngine.Debug.Log</c> is avoided on purpose: from a
/// worker thread Mirror's ThreadLog re-logs it on the main thread, doubling the cost.
/// Every method swallows exceptions from the logger: SlKit logs from inside its catch blocks, on
/// the clock's frame and on the worker thread, and a throwing logger there would skip the
/// remaining jobs of the frame or silently kill the worker thread for the rest of the process.
/// Thread affinity: any thread.
/// </remarks>
internal static class SlLog
{
    private const string Prefix = "[SlKit] ";

    internal static void Info(string message)
    {
        try
        {
            Logger.Info(Prefix + message);
        }
        catch (Exception)
        {
            // Nowhere left to report a failing logger.
        }
    }

    internal static void Warn(string message)
    {
        try
        {
            Logger.Warn(Prefix + message);
        }
        catch (Exception)
        {
            // See Info.
        }
    }

    internal static void Error(string message)
    {
        try
        {
            Logger.Error(Prefix + message);
        }
        catch (Exception)
        {
            // See Info.
        }
    }
}
