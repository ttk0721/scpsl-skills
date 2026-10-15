using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace TickrateFix.Tests
{
    /// <summary>Tests for the rule "make server_tickrate take effect, never lower it". Exit code 0 = all passed.</summary>
    internal static class Program
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _passed;

        private static int Main()
        {
            Run(nameof(Config_IsOffByDefault), Config_IsOffByDefault);
            Run(nameof(Decide_ConfiguredValueIsReappliedAsIs), Decide_ConfiguredValueIsReappliedAsIs);
            Run(nameof(Decide_UnlimitedBecomesTheCap), Decide_UnlimitedBecomesTheCap);
            Run(nameof(Decide_AboveTheCapIsNeverLowered), Decide_AboveTheCapIsNeverLowered);
            Run(nameof(Decide_InvalidCapFallsBackTo60), Decide_InvalidCapFallsBackTo60);
            Run(nameof(Line_IsStableForGrep), Line_IsStableForGrep);
            Run(nameof(Line_IgnoresTheServerCulture), Line_IgnoresTheServerCulture);

            Console.WriteLine();
            if (Failures.Count == 0)
            {
                Console.WriteLine("ALL " + _passed + " TESTS PASSED");
                return 0;
            }

            Console.WriteLine(Failures.Count + " FAILED, " + _passed + " passed:");
            foreach (string failure in Failures)
                Console.WriteLine("  - " + failure);

            return 1;
        }

        private static void Config_IsOffByDefault()
        {
            TickrateFixConfig config = new TickrateFixConfig();
            Equal(false, config.Enabled, "the owner turns the fix on deliberately");
            Equal(60, config.Cap, "default cap");
        }

        private static void Decide_ConfiguredValueIsReappliedAsIs()
        {
            foreach (int configured in new[] { 1, 20, 30, 59, 60 })
            {
                TickrateDecision d = TickratePolicy.Decide(configured, 60);
                Equal(configured, d.Target, "server_tickrate " + configured);
                Equal("configured", d.ReasonToken, "reason for " + configured);
            }
        }

        private static void Decide_UnlimitedBecomesTheCap()
        {
            foreach (int configured in new[] { -1, 0, -5 })
            {
                TickrateDecision d = TickratePolicy.Decide(configured, 60);
                Equal(60, d.Target, "server_tickrate " + configured + " takes the cap");
                Equal("unlimited-replaced-by-cap", d.ReasonToken);
            }

            Equal(30, TickratePolicy.Decide(-1, 30).Target, "a configured cap is used for unlimited");
        }

        private static void Decide_AboveTheCapIsNeverLowered()
        {
            // The regression: 1.0.0 replaced a server_tickrate above the cap with the cap.
            foreach (int configured in new[] { 61, 90, 120, short.MaxValue })
            {
                TickrateDecision d = TickratePolicy.Decide(configured, 60);
                Equal(configured, d.Target, "server_tickrate " + configured + " is kept");
                Equal("above-cap-kept", d.ReasonToken);
                Equal(60, d.Cap, "the cap is reported");
            }

            Equal(45, TickratePolicy.Decide(45, 30).Target, "also above a lower cap");
        }

        private static void Decide_InvalidCapFallsBackTo60()
        {
            foreach (int cap in new[] { 0, -1, 32768, int.MaxValue, int.MinValue })
            {
                Equal(60, TickratePolicy.EffectiveCap(cap), "cap " + cap);
                Equal(60, TickratePolicy.Decide(-1, cap).Target, "unlimited with cap " + cap);
            }

            Equal(32767, TickratePolicy.EffectiveCap(32767), "largest valid cap");
            Equal(1, TickratePolicy.EffectiveCap(1), "smallest valid cap");
        }

        private static void Line_IsStableForGrep()
        {
            Equal(
                "targetFrameRate before=60 after=20 reason=configured server_tickrate=20 cap=60",
                TickratePolicy.MeasurementLine(60, 20, "configured", 20, 60),
                "the line the maintainer greps for");
            Equal(
                "targetFrameRate before=60 after=60 reason=failed server_tickrate=? cap=60",
                TickratePolicy.MeasurementLine(60, 60, "failed", null, 60),
                "unknown tickrate");
        }

        private static void Line_IgnoresTheServerCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                // Swedish formats negative numbers with U+2212, which a grep for "-1" would miss.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("sv-SE");
                Equal(
                    "targetFrameRate before=-1 after=60 reason=unlimited-replaced-by-cap server_tickrate=-1 cap=60",
                    TickratePolicy.MeasurementLine(-1, 60, "unlimited-replaced-by-cap", -1, 60),
                    "invariant digits and sign");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        private static void Run(string name, Action test)
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

        private static void Equal<T>(T expected, T actual, string message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception((message ?? "values differ") + ": expected " + expected + ", got " + actual);
        }
    }
}
