using MEC;

namespace Fixture
{
    public class Killer
    {
        private CoroutineHandle handle;

        public void StopAll()
        {
            Timing.KillCoroutines(); // expect: SL006
            MEC.Timing.KillCoroutines (); // expect: SL006
        }

        public void StopOwn()
        {
            Timing.KillCoroutines(handle);
            Timing.KillCoroutines("my-plugin");
            // Timing.KillCoroutines();
            string s = "Timing.KillCoroutines()";
        }
    }
}
