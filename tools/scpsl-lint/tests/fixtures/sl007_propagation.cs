// Fixture for SL007 intra-file propagation: a same-file method called from a main-thread context
// (directly or through up to three levels of same-file calls) is itself a main-thread context.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommandSystem;
using LabApi.Events.Handlers;
using SlKit;

namespace Fixture
{
    public sealed class SaveCommand : ICommand
    {
        public string Command => "save";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "saves";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            // Two blocking calls on one line: both are reported (checked by a dedicated unit test).
            File.WriteAllText("a.txt", "x"); File.ReadAllText("a.txt"); // expect: SL007
            Level1();
            this.ThisCall();
            SaveCommand.Qualified();
            Task.Run(() => OffThreadOnly());          // not followed: runs on a worker
            Task.Run(PumpGroup);                      // method group handed to a worker
            new Thread(Pump).Start();
            Background.TryRun(() => LoadFromDisk(), text => { }, ex => { });
            other.Foreign();                          // unknown receiver: not followed
            response = "ok";
            return true;
        }

        private void Level1() { Level2(); }
        private void Level2() { Level3(); }
        private void Level3() { Thread.Sleep(10); Level4(); } // expect: SL007  (third level from Execute)
        private void Level4() { Thread.Sleep(10); }           // fourth level: not followed, not reported
        private void ThisCall() { File.ReadAllText("t.txt"); } // expect: SL007
        private static void Qualified() { File.ReadAllText("q.txt"); } // expect: SL007

        private void OffThreadOnly() { Thread.Sleep(10); }
        private void PumpGroup() { Thread.Sleep(10); }
        private void Pump() { Thread.Sleep(10); }
        private string LoadFromDisk() { return File.ReadAllText("l.txt"); }

        private async Task SaveAsync()
        {
            await File.WriteAllTextAsync("async.txt", "x");   // not blocking
        }

        private Other other;
    }

    public sealed class Other
    {
        // Same name as nothing in SaveCommand's call graph; never reached from a main-thread context.
        public void Foreign() { Thread.Sleep(10); }
    }

    public sealed class Plugin
    {
        public void Enable()
        {
            PlayerEvents.Joined += OnJoined;
            SlClock.Every(TimeSpan.FromSeconds(5), () => Tick(), "tick");
        }

        private void OnJoined(object ev) { Persist(); }
        private void Persist() { File.AppendAllText("joined.txt", "x"); } // expect: SL007

        public void Update() { Poll(); }
        private void Poll() { Thread.Sleep(1); } // expect: SL007

        private void Tick() { File.AppendAllText("tick.txt", "x"); } // expect: SL007

        // Declared as a local function but never called: not a main-thread context.
        public void Declared()
        {
            void NeverCalled() { Thread.Sleep(1); }
        }

        // Called from nowhere the scanner can see.
        private void Orphan() { Thread.Sleep(1); }

        public void Reload() { Orphan2(); }
        private void Orphan2() { Thread.Sleep(1); }
    }
}
