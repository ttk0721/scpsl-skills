using System;
using System.Collections.Generic;

namespace Examples.TestingPattern
{
    /// <summary>
    /// Credits "active seconds" to living, non-staff players once per tick and pays a reward each
    /// time a player's credit reaches the threshold. The handler logic of a reward plugin, with no
    /// game types: the world comes in through <see cref="IPlayerSource"/>, the effect goes out
    /// through <see cref="IRewardSink"/>, time is a number the caller passes in.
    /// Thread affinity: MAIN THREAD only (the plugin calls it from SlClock and from events).
    /// </summary>
    public sealed class RewardCoordinator
    {
        private readonly IPlayerSource _source;
        private readonly IRewardSink _sink;
        private readonly Func<RewardRules> _rules;

        // Keyed by UserId, never by Player: a Player is dead after a round restart.
        private readonly Dictionary<string, double> _credit = new Dictionary<string, double>(64);
        private readonly List<PlayerSnapshot> _buffer = new List<PlayerSnapshot>(64);
        private double _lastTick = double.NaN;

        public RewardCoordinator(IPlayerSource source, IRewardSink sink, Func<RewardRules> rules)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        public int TrackedCount => _credit.Count;

        /// <summary>Failed grant ATTEMPTS (a retry that fails again counts again). A health number for the plugin to log, rate-limited.</summary>
        public int FailedGrants { get; private set; }

        /// <summary>Per-round reset (WaitingForPlayers): drop every credit and the time baseline.</summary>
        public void OnRoundStart()
        {
            _credit.Clear();
            _lastTick = double.NaN;
        }

        public void OnLeft(string userId)
        {
            if (userId != null)
                _credit.Remove(userId);
        }

        public void Tick(double nowSeconds)
        {
            RewardRules rules = _rules();
            if (rules == null || !rules.Enabled || rules.SecondsPerReward <= 0)
            {
                _lastTick = double.NaN; // off: the gap must not be credited when it is switched back on
                return;
            }

            if (double.IsNaN(_lastTick))
            {
                _lastTick = nowSeconds; // first tick after a reset only sets the baseline
                return;
            }

            double elapsed = nowSeconds - _lastTick;
            _lastTick = nowSeconds;
            if (elapsed <= 0)
                return;
            if (elapsed > rules.MaxCreditPerTickSeconds)
                elapsed = rules.MaxCreditPerTickSeconds;

            _buffer.Clear();
            _source.Fill(_buffer);
            for (int i = 0; i < _buffer.Count; i++)
            {
                PlayerSnapshot p = _buffer[i];
                if (string.IsNullOrEmpty(p.UserId) || p.IsStaff || !p.IsAlive)
                    continue;

                _credit.TryGetValue(p.UserId, out double have);
                have += elapsed;
                if (have >= rules.SecondsPerReward)
                {
                    if (TryGrant(p.UserId, rules.Amount))
                        have -= rules.SecondsPerReward;
                    else
                        have = rules.SecondsPerReward; // owed: retried next tick, but an outage never queues more than one
                }

                _credit[p.UserId] = have;
            }
        }

        private bool TryGrant(string userId, int amount)
        {
            try
            {
                _sink.Grant(userId, amount);
                return true;
            }
            catch (Exception)
            {
                // One bad grant must not abort the pass for the other players.
                FailedGrants++;
                return false;
            }
        }
    }
}
