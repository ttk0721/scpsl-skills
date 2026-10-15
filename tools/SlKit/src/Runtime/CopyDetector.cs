using System;
using System.Collections.Generic;
using System.Reflection;
using SlKit.Core;

namespace SlKit;

/// <summary>
/// Finds other copies of SlKit in the process: a second <c>SlKit.dll</c> (another version, or the
/// same file twice under <c>dependencies/</c>) or a plugin that compiled SlKit's sources in.
/// </summary>
/// <remarks>
/// Why it matters: LabAPI loads every DLL with <c>Assembly.Load(byte[])</c> into one AppDomain and
/// dedupes nothing, and SlKit's state is static per assembly. Two copies therefore run two clocks,
/// two main-thread queues each with its own per-frame budget, two sets of workers, two rate limiters,
/// silently. The check runs once, at the first <see cref="SlClock.Install"/>, and only logs.
/// Thread affinity: MAIN THREAD (called from <see cref="SlClock.Install"/>).
/// </remarks>
internal static class CopyDetector
{
    private const string SharedName = "SlKit";
    private const string MarkerTypeName = "SlKit.SlClock";

    /// <summary>This copy's assembly version, for log lines.</summary>
    internal static string SelfVersion
    {
        get
        {
            try
            {
                return typeof(CopyDetector).Assembly.GetName().Version?.ToString() ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }

    internal static void WarnIfOtherCopies()
    {
        try
        {
            Assembly self = typeof(CopyDetector).Assembly;
            List<SlKitCopy> others = new List<SlKitCopy>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == self)
                {
                    continue;
                }

                SlKitCopy? copy = Inspect(assembly);
                if (copy.HasValue)
                {
                    others.Add(copy.Value);
                }
            }

            AssemblyName selfName = self.GetName();
            SlKitCopy me = new SlKitCopy(
                selfName.Name,
                selfName.Version?.ToString(),
                LocationOf(self),
                string.Equals(selfName.Name, SharedName, StringComparison.Ordinal));

            string message = CopyReport.Describe(me, others);
            if (message != null)
            {
                SlLog.Warn(message);
            }
        }
        catch (Exception e)
        {
            SlLog.Warn("Could not check for other SlKit copies: " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static SlKitCopy? Inspect(Assembly assembly)
    {
        try
        {
            AssemblyName name = assembly.GetName();
            bool shared = string.Equals(name.Name, SharedName, StringComparison.Ordinal);

            // A source-included copy keeps SlKit's namespace unless the plugin renamed it; a renamed,
            // internal copy is the documented way to embed SlKit and is not reported.
            if (!shared && assembly.GetType(MarkerTypeName, false) == null)
            {
                return null;
            }

            return new SlKitCopy(name.Name, name.Version?.ToString(), LocationOf(assembly), shared);
        }
        catch (Exception)
        {
            // Dynamic or broken assemblies can throw from GetName/GetType; they are not SlKit.
            return null;
        }
    }

    private static string LocationOf(Assembly assembly)
    {
        try
        {
            // Empty for Assembly.Load(byte[]), which is how LabAPI loads plugins and dependencies,
            // so fall back to what the loader knows: a plugin's file path, or "a dependency".
            string location = assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                return location;
            }

            foreach (KeyValuePair<LabApi.Loader.Features.Plugins.Plugin, Assembly> entry in LabApi.Loader.PluginLoader.Plugins)
            {
                if (entry.Value == assembly && !string.IsNullOrEmpty(entry.Key.FilePath))
                {
                    return entry.Key.FilePath;
                }
            }

            if (LabApi.Loader.PluginLoader.Dependencies.Contains(assembly))
            {
                return "a DLL under LabAPI/dependencies (loaded from bytes, no path recorded)";
            }

            return string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
