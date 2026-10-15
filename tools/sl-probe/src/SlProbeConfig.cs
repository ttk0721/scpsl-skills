using System.ComponentModel;

namespace SlProbe
{
    /// <summary>
    /// Configuration of the probe, stored by LabAPI as YAML in
    /// <c>configs/&lt;port&gt;/SlProbe/config.yml</c>.
    /// </summary>
    /// <remarks>
    /// LabAPI's serializer uses the underscored naming convention (<c>CsvEnabled</c> becomes
    /// <c>csv_enabled</c>), honours only public properties with a public setter, and rewrites the file on
    /// every load. <c>labapi reload configs</c> replaces the whole object, so the plugin reads values at
    /// use time instead of caching them. Settings that are applied once at startup say so in their
    /// description. Thread affinity: plain data, but LabAPI creates and fills it on the main thread.
    /// </remarks>
    public sealed class SlProbeConfig
    {
        /// <summary>
        /// Whether the per-second CSV log is written. Off by default because the file grows without
        /// bound (about 13 MB per day). Applied at startup only: changing it needs a server restart.
        /// </summary>
        [Description("Write one CSV line per second (frame period/busy percentiles, % slow frames, markers). Needs a restart to change.")]
        public bool CsvEnabled { get; set; } = false;

        /// <summary>
        /// CSV file path. A relative path is resolved against this plugin's config directory, so the
        /// default lands next to <c>config.yml</c>. Applied at startup only.
        /// </summary>
        [Description("CSV file. Relative paths are resolved against this plugin's config directory. Needs a restart to change.")]
        public string CsvPath { get; set; } = "frames.csv";

        /// <summary>
        /// Seconds between periodic report lines in the server log; 0 turns the periodic report off.
        /// Applied at startup only because the clock job is registered once.
        /// </summary>
        [Description("Seconds between report lines in the server log. 0 disables them. Needs a restart to change.")]
        public int ReportEverySeconds { get; set; } = 60;

        /// <summary>
        /// Number of most recent frames the statistics cover. 3600 is one minute at 60 FPS. A larger
        /// window gives a steadier p99 over a whole round but makes every report sort more samples
        /// (a few milliseconds at 36000, estimated, not measured). The window is fixed by whoever installs the profiler first.
        /// </summary>
        [Description("Frames kept for report/status (3600 = one minute at 60 FPS). Clamped to 300..100000. Needs a restart to change.")]
        public int WindowFrames { get; set; } = 3600;

        /// <summary>
        /// Whether round events (waiting for players, round start/end/restart, wave respawn, warhead
        /// detonation) are written into the CSV as markers. Read at event time, so a config reload applies.
        /// </summary>
        [Description("Mark round start/end, wave respawns and warhead detonation in the CSV (only has an effect when csv_enabled is true).")]
        public bool MarkRoundEvents { get; set; } = true;
    }
}
