using System;
using System.IO;
using LabApi.Loader.Features.Plugins;

namespace Fixture
{
    // Negative fixture for SL013: reading Assembly.Location is harmless (it is ""), only feeding "" to
    // Path.* throws. Calibrated against a real plugin that guards the value and hands it to a helper.
    public class GuardedLocationPlugin : Plugin
    {
        public override string Name => "x";

        public override void Enable()
        {
            var asmLoc = typeof(GuardedLocationPlugin).Assembly.Location;
            var baseDir = string.IsNullOrEmpty(asmLoc) ? null : Path.GetDirectoryName(asmLoc);
            if (string.IsNullOrEmpty(baseDir)) baseDir = AppDomain.CurrentDomain.BaseDirectory;

            Helper.Apply(typeof(GuardedLocationPlugin).Assembly.Location, s => { });
            Helper.Stage("x", typeof(GuardedLocationPlugin).Assembly.Location);
        }

        public override void Disable() { }
    }

    internal static class Helper
    {
        public static void Apply(string location, Action<string> log) { }

        public static void Stage(string file, string location) { }
    }
}
