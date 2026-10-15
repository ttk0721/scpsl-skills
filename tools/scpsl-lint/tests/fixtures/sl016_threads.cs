using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace Fixture
{
    public class OffThread
    {
        public void Bad(Player p)
        {
            Task.Run(() =>
            {
                var n = Player.List.Count; // expect: SL016
                p.SendHint("x", 1f);
            });

            var t = new Thread(() => Server.SendBroadcast("hi", 1)); // expect: SL016
            t.Start();

            ThreadPool.QueueUserWorkItem(_ => { var pos = gameObject.transform.position; }); // expect: SL016

            Task.Run(() => NetworkServer.active.ToString()); // expect: SL016

            new Thread(Worker).Start();
        }

        // Method group handed to a thread: the method body is off-thread code.
        private void Worker()
        {
            var name = Player.Get(1).Nickname; // expect: SL016
        }

        public void Good(Player p)
        {
            string nick = p.Nickname;                 // snapshot on the main thread
            Task.Run(() =>
            {
                string upper = nick.ToUpperInvariant();
                Logger.Info(upper);                    // Logger is thread-safe
                MainThread.Post(() => Player.List.Count.ToString()); // marshalled back
                MainThreadDispatcher.Dispatch(() => Server.SendBroadcast(upper, 1));
            });

            Background.TryRun(() => nick.Length, len => Server.SendBroadcast(len.ToString(), 1));
        }

        public void Bad2()
        {
            Background.TryRun(() => Player.List.Count); // expect: SL016
        }

        private void Worker2()
        {
            // Not handed to any thread: no finding.
            var p = Player.List;
        }
    }
}
