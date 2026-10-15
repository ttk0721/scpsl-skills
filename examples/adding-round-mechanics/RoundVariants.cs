using LabApi.Events.Arguments.ServerEvents;
using LabApi.Features.Wrappers;

namespace Examples.AddingRoundMechanics
{
    /// <summary>
    /// Compiled fragments for the variants in variants.md. They are not wired into the plugin:
    /// copy the one you need, subscribe its handlers in Enable and reset its flags at
    /// WaitingForPlayers and RoundRestarted, exactly like the plugin does for its own state.
    /// </summary>
    internal static class RoundVariants
    {
        // ---- end on an objective (for example "all generators engaged") ----------------------

        private static bool _objectiveDone;

        /// <summary>Call from your objective code, on the main thread, once per round.</summary>
        internal static void CompleteObjective()
        {
            if (_objectiveDone || !Round.IsRoundInProgress)
                return;
            _objectiveDone = true;
            Round.End(true); // opens the gate; the handler below supplies the verdict
        }

        internal static void OnConditionsCheck(RoundEndingConditionsCheckEventArgs ev)
        {
            if (_objectiveDone)
                ev.CanEnd = true;
        }

        internal static void OnEnding(RoundEndingEventArgs ev)
        {
            // The summary names whoever you put here. It does not change what the game counted.
            if (_objectiveDone)
                ev.LeadingTeam = RoundSummary.LeadingTeam.FacilityForces;
        }

        /// <summary>Call from WaitingForPlayers and RoundRestarted.</summary>
        internal static void ResetObjective() => _objectiveDone = false;

        // ---- keep a round open (event rounds, load tests) ----------------------------------

        private static bool _ownsRoundLock;

        /// <summary>
        /// IsLocked stops the whole end check, so RoundEndingConditionsCheck is never raised.
        /// Use CanEnd = false instead when vanilla should take over again later. The lock is a
        /// static: it survives restarts, so release it yourself and never release another owner's.
        /// </summary>
        internal static void HoldRoundOpen(bool hold)
        {
            if (hold)
            {
                if (Round.IsLocked)
                    return; // somebody else owns it
                Round.IsLocked = true;
                _ownsRoundLock = true;
            }
            else if (_ownsRoundLock)
            {
                Round.IsLocked = false;
                _ownsRoundLock = false;
            }
        }

        // ---- reinforcements as a reward ----------------------------------------------------

        /// <summary>
        /// Tokens are per wave and reset by vanilla at the next round's scene setup, so a grant
        /// needs no cleanup. Waves without a token limit (the setter returns silently) are skipped.
        /// </summary>
        internal static void GrantMtfToken()
        {
            MtfWave mtf = RespawnWaves.PrimaryMtfWave;
            if (mtf != null)
                mtf.RespawnTokens += 1;
        }
    }
}
