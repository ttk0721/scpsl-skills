using System;

namespace Fixture
{
    public class Misc
    {
        public void Enable()
        {
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance; // expect: SL009
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        }

        public void Disable()
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        public void Cleanup()
        {
            GC.Collect(); // expect: SL010
            System.GC.Collect(2); // expect: SL010
            GC.SuppressFinalize(this);
            // GC.Collect();
            string s = "GC.Collect()";
        }

        private void OnFirstChance(object sender, System.Runtime.ExceptionServices.FirstChanceEventArgs e) { }
        private void OnUnhandled(object sender, UnhandledExceptionEventArgs e) { }
    }
}
