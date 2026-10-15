// Fixture for SL007 on the main-thread contexts that are not event handlers: console / Remote Admin
// commands, SlKit scheduler callbacks, MainThread.Post, MEC delayed calls, and fully qualified spellings.
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommandSystem;
using MEC;
using SlKit;

namespace Fixture
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class SaveCommand : ICommand
    {
        public string Command => "save";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "saves";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            Thread.Sleep(5000); // expect: SL007
            File.WriteAllText("save.txt", "x"); // expect: SL007
            System.Threading.Thread.Sleep(10); // expect: SL007
            using (var wc = new System.Net.WebClient()) // expect: SL007
            {
            }
            Task.Run(() => Thread.Sleep(5000)); // fine: off the main thread
            response = "ok";
            return true;
        }
    }

    public sealed class RootCommand : ParentCommand
    {
        public override string Command => "root";
        public override string[] Aliases => Array.Empty<string>();
        public override string Description => "root";

        public override void LoadGeneratedCommands() { }

        protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            var text = File.ReadAllText("root.txt"); // expect: SL007
            response = text;
            return true;
        }
    }

    public sealed class Scheduler
    {
        private int[] players = new int[0];

        public void Enable()
        {
            SlClock.Every(TimeSpan.FromSeconds(5), () => File.AppendAllText("log.txt", "tick"), "log"); // expect: SL007
            SlClock.Every(TimeSpan.FromSeconds(5), Flush, "flush");
            SlClock.After(TimeSpan.FromSeconds(1), () => { Thread.Sleep(100); }, "after"); // expect: SL007
            SlClock.EveryStaggered(TimeSpan.FromSeconds(1), () => players, p => File.ReadAllText("p.txt"), "stagger"); // expect: SL007
            MainThread.Post(() => { File.WriteAllText("posted.txt", "x"); }); // expect: SL007
            Timing.CallDelayed(1f, () => Thread.Sleep(1)); // expect: SL007
            Background.TryRun(
                () => File.ReadAllText("work.txt"),
                text => File.WriteAllText("done.txt", text)); // expect: SL007

            // Not blocking the main thread: the work runs on a worker, or the callback does not block.
            Task.Run(() => Thread.Sleep(1000));
            SlClock.Every(TimeSpan.FromSeconds(5), () => counter++, "count");
        }

        private void Flush()
        {
            File.AppendAllText("flush.txt", "x"); // expect: SL007
        }

        // Called from nowhere the scanner can see: not a main-thread context.
        private void Unused()
        {
            File.AppendAllText("unused.txt", "x");
        }

        private int counter;
    }
}
