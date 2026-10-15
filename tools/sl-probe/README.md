# sl-probe

A LabAPI plugin that measures how long the server's frames really take. Use it to answer one
question: did this plugin make my server slower?

It is built on [`SlKit.FrameProfiler`](../SlKit/README.md) and targets **LabAPI 1.1.7 / game 14.2.7**.

> Status: built against the 14.2.7 server assemblies and verified on a live 14.2.7 server.
> After
> installing it, run `slprobe status` and check the log for the enable line.

## Why not `Server.Tps`

`Server.Tps` (and the `tps` style numbers built on it) is `round(1 / Time.smoothDeltaTime)`:

- `smoothDeltaTime` is a low-pass filtered value, so a single long frame is averaged away.
- Unity clamps the frame delta at `maximumDeltaTime`, so a freeze is reported as a shorter frame
  than it was.
- It is scaled by `Time.timeScale`, which is 0.01 in idle mode.
- It is an integer, so 59.6 and 60.4 look the same.

A server that stalls for 300 ms every few seconds can still print 60. Players feel those stalls as
rubber-banding. Mirror's own counters are no better: `actualTickRate` is a whole-number frame
count per second and `fullUpdateDuration` is a moving average.

sl-probe records the wall-clock **period** (start of one frame to the start of the next) and the
**busy time** (start of the frame to the end of it) of every frame with `Stopwatch`, at the
PlayerLoop boundaries. It keeps the last N frames and reports p50/p95/p99/max and the share of
frames slower than 50 ms and 100 ms. `Server.Tps` is still printed next to those numbers, only so
you can see how far apart they are.

## Install

1. Build both projects:

   ```
   dotnet build tools/sl-probe/src/SlProbe.csproj -c Release
   dotnet build tools/sl-probe/src/SlProbe.csproj -c Release -p:SL_REFERENCES="/path/to/SCPSL_Data/Managed"
   ```

   (The second line is the same build with the game's `Managed` folder given explicitly. Building
   `SlProbe` builds `SlKit` first.)
2. Copy `tools/SlKit/src/bin/Release/net48/SlKit.dll` to
   `~/.config/SCP Secret Laboratory/LabAPI/dependencies/global/` (or `dependencies/<port>/`).
   It must not go into `plugins/`: LabAPI loads every `.dll` in the plugin folders as a plugin and
   libraries belong in `dependencies/`.
3. Copy `tools/sl-probe/src/bin/Release/net48/SlProbe.dll` to
   `~/.config/SCP Secret Laboratory/LabAPI/plugins/global/` (or `plugins/<port>/`).
4. Start the server once. LabAPI writes `configs/<port>/SlProbe/config.yml`. Edit it and restart.

The probe is marked transparent (`IsTransparent`): it only reads timings and writes a log.

## Configuration

`configs/<port>/SlProbe/config.yml`:

| key | default | meaning |
|---|---|---|
| `csv_enabled` | `false` | Write one CSV line per second. Needed for the round-long comparison below. |
| `csv_path` | `frames.csv` | CSV file. A relative path lands in `configs/<port>/SlProbe/`. Appended to across restarts. |
| `report_every_seconds` | `60` | Seconds between report lines in the server log. `0` turns them off. |
| `window_frames` | `3600` | How many recent frames `report` and the log line cover (3600 is one minute at 60 FPS). Clamped to 300..100000. |
| `mark_round_events` | `true` | Put round events into the CSV as markers. Read when the event fires, so `labapi reload configs` applies it. |

`csv_enabled`, `csv_path`, `report_every_seconds` and `window_frames` are applied once at startup,
so changing them needs a restart.

A bigger `window_frames` gives a steadier p99 over a whole round, but each report sorts the whole
window. That is cheap at the default and grows with the window, so do not combine a very large
window with a one-second report interval. (The cost at 36000 frames is estimated, not measured.)

## Commands

Available in Remote Admin and in the server console. They need the `ServerConsoleCommands`
permission in Remote Admin; the console always has it. There is deliberately no `.slprobe` client
command, because that would be reachable by every connected player.

| command | what it does |
|---|---|
| `slprobe status` | Is the profiler installed, window size, frames recorded, CSV state, idle-mode warning, the clock's own cost per frame, pending main-thread queue. |
| `slprobe report` | Statistics over the current window (see below). Rate limited to two per second. |
| `slprobe mark <name>` | Writes a marker into the next CSV line, for example `slprobe mark enabled-feature-x`. Only works while the CSV is on. |
| `slprobe reset` | Empties the window so the next `report` covers only frames from now on. The CSV keeps running and gets a `reset` marker. |

The names are namespaced on purpose. LabAPI skips a command whose name already exists, without a
log line, so a generic name could silently not register.

### Reading a report

```
frames=3600 realFps=59.8 load=24.1% target=60
period ms: p50=16.67 p95=17.31 p99=21.04 max=63.20 mean=16.72 (n=3600)
busy   ms: p50=3.20 p95=4.15 p99=7.80 max=41.90 mean=3.60 (n=3600)
frames >50ms: 0.08%  >100ms: 0.00%
Mirror actualTickRate=60 fullUpdate=1.18ms (avg) | Server.Tps=60 (smoothed, for comparison)
```

(The numbers above are an illustration of the format, not a measurement.)

- **period** is what players experience. At a 60 FPS limit it sits at 16.7 ms until the server
  falls behind. The p95, p99 and the share of frames over 50 ms are the numbers that move first
  when something hitches.
- **busy** is how much of each frame the server actually works. The rest is waiting for the frame
  limiter, so busy minus its baseline is the cost of whatever you changed. The probe assumes the
  limiter's wait happens before the start of the Initialization phase; that is not verified for this
  Unity build, so treat busy as "time inside the frame" and compare it only between runs.
- **load** is mean busy / mean period.
- `target` is `Application.targetFrameRate`. Both runs of a comparison must show the same value.

The periodic log line carries the same figures on one line and is tagged `IDLE MODE` when the
server is idle.

## CSV format

One line per second, written by the SlKit background worker, never on the main thread:

```
utc,frames,fps,period_p50_ms,period_p95_ms,period_p99_ms,period_max_ms,busy_p50_ms,busy_p95_ms,
busy_p99_ms,busy_max_ms,pct_over_50ms,pct_over_100ms,mirror_tick_rate,mirror_full_update_ms,
server_tps,target_frame_rate,marks
```

(One line in the file; wrapped here.) `marks` holds the markers recorded during that second,
separated by `|`: `WaitingForPlayers`, `RoundStarted`, `RoundEnded`, `RoundRestarted`,
`WaveRespawned`, `WarheadDetonated`, `reset` and anything from `slprobe mark`. If the disk cannot
keep up, whole seconds are dropped and counted in `status` as `dropped seconds`.

## A/B measurement: baseline round against a round with the plugin under test

The idea: play two comparable rounds on the same server, one without the plugin and one with it,
and compare the tail of the frame time, not the average.

### Before you start

- **Players must be connected.** With no players the server enters idle mode: `timeScale` 0.01 and
  about one frame per second. Every number from that state describes a sleeping server. `status`
  and `report` warn about it, and the script skips such seconds.
- Use the **same hardware, same server config, same `server_tickrate`** and a similar player
  count for both runs. The player count changes the load more than most plugins do.
- Turn the CSV on (`csv_enabled: true`) and set a `window_frames` large enough for the part of the
  round you want to look at: 36000 is ten minutes at 60 FPS.
- Restart the server for each condition. Plugins are loaded at boot and LabAPI never unloads
  them. A fresh process warms up (JIT, caches), so ignore the first minute of every run.

### The two rounds

1. **Baseline.** Start the server without the plugin under test (remove its DLL, or set
   `is_enabled: false` in its `properties.yml`). Start a round with players.
2. About a minute after the round start, run `slprobe reset`. This drops the loading and
   round-start hitches from the window.
3. Play normally. Shortly before the round ends run `slprobe report` and save the output. For an
   exact p95/p99 over that stretch, the window must be at least as large as the stretch.
4. **Candidate.** Restart with the plugin installed and repeat steps 1 to 3.
5. Repeat each condition at least three times. Round-to-round variation (what the players do,
   where they are) is large, and one pair of rounds cannot tell you what is plugin and what is
   noise.

### Comparing

Use the CSV for whole rounds. The script combines the per-second lines:

```
python3 tools/sl-probe/analyze/slprobe_csv.py baseline.csv candidate.csv \
    --from-mark RoundStarted --to-mark RoundEnded --trim-start 60
```

Give it one file to get a summary, two to get a baseline / candidate / delta table. What it prints
and how far to trust it:

| line | exact? |
|---|---|
| `% frames > 50 ms`, `% frames > 100 ms` | Exact, each second weighted by its frame count. |
| `worst frame ms` | Exact. |
| `p95 / p99 of a typical second` (median) | The distribution of the per-second percentiles, not a p99 of all frames. |
| `p99 of a bad second` (95th percentile of the seconds), `worst second` | Same: how bad a bad second is. |
| `% of seconds with p99 > 50 ms` | Share of seconds that contained a visible hitch. |
| `busy ...` | Same construction, for time inside the frame. |

A p99 over all frames of a round cannot be rebuilt from per-second percentiles, which is why
`slprobe report` over a large window is the source for that single number.

`analyze/tests/` holds two small hand-written CSV files in the same format, for trying the
script. They are not measurements.

### Deciding

Compare, in this order:

1. `% frames > 50 ms` and the share of seconds with p99 over 50 ms. These show hitches.
2. p95 and p99 of the period, and of the busy time.
3. The worst frame, last and with the most suspicion. One outlier is usually the map, a GC pause
   or something outside the plugin.

If the candidate's numbers are inside the spread you see between your own baseline repeats, you
have no evidence of a difference. The probe tells you that the server got slower, not why. To find
the culprit inside a plugin, put its work on `SlClock` jobs, which report their own per-job cost
in `slprobe status`, or time sections with `Stopwatch`.

## What the probe does not see

- It measures the Unity main-thread loop only. Work on other threads (Mirror's transport poll, your
  own workers) shows up only when it makes the main thread late.
- Whole-process GC pauses and OS scheduling stalls do show up as long frames, but nothing in the
  numbers says which one caused them.
- Mirror's per-second values in the CSV (`mirror_tick_rate`, `mirror_full_update_ms`) are the
  engine's own coarse counters, included for reference.
- A frame that was already running when you called `reset` or `report` is counted in the window it
  finishes in.

## Cost

Per frame: two PlayerLoop callbacks, each one `Stopwatch.GetTimestamp` read plus a write into a
pre-allocated ring buffer, no allocation. Once per second, with the CSV on, the main thread copies
that second's samples into a pooled buffer; sorting, formatting and file IO happen on the SlKit
worker. Once per `report_every_seconds` the main thread sorts the window. This has not been
measured on a loaded server, so check `slprobe status` and compare a run with and without the probe
if the difference matters to you.

## Files

```
src/SlProbePlugin.cs    plugin class, config use, event markers, periodic report job
src/SlProbeConfig.cs    config class (YAML keys come from the property names)
src/SlProbeCommand.cs   the slprobe command
src/ProbeFormat.cs      text formatting and idle-mode detection
analyze/slprobe_csv.py  CSV summary and comparison
```

The build uses the game's own `SCPSL_Data/Managed` assemblies and nothing from NuGet; see
[`../SlKit/Directory.Build.props`](../SlKit/Directory.Build.props), which `sl-probe` imports.
