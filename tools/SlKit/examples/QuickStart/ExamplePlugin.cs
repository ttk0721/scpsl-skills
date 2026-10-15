using System;
using System.Collections.Generic;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using SlKit;

/// <summary>The README quick start: SlClock.Every + EveryStaggered + Background.ForOwner + MainThread.Post.</summary>
public sealed class ExamplePlugin : Plugin
{
    /// <inheritdoc />
    public override string Name => "Example";

    /// <inheritdoc />
    public override string Description => "SlKit quick start";

    /// <inheritdoc />
    public override string Author => "you";

    /// <inheritdoc />
    public override Version RequiredApiVersion => new Version(1, 1, 7);

    // Main-thread scratch lists, reused: steady state allocates nothing.
    private readonly List<Player> _players = new List<Player>(64);
    private readonly List<string> _names = new List<string>(64);

    // This plugin's own worker thread and queue: another plugin's slow IO cannot delay it.
    private BackgroundWorker _worker;

    /// <inheritdoc />
    public override void Enable()
    {
        SlClock.Install();          // main thread: insert the clock entry; Round jobs end at RoundRestarted
        _worker = Background.ForOwner(Name, capacity: 64);

        // Wall-clock heartbeat. Unlike MEC Update-segment waits, idle mode's timeScale 0.01
        // does not stretch it.
        SlClock.Every(TimeSpan.FromSeconds(30), Heartbeat, "example.heartbeat");

        // One hint per player per second, spread over the frames (1 player/frame at 60 players, 60 fps).
        SlClock.EveryStaggered<Player>(TimeSpan.FromSeconds(1), RefillPlayers, ShowHint, "example.hints");
    }

    /// <inheritdoc />
    public override void Disable() { } // never called by LabAPI or the game; flush in ServerEvents.Shutdown

    private void Heartbeat()
    {
        // 1) Snapshot on the main thread: plain strings, no wrappers leave this thread.
        _names.Clear();
        foreach (Player p in Player.List)
        {
            if (p.IsReady && !p.IsHost)
            {
                _names.Add(p.Nickname);
            }
        }

        string[] snapshot = _names.ToArray();

        // 2) Work off-thread on the snapshot only; 3) result comes back on the main thread.
        bool queued = _worker.TryRun(
            () => string.Join(", ", snapshot),                       // worker thread
            text => Logger.Info("Online: " + text),                  // main thread
            error => Logger.Warn("Heartbeat failed: " + error.Message));
        if (!queued)
        {
            Logger.Warn("Worker queue full; heartbeat skipped");
        }

        // Any thread may hand work to the main thread:
        MainThread.Post(() => Logger.Info("Clock cost avg: " + SlClock.FrameCost.AverageMicroseconds + " us"));
    }

    private IReadOnlyList<Player> RefillPlayers()
    {
        // Called once per period, after the previous period's items are done, so refilling is safe.
        _players.Clear();
        foreach (Player p in Player.List)
        {
            if (p.IsReady && !p.IsHost)
            {
                _players.Add(p);
            }
        }

        return _players;
    }

    private static void ShowHint(Player p)
    {
        if (p.IsDestroyed)
        {
            return;  // the list is a snapshot; players can leave mid-period
        }

        p.SendHint("Hello " + p.Nickname, 1.2f);
    }
}
