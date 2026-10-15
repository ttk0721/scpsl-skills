using System.IO;
using System.Reflection;
using LabApi.Loader.Features.Plugins;

namespace Fixture
{
    public class LocationPlugin : Plugin
    {
        public override string Name => "x";

        public override void Enable()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); // expect: SL013
            string url = Assembly.GetExecutingAssembly().CodeBase; // expect: SL013
            string a = typeof(LocationPlugin).Assembly.Location; // expect: SL013
            string b = GetType().Assembly.Location; // expect: SL013
            string ok = FilePath;
            string game = typeof(UnityEngine.Object).Assembly.Location; // a game assembly is loaded from disk
            var name = Assembly.GetExecutingAssembly().GetName().Name;
            // Assembly.GetExecutingAssembly().Location
        }

        public override void Disable() { }
    }
}
