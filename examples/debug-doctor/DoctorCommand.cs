using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using CommandSystem;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;

namespace Examples.DebugDoctor
{
    /// <summary>
    /// <c>sldoctor plugins | subs &lt;Class.Event&gt; | count &lt;Class.Event&gt; [seconds] [force]</c>, for Remote Admin and the
    /// server console. One class with two attributes gives LabAPI two instances, so state lives in statics.
    /// Both handlers run on the main thread. The name is namespaced: LabAPI skips a duplicate command name
    /// without logging anything.
    /// </summary>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public sealed class DoctorCommand : ICommand, IUsageProvider
    {
        public string Command => "sldoctor";

        // Never null: LabAPI joins the aliases when it lists commands.
        public string[] Aliases => Array.Empty<string>();

        public string Description => "Diagnostics: plugins | subs <Class.Event> | count <Class.Event> [seconds] [force]";

        public string[] Usage => new[] { "plugins|subs|count", "Class.Event", "seconds" };

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            // Game flag, checked first. The server console always passes.
            if (!sender.CheckPermission(PlayerPermissions.ServerConsoleCommands, out response))
                return false;

            try
            {
                if (arguments.Count == 0)
                {
                    response = Description;
                    return false;
                }

                string sub = arguments.At(0);
                if (string.Equals(sub, "plugins", StringComparison.OrdinalIgnoreCase))
                    return Plugins(out response);

                if (string.Equals(sub, "subs", StringComparison.OrdinalIgnoreCase))
                    return Subs(arguments, out response);

                if (string.Equals(sub, "count", StringComparison.OrdinalIgnoreCase))
                    return Count(arguments, out response);

                response = "Unknown subcommand '" + sub + "'. " + Description;
                return false;
            }
            catch (Exception e)
            {
                // An exception would send its stack trace to the sender. Log it, answer with one line.
                Logger.Error("DebugDoctor command failed: " + e);
                response = "sldoctor failed, see the server log.";
                return false;
            }
        }

        /// <summary>
        /// Loader state: what LabAPI instantiated, what it enabled, and what is in neither list.
        /// A plugin that is absent here was never instantiated (wrong folder, missing dependency, no Plugin
        /// subclass). A plugin that is listed but not enabled was skipped or threw in Enable. Plugins that EXILED
        /// loaded itself (its own folder) are not in <c>PluginLoader.Plugins</c>, so their absence here proves nothing.
        /// </summary>
        private static bool Plugins(out string response)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<string, int> names = new Dictionary<string, int>(StringComparer.Ordinal);

            sb.AppendLine("LabAPI plugins (instantiated: " + PluginLoader.Plugins.Count
                + ", enabled: " + PluginLoader.EnabledPlugins.Count
                + ", dependencies: " + PluginLoader.Dependencies.Count + ")");

            foreach (KeyValuePair<Plugin, Assembly> pair in PluginLoader.Plugins)
            {
                Plugin p = pair.Key;
                bool enabled = PluginLoader.EnabledPlugins.Contains(p);
                names.TryGetValue(p.Name, out int seen);
                names[p.Name] = seen + 1;

                sb.Append(enabled ? "  ENABLED  " : "  NOT ENABLED  ")
                    .Append(p.Name).Append(' ').Append(p.Version)
                    .Append(" prio=").Append(p.Priority)
                    .Append(" file=").AppendLine(p.FilePath);
            }

            foreach (KeyValuePair<string, int> n in names)
            {
                if (n.Value > 1)
                {
                    sb.AppendLine("  DUPLICATE NAME '" + n.Key + "' x" + n.Value
                        + ": the copies share one config folder and both run (look for a stray copy or backup .dll).");
                }
            }

            sb.Append("Not listed? Read the boot log for \"Couldn't load the plugin\" and check the folder and port. "
                + "Listed but NOT ENABLED? Look for \"Couldn't enable\" or is_enabled: false in properties.yml. "
                + "EXILED plugins, and LabAPI plugins that EXILED loaded from its own folder, are not in this list: EXILED keeps its own.");

            response = sb.ToString();
            return true;
        }

        private static bool Subs(ArraySegment<string> arguments, out string response)
        {
            if (arguments.Count < 2)
            {
                response = "Usage: sldoctor subs <Class.Event>, for example sldoctor subs PlayerEvents.Hurting";
                return false;
            }

            string name = arguments.At(1);
            if (!EventLookup.TryResolve(name, out _, out FieldInfo field, out string error))
            {
                response = error;
                return false;
            }

            IReadOnlyList<string> lines = EventLookup.Subscribers(field);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(name + ": " + lines.Count + " subscriber(s), in the order they run");
            foreach (string line in lines)
                sb.AppendLine("  " + line);

            if (lines.Count == 0)
                sb.Append("  none: nothing is subscribed, so no handler of yours is registered either.");

            response = sb.ToString();
            return true;
        }

        private static bool Count(ArraySegment<string> arguments, out string response)
        {
            if (arguments.Count < 2)
            {
                response = "Usage: sldoctor count <Class.Event> [seconds] [force]";
                return false;
            }

            int seconds = 30;
            bool force = false;
            for (int i = 2; i < arguments.Count; i++)
            {
                string a = arguments.At(i);
                if (string.Equals(a, "force", StringComparison.OrdinalIgnoreCase))
                    force = true;
                else if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                    || seconds < 1 || seconds > EventProbe.MaxSeconds)
                {
                    response = "Seconds must be 1.." + EventProbe.MaxSeconds + ", or the word force.";
                    return false;
                }
            }

            return EventProbe.Start(arguments.At(1), seconds, force, out response);
        }
    }
}
