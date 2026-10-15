using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LabApi.Features.Console;

/// <summary>
/// Stand-in for LabAPI's static logger (the real one ends in the game's console). Records every
/// line so a test can assert on what SlKit logged. Thread-safe.
/// </summary>
public static class Logger
{
    private static readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();

    public static void Info(object message) => Lines.Enqueue("INFO " + message);

    public static void Warn(object message) => Lines.Enqueue("WARN " + message);

    public static void Error(object message) => Lines.Enqueue("ERROR " + message);

    /// <summary>Returns and forgets everything logged so far.</summary>
    public static List<string> Take()
    {
        List<string> taken = new List<string>();
        while (Lines.TryDequeue(out string line))
        {
            taken.Add(line);
        }

        return taken;
    }
}
