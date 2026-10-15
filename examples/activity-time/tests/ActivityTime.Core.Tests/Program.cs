using System;
using System.Collections.Generic;
using Examples.ActivityTime.Core;

namespace Examples.ActivityTime.Tests
{
    /// <summary>Tests for the pure activity rules. Exit code 0 means every test passed.</summary>
    internal static class Program
    {
        private static int _failed;
        private static int _passed;

        // 0.5 m to count as moving, 30 s idle window, at most 2 s credit per sample, staff groups.
        private static ActivityPolicy NewPolicy() =>
            new ActivityPolicy(0.5, 30, 2, new[] { "owner", "Admin" });

        private static int Main()
        {
            Run(nameof(FirstSampleCreditsNothing), FirstSampleCreditsNothing);
            Run(nameof(MovingAlivePlayerEarnsElapsedTime), MovingAlivePlayerEarnsElapsedTime);
            Run(nameof(StandingStillStopsEarningAfterIdleWindow), StandingStillStopsEarningAfterIdleWindow);
            Run(nameof(DeadPlayerEarnsNothing), DeadPlayerEarnsNothing);
            Run(nameof(StallCreditIsCapped), StallCreditIsCapped);
            Run(nameof(RoundRestartKeepsTotalsAndDropsPositions), RoundRestartKeepsTotalsAndDropsPositions);
            Run(nameof(LeaveAndRejoinDoesNotCreditTheGap), LeaveAndRejoinDoesNotCreditTheGap);
            Run(nameof(StaffGroupsAreExcludedCaseInsensitively), StaffGroupsAreExcludedCaseInsensitively);
            Run(nameof(OnlyReadyNonStaffPlayersAreTracked), OnlyReadyNonStaffPlayersAreTracked);
            Run(nameof(OnlyAccountIdsKeyPersistentData), OnlyAccountIdsKeyPersistentData);

            Console.WriteLine(_failed == 0 ? $"ALL {_passed} TESTS PASSED" : $"{_failed} FAILED, {_passed} passed");
            return _failed == 0 ? 0 : 1;
        }

        private static void OnlyAccountIdsKeyPersistentData()
        {
            True(ActivityPolicy.IsAccountId("76561198000000000@steam"), "steam account");
            True(ActivityPolicy.IsAccountId("123456789012345678@discord"), "discord account");
            True(!ActivityPolicy.IsAccountId("ID_Dummy"), "dummies share one id");
            True(!ActivityPolicy.IsAccountId("ID_Offline_42_1700000000"), "offline ids are per connection");
            True(!ActivityPolicy.IsAccountId(""), "empty");
            True(!ActivityPolicy.IsAccountId(null), "null");
        }

        private static void FirstSampleCreditsNothing()
        {
            var ledger = new ActivityLedger();
            Near(0, ledger.Sample("u1@steam", new Pos3(0, 0, 0), true, 100, NewPolicy()));
            Near(0, ledger.SecondsOf("u1@steam"));
        }

        private static void MovingAlivePlayerEarnsElapsedTime()
        {
            var ledger = new ActivityLedger();
            ActivityPolicy policy = NewPolicy();
            for (int t = 0; t <= 10; t++)
                ledger.Sample("u1@steam", new Pos3(t, 0, 0), true, t, policy); // 1 m per second
            Near(10, ledger.SecondsOf("u1@steam"));
        }

        private static void StandingStillStopsEarningAfterIdleWindow()
        {
            var ledger = new ActivityLedger();
            ActivityPolicy policy = NewPolicy();
            for (int t = 0; t <= 60; t++)
                ledger.Sample("afk@steam", new Pos3(5, 0, 5), true, t, policy);
            Near(30, ledger.SecondsOf("afk@steam")); // credited only inside the 30 s window
        }

        private static void DeadPlayerEarnsNothing()
        {
            var ledger = new ActivityLedger();
            ActivityPolicy policy = NewPolicy();
            for (int t = 0; t <= 10; t++)
                ledger.Sample("spec@steam", new Pos3(t, 0, 0), false, t, policy);
            Near(0, ledger.SecondsOf("spec@steam"));
        }

        private static void StallCreditIsCapped()
        {
            var ledger = new ActivityLedger();
            ActivityPolicy policy = NewPolicy();
            ledger.Sample("u1@steam", new Pos3(0, 0, 0), true, 0, policy);
            Near(2, ledger.Sample("u1@steam", new Pos3(3, 0, 0), true, 40, policy)); // 40 s gap, 2 s credit
        }

        private static void RoundRestartKeepsTotalsAndDropsPositions()
        {
            var ledger = new ActivityLedger();
            ActivityPolicy policy = NewPolicy();
            ledger.Sample("u1@steam", new Pos3(0, 0, 0), true, 0, policy);
            ledger.Sample("u1@steam", new Pos3(1, 0, 0), true, 1, policy);
            ledger.ResetMotion();                                                   // WaitingForPlayers
            Near(0, ledger.Sample("u1@steam", new Pos3(90, 0, 0), true, 2, policy)); // new map: re-baseline
            Near(1, ledger.SecondsOf("u1@steam"));
        }

        private static void LeaveAndRejoinDoesNotCreditTheGap()
        {
            var ledger = new ActivityLedger();
            ActivityPolicy policy = NewPolicy();
            ledger.Sample("u1@steam", new Pos3(0, 0, 0), true, 0, policy);
            ledger.Sample("u1@steam", new Pos3(1, 0, 0), true, 1, policy);
            ledger.ForgetMotion("u1@steam");                                         // Left
            Near(0, ledger.Sample("u1@steam", new Pos3(2, 0, 0), true, 600, policy)); // rejoined later
            Near(1, ledger.SecondsOf("u1@steam"));
            var totals = new List<KeyValuePair<string, double>>();
            ledger.CopyTotals(totals);
            True(totals.Count == 1 && totals[0].Key == "u1@steam", "one total kept across leave");
        }

        private static void StaffGroupsAreExcludedCaseInsensitively()
        {
            ActivityPolicy policy = NewPolicy();
            True(policy.IsExcluded("admin"), "admin excluded");
            True(policy.IsExcluded("OWNER"), "owner excluded");
            True(!policy.IsExcluded("vip"), "vip earns");
            True(!policy.IsExcluded(null), "no group earns");
        }

        private static void OnlyReadyNonStaffPlayersAreTracked()
        {
            ActivityPolicy policy = NewPolicy();
            True(policy.ShouldTrack(false, true, "vip"), "ready player tracked");      // positive control
            True(!policy.ShouldTrack(true, true, null), "host or dummy not tracked");
            True(!policy.ShouldTrack(false, false, null), "unauthenticated not tracked");
            True(!policy.ShouldTrack(false, true, "owner"), "staff not tracked");
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception e)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + ": " + e.Message);
            }
        }

        private static void Near(double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 1e-9)
                throw new Exception($"expected {expected}, got {actual}");
        }

        private static void True(bool condition, string what)
        {
            if (!condition) throw new Exception("expected: " + what);
        }
    }
}
