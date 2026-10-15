# Frame probe: code, columns, server state

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Two routes: SlKit's `FrameProfiler` (ready, tested) or the hand-rolled probe below (for a plugin that cannot take a dependency). The `sl-probe` plugin in this repository's `tools/` wraps `FrameProfiler` as a drop-in plugin: `slprobe status|report|mark <name>|reset` in Remote Admin or the console, and `csv_enabled`, `csv_path`, `window_frames` in its config. It is verified on 14.2.7. After installing it, check `slprobe status` first.

## Route A: SlKit `FrameProfiler`

```csharp
public override void Enable()
{
    FrameProfiler.Install();                         // idempotent; first entry in Initialization, last in PostLateUpdate
    FrameProfiler.StartCsv("/var/log/scpsl/frames.csv");   // one line per second, written off-thread
}

// event markers, from any thread
ServerEvents.RoundStarted += () => FrameProfiler.Mark("round-start");
ServerEvents.WaitingForPlayers += () => FrameProfiler.Mark("waiting");
```

The CSV has: UTC, frames, fps, period p50/p95/p99/max, busy p50/p95/p99/max, % over 50 ms, % over 100 ms, Mirror tick rate, Mirror full-update ms, `Server.Tps`, `targetFrameRate`, marks. `Snapshot()` returns the same over the last N frames (default 3600). It sorts two arrays, so call it on demand.

## Route B: minimal hand-rolled probe

Insert into the CURRENT loop, with marker types, and copy-resize the subsystem arrays. A default or hand-built loop drops Mirror's hooks and with them all networking.

```csharp
using System;
using System.Diagnostics;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

public static class FrameProbe
{
    private struct BeginMark { }
    private struct EndMark { }

    private const int Capacity = 4096;                       // power of two
    private static readonly double[] Period = new double[Capacity];
    private static readonly double[] Busy = new double[Capacity];
    private static long _frames, _begin, _prevBegin;
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    public static void Install()                             // main thread
    {
        _prevBegin = 0;                                      // no bogus first period after a reinstall
        PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
        Add(ref root, typeof(Initialization), typeof(BeginMark), OnBegin, atStart: true);
        Add(ref root, typeof(PostLateUpdate), typeof(EndMark), OnEnd, atStart: false);
        PlayerLoop.SetPlayerLoop(root);
    }

    public static void Uninstall()                           // optional, from ServerEvents.Shutdown
    {
        PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
        Strip(ref root, typeof(BeginMark));
        Strip(ref root, typeof(EndMark));
        PlayerLoop.SetPlayerLoop(root);
    }

    // Copy the newest n samples (oldest first). Main thread only; sort the copy elsewhere.
    public static int CopyLast(double[] period, double[] busy)
    {
        int n = (int)Math.Min(Math.Min(period.Length, busy.Length), Math.Min(_frames, Capacity));
        for (int i = 0; i < n; i++)
        {
            long idx = (_frames - n + i) & (Capacity - 1);
            period[i] = Period[idx];
            busy[i] = Busy[idx];
        }
        return n;
    }

    private static void OnBegin()
    {
        long now = Stopwatch.GetTimestamp();
        if (_prevBegin != 0) Period[_frames & (Capacity - 1)] = (now - _prevBegin) * MsPerTick;
        _prevBegin = _begin = now;
    }

    private static void OnEnd()
    {
        Busy[_frames & (Capacity - 1)] = (Stopwatch.GetTimestamp() - _begin) * MsPerTick;
        _frames++;
    }

    private static bool Add(ref PlayerLoopSystem node, Type phase, Type marker, PlayerLoopSystem.UpdateFunction fn, bool atStart)
    {
        if (node.type == phase)
        {
            PlayerLoopSystem[] old = node.subSystemList ?? new PlayerLoopSystem[0];
            foreach (PlayerLoopSystem s in old)
                if (s.type == marker) return true;           // already installed
            var list = new PlayerLoopSystem[old.Length + 1];
            Array.Copy(old, 0, list, atStart ? 1 : 0, old.Length);
            list[atStart ? 0 : old.Length] = new PlayerLoopSystem { type = marker, updateDelegate = fn };
            node.subSystemList = list;
            return true;
        }
        if (node.subSystemList == null) return false;
        for (int i = 0; i < node.subSystemList.Length; i++)
            if (Add(ref node.subSystemList[i], phase, marker, fn, atStart)) return true;
        return false;
    }

    private static void Strip(ref PlayerLoopSystem node, Type marker)
    {
        if (node.subSystemList == null) return;
        node.subSystemList = Array.FindAll(node.subSystemList, s => s.type != marker);
        for (int i = 0; i < node.subSystemList.Length; i++) Strip(ref node.subSystemList[i], marker);
    }
}
```

Once per second: `CopyLast` on the main thread into pooled arrays, then sort, take nearest-rank percentiles and write the CSV line on a worker thread (plain `double[]` data, safe off-thread). Never format strings or touch files on the main thread. For the percentile of a sorted array of n values use index `ceil(p * n) - 1`.

At install, log the top-level phase names once (a single `Logger.Info`) so a future build's loop shape is on record.

## Extra markers worth adding

- **FixedUpdate phase:** a counter system that increments per call, reset at frame start. Gives physics steps per frame (0-1 at 60 FPS, 2-3 at 20 FPS).
- **PreLateUpdate:** a begin/end pair around it. Transport packet handlers (most game commands and the LabAPI events they raise) and position distribution run there.
- **Mirror late update** (`Broadcast`, transport flush): read `NetworkServer.lateUpdateDuration.average` rather than adding a marker. Like `fullUpdateDuration` it is an average.
- Prefer a few markers over many. Each is one native-to-managed call per frame, which is cheap but not free.

## Server state to log at 1 Hz next to the frame stats

| Field | Why |
|---|---|
| `Player.ReadyList` count, dummies counted separately | Bucket key for A/B |
| `Application.targetFrameRate`, `NetworkManager.singleton.sendRate` | Detect the tickrate override |
| `Server.IdleModeActive` | Drop idle windows |
| `NetworkServer.spawned.Count` | Network object count, a major driver of Broadcast cost (unmeasured on real maps) |
| `NetworkServer.connections.Count` | Connection count |
| `GC.CollectionCount(0)` delta, `GC.GetTotalMemory(false)` | Correlate hitches with GC |
| Round phase, round number, process uptime | Separate first-round JIT cost and warm-up |
| Active A/B arm id | Needed for the comparison |
| Marks | Round start, waves, detonation, decontamination, schematic loads, mass join |

## Probe hygiene

- Behaviour-neutral: reads only. Do not change `Server.MaxTps` or anything else from a measuring plugin.
- Own cost visible: with SlKit, `SlClock.FrameCost` and `SlClock.Stats` report the clock's and each job's microseconds.
- No `Logger` or `Debug.Log` per frame. A log line costs a stack walk plus regexes, and off-thread `Debug.Log` is re-logged on the main thread.
- Drop and count CSV seconds if the writer lags, never block.
- `Disable()` is never called and the loop survives scene loads, so entries live until process exit. Call `Uninstall` (SlKit: `FrameProfiler.Uninstall`, which also stops the CSV) from `ServerEvents.Shutdown` if you want a clean exit.

## Re-verifying on a new build

- `PlayerLoop.GetCurrentPlayerLoop()` still contains the Mirror hooks (log names at install; confirm Mirror's early and late systems are present after your insert).
- Phase types `Initialization`, `PostLateUpdate`, `FixedUpdate` still exist in `UnityEngine.PlayerLoop` in the shipped UnityEngine.CoreModule.
- Sanity-test the probe: inject a known 100 ms `Thread.Sleep` from a test plugin once and confirm p99/max and `% > 100 ms` show it while `Server.Tps` does not.
- SlKit: rebuild against the new `SCPSL_Data/Managed` and run its tests.
