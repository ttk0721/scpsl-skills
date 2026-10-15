using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Events.Handlers;

namespace Fixture
{
    public class Blocking : CustomEventsHandler
    {
        private static readonly HttpClient Client = new HttpClient();

        public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            string body = Client.GetStringAsync("http://example.invalid/").Result; // expect: SL007
            Task<string> pending = Client.GetStringAsync("http://example.invalid/");
            pending.Wait(); // expect: SL007
            var text = pending.GetAwaiter().GetResult(); // expect: SL007
            using (var wc = new WebClient()) // expect: SL007
            {
            }
            Thread.Sleep(100); // expect: SL007
            File.WriteAllText("a.txt", "x"); // expect: SL007
            var req = (HttpWebRequest)WebRequest.Create("http://example.invalid/");
            var resp = req.GetResponse(); // expect: SL007
        }

        public void Enable()
        {
            PlayerEvents.Left += ev =>
            {
                var all = File.ReadAllText("state.json"); // expect: SL007
            };
        }

        // Update-like code
        private void Update()
        {
            File.AppendAllText("log.txt", "tick"); // expect: SL007
        }

        // MEC coroutines run on the main thread too
        private IEnumerator<float> Loop()
        {
            while (true)
            {
                Thread.Sleep(5); // expect: SL007
                yield return Timing.WaitForSeconds(1f);
            }
        }

        // Not on the main thread, or not in a hot path: no findings.
        public void Enable2()
        {
            var cfg = File.ReadAllText("config.yml");
            Task.Run(() =>
            {
                Thread.Sleep(1000);
                var t = Client.GetStringAsync("http://example.invalid/").Result;
            });
        }

        public override void OnPlayerLeft(PlayerLeftEventArgs ev)
        {
            Task.Run(() => File.WriteAllText("left.txt", "x"));
            var ok = ev.Result; // a property literally named Result on event args is not a Task
        }
    }
}
