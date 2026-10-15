using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SlKit
{
    /// <summary>
    /// Stand-in for the LabAPI-bound logger. Records every line so a test can assert that a warning
    /// was (or was not) written. Thread-safe.
    /// </summary>
    internal static class SlLog
    {
        private static readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();

        internal static void Info(string message) => Lines.Enqueue("INFO " + message);

        internal static void Warn(string message) => Lines.Enqueue("WARN " + message);

        internal static void Error(string message) => Lines.Enqueue("ERROR " + message);

        /// <summary>Returns and forgets everything logged so far.</summary>
        internal static List<string> Take()
        {
            List<string> taken = new List<string>();
            while (Lines.TryDequeue(out string line))
            {
                taken.Add(line);
            }

            return taken;
        }
    }

    /// <summary>
    /// Stand-in for the main-thread queue: there is no game loop in a test, so posted actions run
    /// inline on the posting thread. Only the delivery matters to these tests, not the thread.
    /// </summary>
    public static class MainThread
    {
        public static void Post(Action action) => action();

        internal static void EnsureCallbacksDrained(string api, string owner)
        {
        }
    }
}

namespace SlKit
{
    /// <summary>
    /// Stand-in for the clock: RateLimitedLogger only registers one periodic job, so the stub just
    /// records the registration (name and interval).
    /// </summary>
    public static class SlClock
    {
        internal static readonly List<string> Registrations = new List<string>();

        public static object Every(TimeSpan interval, Action job, string name)
        {
            lock (Registrations)
            {
                Registrations.Add(name + " every " + interval.TotalSeconds + " s");
            }

            return new object();
        }
    }
}
