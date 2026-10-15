using System.Collections.Generic;

namespace Examples.TestingPattern
{
    /// <summary>
    /// What the logic needs to know about one player, copied out of the game on the main thread.
    /// No Player, no ReferenceHub: this type (and everything in Core/) compiles without the game.
    /// </summary>
    public struct PlayerSnapshot
    {
        public string UserId;
        public bool IsStaff;
        public bool IsAlive;

        public PlayerSnapshot(string userId, bool isStaff, bool isAlive)
        {
            UserId = userId;
            IsStaff = isStaff;
            IsAlive = isAlive;
        }
    }

    /// <summary>Seam 1: the world, read-only. The plugin implements it over Player.ReadyList.</summary>
    public interface IPlayerSource
    {
        /// <summary>Appends one snapshot per real player to <paramref name="into"/> (the caller clears it).</summary>
        void Fill(List<PlayerSnapshot> into);
    }

    /// <summary>Seam 2: the side effect. The plugin implements it over a log line, an HTTP call or a database.</summary>
    public interface IRewardSink
    {
        /// <summary>
        /// Delivers a reward. May throw; the coordinator treats a throw as "not delivered" and retries
        /// next tick. Called on the MAIN THREAD, so it must return fast: a real sink that talks to a
        /// web service or database enqueues the call and returns (scpsl-main-thread-and-timing).
        /// </summary>
        void Grant(string userId, int amount);
    }

    /// <summary>The rules. Handed over through a Func so a config reload (which replaces Config) takes effect.</summary>
    public sealed class RewardRules
    {
        public bool Enabled = true;
        public double SecondsPerReward = 600;
        public int Amount = 10;

        /// <summary>
        /// Longest span one tick may credit. A hitch, a map load or idle mode must not hand out
        /// minutes of "playtime" in one go.
        /// </summary>
        public double MaxCreditPerTickSeconds = 5;
    }
}
