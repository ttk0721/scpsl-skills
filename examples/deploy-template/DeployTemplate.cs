using System;
using System.ComponentModel;
using System.Reflection;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;

namespace Examples.DeployTemplate
{
    /// <summary>Config: public get/set properties only, defaults as initializers. The file is rewritten on every load.</summary>
    public sealed class DeployTemplateConfig
    {
        [Description("Kill switch: false makes Enable() return without subscribing anything.")]
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// What this build says about itself. The client id is baked in by the csproj
    /// (-p:Client=acme becomes an AssemblyMetadata attribute); the assembly name is what the
    /// loader and Logger show the operator.
    /// </summary>
    internal static class Branding
    {
        public static string Client
        {
            get
            {
                foreach (var attr in typeof(Branding).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                {
                    var meta = (AssemblyMetadataAttribute)attr;
                    if (meta.Key == "Client")
                        return meta.Value;
                }
                return "unknown";
            }
        }
    }

    /// <summary>Which LabAPI plugin folder a DLL path sits in. Pure string work, no game state.</summary>
    internal static class DeployScope
    {
        /// <summary>
        /// "global", a port number, or null when the path has no "plugins" segment. Reads the
        /// segment after the LAST "plugins", so a DLL in a sub-folder (plugins/7777/sub/x.dll,
        /// which the loader also scans) still reports its scope.
        /// </summary>
        public static string FromFilePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            var parts = filePath.Split('/', '\\');
            for (var i = parts.Length - 2; i >= 0; i--)
            {
                if (string.Equals(parts[i], "plugins", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1];
            }
            return null;
        }
    }

    /// <summary>
    /// Template plugin for the deploy workflow. It does almost nothing on purpose: the point is the
    /// build (csproj, branding) and the first log line, which tells whoever deploys it exactly which
    /// DLL, which build and which folder the loader enabled.
    /// </summary>
    public sealed class DeployTemplatePlugin : Plugin<DeployTemplateConfig>
    {
        private bool _subscribed;

        // Constant, unique, path-safe: it is the config directory name. Variants built with different
        // -p:Client values keep this Name on purpose (one config per server, whichever variant runs).
        // Give each variant its own Name only if they must NOT share a config, and never change it
        // after release or the config looks reset.
        public override string Name => "DeployTemplate";
        public override string Description => "Template: one DLL, branded per client, verifiable after boot.";
        public override string Author => "scpsl-skills";

        // A literal: LabApiProperties.CompiledVersion is computed from the RUNNING LabApi.dll,
        // so using it makes the version check unable to ever fire.
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public override void Enable()
        {
            // An empty config.yml deserialises to null.
            Config ??= new DeployTemplateConfig();
            if (!Config.IsEnabled || _subscribed)
                return;

            // The boot-log line a deploy check greps for. Logger prefixes it with the calling assembly's
            // name, so a branded build prints [DeployTemplate.acme]. FilePath is set after the
            // constructor, so it is valid here and not before. Assembly.Location is "" for plugins.
            Logger.Info("build=" + Branding.Client
                + " scope=" + (DeployScope.FromFilePath(FilePath) ?? "unknown")
                + " port=" + Server.Port
                + " file=" + FilePath);

            // Fallible work above this line, subscriptions below it (no rollback if Enable() throws later).
            ServerEvents.Shutdown += OnShutdown;
            _subscribed = true;
        }

        // Never called by LabAPI or the game. Kept as a harmless mirror of Enable().
        public override void Disable() => Unsubscribe();

        private void OnShutdown()
        {
            // The only reliable last call. Flush synchronously here; the process exits right after.
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;
            ServerEvents.Shutdown -= OnShutdown;
            _subscribed = false;
        }
    }
}
