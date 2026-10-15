using System;
using System.Collections.Generic;

namespace Examples.TestingPattern.Tests
{
    /// <summary>
    /// Tests for RewardCoordinator, the "handler logic" of a reward plugin. The game is replaced by
    /// two fakes behind the seams in Core/Seams.cs, so nothing here needs a server, a Player or a
    /// Unity object. Exit code 0 means every test passed.
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _passed;

        // ---- fakes -------------------------------------------------------------------------

        private sealed class FakeWorld : IPlayerSource
        {
            public PlayerSnapshot[] Players = new PlayerSnapshot[0];

            public void Fill(List<PlayerSnapshot> into)
            {
                for (int i = 0; i < Players.Length; i++)
                    into.Add(Players[i]);
            }
        }

        private sealed class FakeSink : IRewardSink
        {
            public readonly List<string> Granted = new List<string>();
            public readonly HashSet<string> Failing = new HashSet<string>();

            public void Grant(string userId, int amount)
            {
                if (Failing.Contains(userId))
                    throw new InvalidOperationException("panel down");
                Granted.Add(userId + ":" + amount);
            }
        }

        private sealed class Rig
        {
            public readonly FakeWorld World = new FakeWorld();
            public readonly FakeSink Sink = new FakeSink();
            public RewardRules Rules = new RewardRules { SecondsPerReward = 10, Amount = 5, MaxCreditPerTickSeconds = 5 };
            public readonly RewardCoordinator Sut;
            public double Now;

            public Rig(params PlayerSnapshot[] players)
            {
                World.Players = players;
                Sut = new RewardCoordinator(World, Sink, () => Rules);
            }

            /// <summary>Advances the fake clock one second at a time and ticks, like SlClock.Every(1 s).</summary>
            public void Run(int seconds)
            {
                for (int i = 0; i < seconds; i++)
                {
                    Now += 1;
                    Sut.Tick(Now);
                }
            }
        }

        private static PlayerSnapshot Alive(string id) => new PlayerSnapshot(id, false, true);

        private static int Main()
        {
            Run(nameof(FirstTickOnlySetsTheBaseline), FirstTickOnlySetsTheBaseline);
            Run(nameof(GrantsExactlyAtTheThreshold), GrantsExactlyAtTheThreshold);
            Run(nameof(GrantsAgainAfterEachFurtherThreshold), GrantsAgainAfterEachFurtherThreshold);
            Run(nameof(StaffNeverEarn), StaffNeverEarn);
            Run(nameof(DeadPlayersDoNotAccrue), DeadPlayersDoNotAccrue);
            Run(nameof(NullOrEmptyUserIdIsIgnored), NullOrEmptyUserIdIsIgnored);
            Run(nameof(AHitchCreditsAtMostTheCap), AHitchCreditsAtMostTheCap);
            Run(nameof(RoundStartClearsProgress), RoundStartClearsProgress);
            Run(nameof(LeftRemovesStateAndARejoinStartsAtZero), LeftRemovesStateAndARejoinStartsAtZero);
            Run(nameof(DisabledDoesNothingAndDoesNotCreditTheGap), DisabledDoesNothingAndDoesNotCreditTheGap);
            Run(nameof(RulesAreReadAtUseTime), RulesAreReadAtUseTime);
            Run(nameof(NonPositiveThresholdIsTreatedAsOff), NonPositiveThresholdIsTreatedAsOff);
            Run(nameof(OneFailingSinkCallDoesNotBlockOthersAndIsRetried), OneFailingSinkCallDoesNotBlockOthersAndIsRetried);
            Run(nameof(AnOutageOwesOneRewardNotAHundred), AnOutageOwesOneRewardNotAHundred);
            Run(nameof(SteadyStateTickAllocatesNothing), SteadyStateTickAllocatesNothing);

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

        // ---- tests: one behaviour each, named after the rule -------------------------------

        private static void FirstTickOnlySetsTheBaseline()
        {
            // Round start or enable gives no elapsed time to credit: the first tick has no "previous".
            var rig = new Rig(Alive("a@steam"));
            rig.Rules.SecondsPerReward = 1;
            rig.Sut.Tick(1000);
            Equal(0, rig.Sink.Granted.Count, "nothing granted on the baseline tick");
            Equal(0, rig.Sut.TrackedCount, "nobody tracked yet");
        }

        private static void GrantsExactlyAtTheThreshold()
        {
            var rig = new Rig(Alive("a@steam"));
            rig.Run(1);          // baseline
            rig.Run(9);          // 9 s credited
            Equal(0, rig.Sink.Granted.Count, "9 s is below 10 s");
            rig.Run(1);
            Equal(1, rig.Sink.Granted.Count, "10 s reaches the threshold");
            Equal("a@steam:5", rig.Sink.Granted[0], "who and how much");
        }

        private static void GrantsAgainAfterEachFurtherThreshold()
        {
            var rig = new Rig(Alive("a@steam"));
            rig.Run(1 + 30);
            Equal(3, rig.Sink.Granted.Count, "30 s at 10 s per reward");
        }

        private static void StaffNeverEarn()
        {
            // Business rule the owner never states unless asked: staff are excluded from rewards.
            var rig = new Rig(new PlayerSnapshot("admin@steam", true, true), Alive("a@steam"));
            rig.Run(1 + 10);
            Equal(1, rig.Sink.Granted.Count, "only the non-staff player");
            Equal("a@steam:5", rig.Sink.Granted[0], "the right one");
            Equal(1, rig.Sut.TrackedCount, "staff are not even tracked");
        }

        private static void DeadPlayersDoNotAccrue()
        {
            // The living player is the control: it proves the machinery runs, so "0 for the dead"
            // cannot be a stub that does nothing for everyone.
            var rig = new Rig(new PlayerSnapshot("dead@steam", false, false), Alive("live@steam"));
            rig.Run(1 + 30);
            Equal(3, rig.Sink.Granted.Count, "only the living player is paid");
            Equal("live@steam:5", rig.Sink.Granted[0], "and nobody else");
        }

        private static void NullOrEmptyUserIdIsIgnored()
        {
            // Dummies and half-joined players can have no usable id. Never key state on null.
            var rig = new Rig(new PlayerSnapshot(null, false, true), new PlayerSnapshot("", false, true), Alive("a@steam"));
            rig.Run(1 + 10);
            Equal(1, rig.Sink.Granted.Count, "only the player with an id");
            Equal(1, rig.Sut.TrackedCount, "no state for null or empty ids");
        }

        private static void AHitchCreditsAtMostTheCap()
        {
            var rig = new Rig(Alive("a@steam"));
            rig.Run(1);                 // baseline at t=1
            rig.Sut.Tick(rig.Now + 100); // a 100 s stall (map load, idle mode, hitch)
            Equal(0, rig.Sink.Granted.Count, "the stall credited 5 s, not 100 s");
            rig.Sut.Tick(rig.Now + 100 + 5);
            Equal(1, rig.Sink.Granted.Count, "5 + 5 reaches 10 s");
        }

        private static void RoundStartClearsProgress()
        {
            var rig = new Rig(Alive("a@steam"));
            rig.Run(1 + 8);             // 8 s of 10 s
            rig.Sut.OnRoundStart();
            Equal(0, rig.Sut.TrackedCount, "state dropped at the round boundary");
            rig.Run(1 + 8);             // baseline again, then 8 s
            Equal(0, rig.Sink.Granted.Count, "progress did not carry over");
            rig.Run(2);
            Equal(1, rig.Sink.Granted.Count, "a fresh 10 s still pays");
        }

        private static void LeftRemovesStateAndARejoinStartsAtZero()
        {
            var rig = new Rig(Alive("a@steam"));
            rig.Run(1 + 8);
            rig.Sut.OnLeft("a@steam");
            Equal(0, rig.Sut.TrackedCount, "no leak after Left");
            rig.Run(8);
            Equal(0, rig.Sink.Granted.Count, "8 + 8 would have paid out if the 8 survived the leave");
            rig.Run(2);
            Equal(1, rig.Sink.Granted.Count, "a rejoin earns from zero");
            rig.Sut.OnLeft(null);       // an unresolvable leave must not throw
        }

        private static void DisabledDoesNothingAndDoesNotCreditTheGap()
        {
            var rig = new Rig(Alive("a@steam"));
            rig.Run(1 + 2);
            rig.Rules.Enabled = false;
            rig.Run(100);
            Equal(0, rig.Sink.Granted.Count, "kill switch: no grants");
            rig.Rules.Enabled = true;
            rig.Run(1);                 // re-baseline, credits nothing
            rig.Run(7);
            Equal(0, rig.Sink.Granted.Count, "the disabled period was not credited");
            rig.Run(1);
            Equal(1, rig.Sink.Granted.Count, "2 s before + 8 s after = 10 s, so the switch really re-arms");
        }

        private static void RulesAreReadAtUseTime()
        {
            var rig = new Rig(Alive("a@steam"));
            rig.Run(1 + 5);
            rig.Rules = new RewardRules { SecondsPerReward = 6, Amount = 9, MaxCreditPerTickSeconds = 5 };
            rig.Run(1);
            Equal(1, rig.Sink.Granted.Count, "a replaced rules object applies at once (config reload)");
            Equal("a@steam:9", rig.Sink.Granted[0], "with the new amount");
        }

        private static void NonPositiveThresholdIsTreatedAsOff()
        {
            // A zero from a hand-edited YAML must not mean "reward every tick".
            var rig = new Rig(Alive("a@steam"));
            rig.Rules.SecondsPerReward = 0;
            rig.Run(1 + 10);
            Equal(0, rig.Sink.Granted.Count, "no grants for a non-positive threshold");
            rig.Rules.SecondsPerReward = 10;
            rig.Run(1 + 10);
            Equal(1, rig.Sink.Granted.Count, "and the same player is paid once the rule is sane");
        }

        private static void OneFailingSinkCallDoesNotBlockOthersAndIsRetried()
        {
            var rig = new Rig(Alive("bad@steam"), Alive("good@steam"));
            rig.Sink.Failing.Add("bad@steam");
            rig.Run(1 + 10);
            Equal(1, rig.Sink.Granted.Count, "the healthy player is still paid");
            Equal("good@steam:5", rig.Sink.Granted[0], "and it is the healthy one");
            True(rig.Sut.FailedGrants >= 1, "the failure is counted, not swallowed");

            rig.Sink.Failing.Clear();   // the receiving service recovers
            rig.Run(1);
            True(rig.Sink.Granted.Contains("bad@steam:5"), "the missed reward is delivered after recovery");
            Equal(1, CountOf(rig.Sink.Granted, "bad@steam:5"), "exactly once");
        }

        private static void AnOutageOwesOneRewardNotAHundred()
        {
            // The bound: a receiving service that is down for 100 s must not leave 10 rewards queued for the
            // moment it comes back (a burst of calls into a service that has just recovered).
            var rig = new Rig(Alive("a@steam"));
            rig.Sink.Failing.Add("a@steam");
            rig.Run(1 + 100);
            Equal(0, rig.Sink.Granted.Count, "control: nothing is delivered during the outage");
            True(rig.Sut.FailedGrants >= 1, "control: the outage was really hit");

            rig.Sink.Failing.Clear();
            rig.Run(3);
            Equal(1, rig.Sink.Granted.Count, "one reward was owed, one is delivered, not one per tick");
        }

        private static void SteadyStateTickAllocatesNothing()
        {
            // A per-second tick over 60 players must not feed the GC. Warm up first so the one-off
            // dictionary growth is not counted.
            var players = new PlayerSnapshot[60];
            for (int i = 0; i < players.Length; i++)
                players[i] = Alive("p" + i + "@steam");
            var rig = new Rig(players);
            rig.Rules.SecondsPerReward = 1e9;   // never pays, so the sink is not part of the measurement
            rig.Run(5);
            Equal(60, rig.Sut.TrackedCount, "control: every player really went through the tick");

            long before = GC.GetAllocatedBytesForCurrentThread();
            rig.Run(1000);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Equal(0L, after - before, "bytes allocated by 1000 ticks of 60 players");
        }

        // ---- runner and assertions ---------------------------------------------------------

        private static int CountOf(List<string> list, string item)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i] == item)
                    n++;
            return n;
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

        private static void True(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }

        private static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
        {
            if (!expected.Equals(actual))
                throw new Exception(message + ": expected " + expected + ", got " + actual);
        }
    }
}
