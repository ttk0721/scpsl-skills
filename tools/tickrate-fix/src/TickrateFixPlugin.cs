using System;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;

namespace TickrateFix
{
    /// <summary>What one <see cref="TickrateFixPlugin.Apply"/> or <see cref="TickrateFixPlugin.Observe"/> call did.</summary>
    public enum TickrateFixOutcome
    {
        /// <summary><c>Server.MaxTps</c> was assigned and the frame-rate target re-applied.</summary>
        Applied = 0,

        /// <summary>Idle mode is active, so nothing was changed (leaving idle mode re-applies the tickrate by itself).</summary>
        SkippedIdle = 1,

        /// <summary>An exception was caught while reading or assigning; it was logged and nothing is known to have changed.</summary>
        Failed = 2,

        /// <summary>The plugin is disabled in its config: the frame-rate target was only read and logged.</summary>
        Observed = 3,
    }

    /// <summary>
    /// Makes the <c>server_tickrate</c> config value take effect, which on its own it may not.
    /// </summary>
    /// <remarks>
    /// Why: reading the code of the game, Mirror and LabAPI shows that the game applies
    /// <c>server_tickrate</c> when the match is created, and that Mirror's <c>StartHost</c> then sets
    /// <c>Application.targetFrameRate</c> to its send rate (60 by default), until the first idle-mode
    /// exit or a config reload. LabAPI's <c>Server.MaxTps</c> getter returns the configured value and
    /// its setter writes the frame-rate target again, so assigning the property to itself re-applies
    /// the config. None of this has been measured on a running server yet, so the plugin is 0.1.0,
    /// starts disabled, and logs <c>Application.targetFrameRate</c> before and after at every
    /// <c>WaitingForPlayers</c> (also while disabled) so the override can be seen and measured first.
    /// Why a plugin and not a library call: the frame rate is one value for the whole server. Only the
    /// server owner should decide it, by installing and enabling this plugin, never a library or
    /// another plugin as a side effect.
    /// Thread affinity: every member runs on the main thread (LabAPI calls <see cref="Enable"/> and the
    /// event handler there, and both touch Unity and the game's static state).
    /// </remarks>
    public sealed class TickrateFixPlugin : Plugin<TickrateFixConfig>
    {
        /// <summary>Default cap: the rate Mirror already sets, and the one the game's position distributor clamps to.</summary>
        public const int DefaultCap = TickratePolicy.DefaultCap;

        // One warning per process for a server_tickrate above the cap: it is the owner's setting and
        // stays as configured, so repeating the warning every round would only be noise.
        private static bool _aboveCapWarned;

        private bool _subscribed;

        /// <summary>Plugin name; LabAPI also uses it as the config directory name, so it stays constant.</summary>
        public override string Name => "TickrateFix";

        /// <inheritdoc />
        public override string Description => "Makes server_tickrate take effect (Mirror overrides it with its send rate at host start). Off by default; logs targetFrameRate either way.";

        /// <inheritdoc />
        public override string Author => "scpsl-skills contributors";

        /// <inheritdoc />
        public override Version Version => new Version(0, 1, 0);

        /// <inheritdoc />
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        /// <summary>
        /// Subscribes to <c>WaitingForPlayers</c>. Nothing is applied here: <c>Enable</c> runs before the
        /// host starts, so Mirror's override would come afterwards and undo it.
        /// Thread affinity: MAIN (LabAPI calls it from <c>ServerStatic.Awake</c>).
        /// </summary>
        public override void Enable()
        {
            if (_subscribed)
                return;

            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            _subscribed = true;
            Logger.Info("TickrateFix " + Version + " loaded: enabled=" + Config.Enabled + ", cap=" + TickratePolicy.EffectiveCap(Config.Cap)
                + (Config.Enabled
                    ? ". server_tickrate is re-applied at every WaitingForPlayers (never lowered; -1/0 become the cap)."
                    : ". Observe only (set enabled: true to apply): targetFrameRate is logged at every WaitingForPlayers, nothing is changed."));
        }

        /// <summary>
        /// Unsubscribes. LabAPI and the game never call it; it exists for completeness and does not
        /// restore the old frame rate (there is nothing correct to restore: Mirror's value was the bug).
        /// Thread affinity: MAIN.
        /// </summary>
        // Nothing to flush or persist, so no Shutdown subscription is needed.
        // scpsl-lint: ignore SL015
        public override void Disable()
        {
            if (!_subscribed)
                return;

            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            _subscribed = false;
        }

        /// <summary>
        /// Re-assigns <c>Server.MaxTps</c> to the configured <c>server_tickrate</c>. -1 or 0 (unlimited)
        /// become <paramref name="cap"/>; a value above the cap is kept as configured, with one warning
        /// per process, because it is the owner's explicit choice. Skipped while idle mode is active:
        /// the setter would replace the idle frame rate (1 FPS by default) while <c>Time.timeScale</c> is
        /// still 0.01, and the server would spin at full speed on a clock that is 100 times slow;
        /// leaving idle mode re-applies the stored tickrate by itself.
        /// Every call logs one measurement line,
        /// <c>targetFrameRate before=X after=Y reason=Z server_tickrate=N cap=C</c>; the game adds its own
        /// "Server tickrate set to ..." line for every assignment.
        /// Thread affinity: MAIN. Never throws.
        /// </summary>
        /// <param name="cap">Frame-rate target for "unlimited", 1 to 32767; other values become 60.</param>
        /// <returns>What happened.</returns>
        internal static TickrateFixOutcome Apply(int cap)
        {
            cap = TickratePolicy.EffectiveCap(cap);
            int before = ReadTargetFrameRate();
            int? configured = null;
            try
            {
                configured = Server.MaxTps;
                if (Server.IdleModeActive)
                {
                    Logger.Info(TickratePolicy.MeasurementLine(before, before, "idle-mode-skipped", configured, cap)
                        + " (leaving idle mode re-applies the configured tickrate)");
                    return TickrateFixOutcome.SkippedIdle;
                }

                TickrateDecision decision = TickratePolicy.Decide(configured.Value, cap);
                if (decision.Reason == TickrateReason.AboveCapKept && !_aboveCapWarned)
                {
                    _aboveCapWarned = true;
                    Logger.Warn("server_tickrate=" + configured.Value + " is above cap=" + cap
                        + ". It is applied as configured: this plugin never lowers your setting, the cap only replaces -1/0 (unlimited). "
                        + "The game sends player positions at most 60 times per second whatever the tickrate. This warning is shown once.");
                }

                Server.MaxTps = (short)decision.Target;
                int after = ReadTargetFrameRate();
                Logger.Info(TickratePolicy.MeasurementLine(before, after, decision.ReasonToken, configured, cap)
                    + " assigned=" + decision.Target);
                return TickrateFixOutcome.Applied;
            }
            catch (Exception e)
            {
                Logger.Warn(TickratePolicy.MeasurementLine(before, ReadTargetFrameRate(), "failed", configured, cap)
                    + " error=" + e.GetType().Name + ": " + e.Message);
                return TickrateFixOutcome.Failed;
            }
        }

        /// <summary>
        /// Logs the measurement line without changing anything: the frame-rate target at
        /// <c>WaitingForPlayers</c> while the plugin is disabled. Where Mirror's override happens and
        /// <c>server_tickrate</c> is not 60, <c>before</c> differs from <c>server_tickrate</c>.
        /// Thread affinity: MAIN. Never throws.
        /// </summary>
        /// <param name="cap">The configured cap, for the line.</param>
        /// <returns><see cref="TickrateFixOutcome.Observed"/>, or <see cref="TickrateFixOutcome.Failed"/>.</returns>
        internal static TickrateFixOutcome Observe(int cap)
        {
            cap = TickratePolicy.EffectiveCap(cap);
            int before = ReadTargetFrameRate();
            try
            {
                int configured = Server.MaxTps;
                Logger.Info(TickratePolicy.MeasurementLine(before, before, "disabled-observe-only", configured, cap));
                return TickrateFixOutcome.Observed;
            }
            catch (Exception e)
            {
                Logger.Warn(TickratePolicy.MeasurementLine(before, before, "failed", null, cap)
                    + " error=" + e.GetType().Name + ": " + e.Message);
                return TickrateFixOutcome.Failed;
            }
        }

        // -1 when it cannot be read. Unity also uses -1 for "no target" (unlimited); either is worth
        // a look in the log, so the two are not told apart.
        private static int ReadTargetFrameRate()
        {
            try
            {
                return UnityEngine.Application.targetFrameRate;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // Once per round, on the main thread, after the host started (so Mirror's override already
        // happened). Config is read here, not cached, because a config reload replaces the object.
        private void OnWaitingForPlayers()
        {
            if (!Config.Enabled)
            {
                Observe(Config.Cap);
                return;
            }

            Apply(Config.Cap);
        }
    }
}
