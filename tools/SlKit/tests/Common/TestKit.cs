using System;
using System.Collections.Generic;
using System.Globalization;

namespace SlKit.TestSupport;

/// <summary>
/// The runner and assertions of the SlKit test projects: a console program whose exit code is the
/// result, so the repository needs no test framework or NuGet package. A project registers its
/// tests with <see cref="Run"/> and ends <c>Main</c> with <see cref="Finish"/>.
/// </summary>
internal static class TestKit
{
    private static readonly List<string> Failures = new List<string>();
    private static int _passed;

    /// <summary>Runs one test, records the outcome and prints it.</summary>
    /// <param name="name">Test name, normally <c>nameof</c> the test method.</param>
    /// <param name="test">The test; any exception fails it.</param>
    internal static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            Failures.Add(name + ": " + ex.Message);
            Console.WriteLine("FAIL " + name);
            Console.WriteLine("     " + ex.Message);
        }
    }

    /// <summary>Prints the summary of every <see cref="Run"/> so far.</summary>
    /// <returns>0 when all tests passed, otherwise 1: the process exit code.</returns>
    internal static int Finish()
    {
        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL " + _passed + " TESTS PASSED");
            return 0;
        }

        Console.WriteLine(Failures.Count + " FAILED, " + _passed + " passed:");
        foreach (string failure in Failures)
        {
            Console.WriteLine("  - " + failure);
        }

        return 1;
    }

    internal static void True(bool condition, string message = "expected true")
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    internal static void False(bool condition, string message = "expected false")
    {
        if (condition)
        {
            throw new Exception(message);
        }
    }

    internal static void Equal<T>(T expected, T actual, string message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception((message ?? "values differ") + ": expected " + expected + ", got " + actual);
        }
    }

    internal static void Near(double expected, double actual, double tolerance = 1e-9, string message = null)
    {
        if (!(Math.Abs(expected - actual) <= tolerance))
        {
            throw new Exception((message ?? "values differ") + ": expected " + expected.ToString(CultureInfo.InvariantCulture)
                + " +/- " + tolerance.ToString(CultureInfo.InvariantCulture) + ", got " + actual.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal static void SequenceEqual<T>(T[] expected, T[] actual, string message = null)
    {
        if (expected.Length != actual.Length)
        {
            throw new Exception((message ?? "sequence length differs") + ": expected [" + string.Join(",", expected)
                + "], got [" + string.Join(",", actual) + "]");
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
            {
                throw new Exception((message ?? "sequences differ at " + i) + ": expected [" + string.Join(",", expected)
                    + "], got [" + string.Join(",", actual) + "]");
            }
        }
    }

    internal static void Throws<TException>(Action action, string message = null)
        where TException : Exception
    {
        string prefix = message == null ? string.Empty : message + ": ";
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception(prefix + "expected " + typeof(TException).Name + ", got " + ex.GetType().Name + ": " + ex.Message);
        }

        throw new Exception(prefix + "expected " + typeof(TException).Name + ", nothing thrown");
    }
}
