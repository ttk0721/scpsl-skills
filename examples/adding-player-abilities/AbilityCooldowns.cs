using System;
using System.Collections.Generic;
using SlKit.Core;

namespace Examples.AddingPlayerAbilities
{
    /// <summary>Everything the plugin remembers about one player. Plain data: no Player, no ReferenceHub.</summary>
    internal sealed class PlayerAbilityState
    {
        /// <summary>Stopwatch timestamp at which each ability is ready again; 0 or a past value = ready.</summary>
        public readonly long[] ReadyAt;

        /// <summary>Abuse limiter for incoming key events. SSS has no server-side rate limit of its own.</summary>
        public readonly TokenBucket Input;

        /// <summary>Earliest time the next "denied" hint may be shown, so a held-down key cannot spam hints.</summary>
        public long NextDeniedHintAt;

        public PlayerAbilityState(int abilityCount, double burst, double perSecond, long now)
        {
            ReadyAt = new long[abilityCount];
            Input = new TokenBucket(burst, perSecond, now);
        }
    }

    /// <summary>
    /// Cooldown and rate-limit bookkeeping, deliberately free of Unity and LabAPI so it can be unit-tested on
    /// plain .NET (see the testing-pattern example). The caller passes "now" (a Stopwatch timestamp), which
    /// is the same wall-clock base SlClock uses: cooldowns must not run on Time.time, which idle mode scales
    /// by 0.01.
    ///
    /// State is keyed by UserId, not PlayerId: PlayerIds are recycled, and a cooldown that vanishes when the
    /// player reconnects is an exploit. It is cleared at WaitingForPlayers, so nothing leaks across rounds.
    /// </summary>
    internal sealed class AbilityCooldowns
    {
        private readonly Dictionary<string, PlayerAbilityState> _states = new Dictionary<string, PlayerAbilityState>(64);
        private readonly int _abilityCount;

        public AbilityCooldowns(int abilityCount)
        {
            _abilityCount = abilityCount;
        }

        public int Count => _states.Count;

        public PlayerAbilityState GetOrCreate(string userId, double burst, double perSecond, long now)
        {
            if (!_states.TryGetValue(userId, out PlayerAbilityState state))
            {
                state = new PlayerAbilityState(_abilityCount, Math.Max(1, burst), Math.Max(0, perSecond), now);
                _states[userId] = state;
            }

            return state;
        }

        public static TimeSpan Remaining(PlayerAbilityState state, int ability, long now)
        {
            long readyAt = state.ReadyAt[ability];
            return readyAt > now ? StopwatchTime.ToTimeSpan(readyAt - now) : TimeSpan.Zero;
        }

        /// <summary>Starts the cooldown and returns the timestamp at which it ends.</summary>
        public static long Start(PlayerAbilityState state, int ability, long now, double cooldownSeconds)
        {
            // Clamp: a typo in the config (negative or absurdly large) must not brick or disable the ability.
            double seconds = Math.Min(Math.Max(cooldownSeconds, 0), 3600);
            long readyAt = now + StopwatchTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
            state.ReadyAt[ability] = readyAt;
            return readyAt;
        }

        /// <summary>Forgets a player who left, unless a cooldown is still running (rejoining must not reset it).</summary>
        public void DropIfIdle(string userId, long now)
        {
            if (!_states.TryGetValue(userId, out PlayerAbilityState state))
                return;

            foreach (long readyAt in state.ReadyAt)
                if (readyAt > now)
                    return;

            _states.Remove(userId);
        }

        public void Clear() => _states.Clear();
    }
}
