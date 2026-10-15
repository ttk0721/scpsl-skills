using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommandSystem;
using LabApi.Features.Console;
using SlKit;
using SlKit.Core;

namespace SlProbe
{
    /// <summary>
    /// <c>slprobe status|report|mark &lt;name&gt;|reset</c>, registered for Remote Admin and the server console.
    /// </summary>
    /// <remarks>
    /// One class with two <c>[CommandHandler]</c> attributes gives LabAPI two instances, so all state is static.
    /// Both handlers run on the main thread (RA inside the transport's LateUpdate drain, the console inside
    /// <c>ServerConsole.FixedUpdate</c>), which is what <see cref="FrameProfiler.Snapshot"/> requires.
    /// Permission: the game flag <see cref="PlayerPermissions.ServerConsoleCommands"/>, checked first, because
    /// the command can clear the window and write markers. The server console always passes. There is no dot
    /// (client console) variant on purpose: it would be reachable by every connected player.
    /// The name is namespaced on purpose: LabAPI skips a command whose name already exists in the handler
    /// without logging anything.
    /// </remarks>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public sealed class SlProbeCommand : ICommand, IUsageProvider
    {
        private const int MaxMarkLength = 64;
        private static readonly long ReportCooldown = Stopwatch.Frequency / 2;
        private static long _lastReport;

        /// <summary>Command word.</summary>
        public string Command => "slprobe";

        /// <summary>No aliases. Never null: LabAPI's sub-command listing joins them and throws on null.</summary>
        public string[] Aliases => Array.Empty<string>();

        /// <summary>Shown in the RA command list, which cuts descriptions at 80 characters.</summary>
        public string Description => "Frame-time probe: status | report | mark <name> | reset";

        /// <summary>Usage hint for RA autocomplete.</summary>
        public string[] Usage => new[] { "status|report|mark|reset", "name (for mark)" };

        /// <summary>
        /// Runs one subcommand. Never throws: an exception would show its stack trace to the sender, so
        /// failures are logged and answered with one short line.
        /// Thread affinity: MAIN THREAD.
        /// </summary>
        /// <param name="arguments">Arguments without the command word.</param>
        /// <param name="sender">RA user or server console.</param>
        /// <param name="response">Reply text, always non-empty (RA and console print nothing for an empty reply).</param>
        /// <returns>True on success; selects the reply colour.</returns>
        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(PlayerPermissions.ServerConsoleCommands, out response))
                return false;

            try
            {
                if (!MainThread.IsMainThread)
                {
                    response = "slprobe must run on the main thread.";
                    return false;
                }

                if (SlProbePlugin.Instance == null)
                {
                    response = "The probe is not running (Enable failed, see the server log).";
                    return false;
                }

                if (arguments.Count == 0)
                {
                    response = UsageLine();
                    return false;
                }

                string sub = arguments.Array[arguments.Offset];
                if (Is(sub, "status"))
                    return Status(out response);

                if (Is(sub, "report"))
                    return Report(out response);

                if (Is(sub, "reset"))
                    return Reset(out response);

                if (Is(sub, "mark"))
                    return Mark(arguments, out response);

                response = "Unknown subcommand '" + sub + "'. " + UsageLine();
                return false;
            }
            catch (Exception e)
            {
                Logger.Error("slprobe failed: " + e);
                response = "slprobe failed, see the server log.";
                return false;
            }
        }

        private static bool Is(string value, string word) => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);

        private static string UsageLine() => "Usage: slprobe status | report | mark <name> | reset";

        private static bool Report(out string response)
        {
            // The snapshot sorts the whole window; a macro that spams the command would turn the probe
            // into the load it is meant to measure.
            long now = Stopwatch.GetTimestamp();
            if (_lastReport != 0 && now - _lastReport < ReportCooldown)
            {
                response = "Wait half a second between reports.";
                return false;
            }

            _lastReport = now;
            response = ProbeFormat.Report(FrameProfiler.Snapshot(), ProbeFormat.IsIdle());
            return true;
        }

        private static bool Reset(out string response)
        {
            FrameProfiler.ResetWindow();
            FrameProfiler.Mark("reset");
            response = "Window cleared. Statistics now cover frames recorded from this moment on.";
            return true;
        }

        private static bool Mark(ArraySegment<string> arguments, out string response)
        {
            if (arguments.Count < 2)
            {
                response = "Usage: slprobe mark <name>";
                return false;
            }

            StringBuilder name = new StringBuilder(MaxMarkLength);
            for (int i = 1; i < arguments.Count && name.Length < MaxMarkLength; i++)
            {
                if (i > 1)
                    name.Append(' ');

                name.Append(arguments.Array[arguments.Offset + i]);
            }

            if (name.Length > MaxMarkLength)
                name.Length = MaxMarkLength;

            if (FrameProfiler.CsvPath == null)
            {
                response = "The CSV sink is off (csv_enabled: false), so the marker was not recorded.";
                return false;
            }

            FrameProfiler.Mark(name.ToString());
            response = "Marked '" + name + "' in the CSV (appears in the next one-second line).";
            return true;
        }

        private static bool Status(out string response)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(512);
            sb.Append("SlProbe ").Append(SlProbePlugin.Instance.Version).AppendLine();
            sb.Append("profiler: ").Append(FrameProfiler.IsInstalled ? "installed" : "NOT installed")
              .Append(", window ").Append(FrameProfiler.Capacity.ToString(c)).Append(" frames, ")
              .Append(FrameProfiler.FrameCount.ToString(c)).Append(" frames recorded since install").AppendLine();
            sb.Append("state: ").Append(ProbeFormat.IsIdle() ? "IDLE MODE (statistics are not usable)" : "active").AppendLine();

            string csv = FrameProfiler.CsvPath;
            sb.Append("csv: ").Append(csv ?? "off");
            if (csv != null)
                sb.Append(" (dropped seconds: ").Append(FrameProfiler.CsvDroppedSeconds.ToString(c)).Append(')');
            sb.AppendLine();

            RunningStats cost = SlClock.FrameCost;
            sb.Append("clock: ").Append(SlClock.JobCount.ToString(c)).Append(" jobs, own cost avg ")
              .Append(cost.AverageMicroseconds.ToString("0.0", c)).Append(" us max ")
              .Append(cost.MaxMicroseconds.ToString("0.0", c)).Append(" us per frame").AppendLine();

            foreach (ClockJobStats job in SlClock.Stats)
            {
                if (job.Name != null && job.Name.StartsWith("slprobe.", StringComparison.Ordinal))
                    sb.Append("job ").Append(job.ToString()).AppendLine();
            }

            sb.Append("main thread queue: ").Append(MainThread.PendingCount.ToString(c)).Append(" pending, ")
              .Append(MainThread.FailureCount.ToString(c)).Append(" failed actions");
            response = sb.ToString();
            return true;
        }
    }
}
