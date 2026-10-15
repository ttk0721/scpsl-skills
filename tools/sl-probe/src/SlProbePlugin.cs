using System;
using System.IO;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Arguments.WarheadEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using SlKit;

namespace SlProbe
{
    /// <summary>
    /// Measures real server frame time with <see cref="FrameProfiler"/>: the honest replacement for
    /// <c>Server.Tps</c> when you need to know whether a plugin made the server slower.
    /// </summary>
    /// <remarks>
    /// Why: <c>Server.Tps</c> is <c>round(1 / Time.smoothDeltaTime)</c>, smoothed, clamped and scaled by
    /// <c>timeScale</c>, so a server that hitches for 300 ms every few seconds can still read 60. The probe
    /// records the wall-clock period and busy time of every frame at the PlayerLoop boundaries.
    /// The probe itself costs two PlayerLoop callbacks per frame and one clock job per report interval;
    /// <c>slprobe status</c> shows the measured cost. Round events are subscribed here because they are
    /// per-round, none of them is on the catalog's hot list.
    /// LabAPI never calls <see cref="Disable"/>; nothing needs flushing there, because the CSV writer
    /// flushes after every line.
    /// Thread affinity: <see cref="Enable"/>, the event handlers and the clock job run on the main thread.
    /// </remarks>
    public sealed class SlProbePlugin : Plugin<SlProbeConfig>
    {
        private const int MinWindowFrames = 300;
        private const int MaxWindowFrames = 100000;

        private IDisposable _reportJob;
        private bool _subscribed;

        /// <summary>The enabled plugin instance, or null before <see cref="Enable"/> succeeded. Thread affinity: any thread.</summary>
        public static SlProbePlugin Instance { get; private set; }

        /// <summary>
        /// Plugin name. LabAPI also uses it as the config directory name, so it stays constant and path-safe.
        /// </summary>
        public override string Name => "SlProbe";

        /// <inheritdoc />
        public override string Description => "Measures real server frame time (p50/p95/p99, % slow frames) for A/B testing of plugins.";

        /// <inheritdoc />
        public override string Author => "scpsl-skills contributors";

        /// <inheritdoc />
        public override Version Version => new Version(0, 1, 0);

        /// <inheritdoc />
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        /// <summary>
        /// The probe only reads timings and writes a log, it changes nothing for players, which is what
        /// LabAPI's "transparent" flag is meant for.
        /// </summary>
        public override bool IsTransparent => true;

        /// <summary>
        /// Installs the profiler and the clock, optionally starts the CSV, registers the report job and
        /// subscribes the round-event markers. Fallible work comes first and subscriptions last, because
        /// LabAPI does not roll anything back when <c>Enable</c> throws.
        /// Thread affinity: MAIN THREAD (LabAPI calls it from <c>ServerStatic.Awake</c>).
        /// </summary>
        public override void Enable()
        {
            try
            {
                SlClock.Install();
                FrameProfiler.Install(Math.Max(MinWindowFrames, Math.Min(MaxWindowFrames, Config.WindowFrames)));

                if (Config.CsvEnabled)
                {
                    string path = ResolveCsvPath();
                    FrameProfiler.StartCsv(path);
                    Logger.Info("CSV frame log: " + path);
                }

                if (Config.ReportEverySeconds > 0)
                {
                    _reportJob = SlClock.Every(
                        TimeSpan.FromSeconds(Config.ReportEverySeconds),
                        ReportTick,
                        "slprobe.report");
                }

                Subscribe();
                Instance = this;
                Logger.Info("Probe running: window " + FrameProfiler.Capacity + " frames. Use 'slprobe status'.");
            }
            catch (Exception e)
            {
                Unsubscribe();
                _reportJob?.Dispose();
                _reportJob = null;
                Logger.Error("Enable failed, the probe is not running: " + e);
            }
        }

        /// <summary>
        /// Undoes this plugin's own subscriptions and job. LabAPI and the game never call it. The shared
        /// profiler and clock stay installed because other plugins may use them.
        /// Thread affinity: MAIN THREAD.
        /// </summary>
        // Nothing to flush: the CSV writer flushes per line, so no Shutdown subscription is needed.
        // scpsl-lint: ignore SL015
        public override void Disable()
        {
            Unsubscribe();
            _reportJob?.Dispose();
            _reportJob = null;
            Instance = null;
        }

        /// <summary>
        /// Absolute CSV path from <see cref="SlProbeConfig.CsvPath"/>; relative paths go under the plugin's
        /// config directory. <c>Assembly.Location</c> is empty for byte-loaded plugins, so it is not used.
        /// Thread affinity: MAIN THREAD (creates the config directory).
        /// </summary>
        /// <returns>The full path.</returns>
        public string ResolveCsvPath()
        {
            string configured = string.IsNullOrWhiteSpace(Config.CsvPath) ? "frames.csv" : Config.CsvPath.Trim();
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(this.GetConfigDirectory().FullName, configured);
        }

        private void Subscribe()
        {
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.RoundStarted += OnRoundStarted;
            ServerEvents.RoundEnded += OnRoundEnded;
            ServerEvents.RoundRestarted += OnRoundRestarted;
            ServerEvents.WaveRespawned += OnWaveRespawned;
            WarheadEvents.Detonated += OnWarheadDetonated;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;

            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.RoundEnded -= OnRoundEnded;
            ServerEvents.RoundRestarted -= OnRoundRestarted;
            ServerEvents.WaveRespawned -= OnWaveRespawned;
            WarheadEvents.Detonated -= OnWarheadDetonated;
            _subscribed = false;
        }

        // Runs once per report interval on the main thread; the Snapshot sorts the window, so it must
        // not be called more often than that.
        private void ReportTick()
        {
            FrameSnapshot snapshot = FrameProfiler.Snapshot();
            if (snapshot.Frames > 0)
                Logger.Info(ProbeFormat.OneLine(snapshot, ProbeFormat.IsIdle()));
        }

        // Marker handlers: Mark is a no-op while the CSV is off and a plain ConcurrentQueue enqueue
        // otherwise, so these cost nothing measurable. Config is read here, not cached, because
        // "labapi reload configs" replaces the object.
        private void Mark(string name)
        {
            if (Config.MarkRoundEvents)
                FrameProfiler.Mark(name);
        }

        private void OnWaitingForPlayers() => Mark("WaitingForPlayers");

        private void OnRoundStarted() => Mark("RoundStarted");

        private void OnRoundEnded(RoundEndedEventArgs ev) => Mark("RoundEnded");

        private void OnRoundRestarted() => Mark("RoundRestarted");

        private void OnWaveRespawned(WaveRespawnedEventArgs ev) => Mark("WaveRespawned");

        private void OnWarheadDetonated(WarheadDetonatedEventArgs ev) => Mark("WarheadDetonated");
    }
}
