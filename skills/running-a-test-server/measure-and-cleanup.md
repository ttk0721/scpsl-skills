# Measuring on the test server, and cleaning up

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Numbers are for a Linux machine with EXILED loaded, sl-probe 0.1.0 and SlKit 0.1.0. They are a reference for "is this plausible", not a benchmark for your hardware.

## Measuring: sl-probe, never Server.Tps

1. Copy `SlKit.dll` to `LabAPI/dependencies/<port>/` and `SlProbe.dll` to `LabAPI/plugins/<port>/` (the test port, not `global`). Start the server, then `slprobe status`.
2. Spawn dummies so the server leaves idle mode, `slprobe reset` after the round starts, then `slprobe report`.
3. Read `period` p50/p95/p99/max, the share of frames over 50 and 100 ms, and busy time. Method for baselines and A/B: measuring-scpsl-performance. Probe usage: its README under `tools/sl-probe`.

Reference run:

| State | real FPS | period p50 / p95 / p99 / max ms | busy p50 / p95 / p99 / max ms | >50 / >100 ms | Mirror fullUpdate | Server.Tps |
|---|---|---|---|---|---|---|
| empty lobby, first ~30 s | 55.6 | 16.67 / 16.68 / 19.51 / 1376.8 | 2.04 / 3.71 / 4.91 / 1376.8 | 0.33% / 0.22% | 2.06 ms | 50 |
| empty, idle mode | 1 while idle (p99 period 1000.0 ms) | n/a | n/a | 1.54% / 1.44% | n/a | **296** |
| 30 dummies, round running, steady | 60.0 | 16.7 / 16.7 / 16.7 / 17.1 | 6.0 / 8.2 / 10.0 / 12.9 | 0% / 0% | 5.93 ms | 60 (50 in the on-demand report) |

Findings:

- `Server.Tps` read 296 while the server ran one frame per second, and 50 while the median frame was a clean 60 fps with a hidden 1.38 s hitch. It cannot be quoted as evidence.
- One dummy ended idle mode immediately.
- 30 dummies cost about 4 ms of main-thread time per frame (busy p50 2.0 to 6.0 ms) and about 3.9 ms of Mirror update; the frame still met 60 fps at 37% load. Dummies skip sockets, auth and voice, so this under-measures real players.
- SlKit's clock cost 1.8 µs per frame on average (max 546.6 µs once, at install).

## Cleanup: remove only what you created

Everything a test instance leaves is in the config root and the EXILED root: `config/<port>/`, `LabAPI/{plugins,dependencies,configs}/<port>/`, `LabApi-<port>.yml`, `LocalAdminLogs/<port>/`, `ServerLogs/<port>/`, EXILED per-port files (`Configs/Plugins/*/<port>.yml`, `Logs/<port>-RAlog.txt`), and whatever a running plugin wrote beside the game in the install folder. The scripts keep the state folder (fifo, log, pid, markers) separately.

1. Stop the server (`bash scripts/stop-server.sh <port>`), confirm the recorded process and its game child are gone. Never `pkill LocalAdmin` or `pkill SCPSL`: those names also match the owner's servers. `stop-server.sh --kill` signals only a process it has verified as its own LocalAdmin for that port, and refuses otherwise. It then waits (`SLT_KILL_WAIT`, default 10 s) and checks that LocalAdmin and its game child are gone: if not, it exits 5 and keeps the pid file. `--kill --sigkill` is the explicit second step (SIGKILL); a non-zero exit means the server is still up, never "probably dead".
2. `bash scripts/list-created.sh <port>` lists paths BORN after the instance was first started. It scans the install that `start-server.sh` recorded for that port; with no record and several installs on the machine it refuses (exit 6) until you pass `--install DIR`. Birth time, not modification time: LabAPI and EXILED rewrite their per-port configs on every load, so mtime marks everything as touched.
3. Read the list. Remove only lines tagged `OURS`, one path at a time, after showing the user the list. `SHARED` (under `global`) and `OTHER` (including anything in the install folder) stay unless the user decides otherwise. `OWNER` means the test ran on the owner's port at their request: nothing goes without asking. `KEEP` never goes.
4. Remove your test DLLs and any copy of SlKit/SlProbe you put in `<port>` folders, and the state folder when the user wants the instance gone for good.
5. Say what you removed and what you left, with paths.

Never delete:

- `Round Logs Archive <date>.zip`. The game zips old round logs at start (older than 7 days, `compress_old_round_logs`) and deletes the originals, so the zip is the owner's only copy. It looks like test garbage and is not.
- Anything under a port that existed before you started.
- Anything in `global` folders that you did not put there yourself.

If the filesystem does not record birth time (`stat -c %W` prints 0), `list-created.sh` says so. Then list candidates by port name only and ask the user before removing.

## Pitfall: a global plugin needing a per-port dependency

A plugin in `plugins/global` that needs a library present only in `dependencies/<other port>` fails on every other port. Example: a global plugin module needing `0Harmony` 2.4.2.0 that exists only in `dependencies/<port A>` throws `ReflectionTypeLoadException` on port B. Global plugins need global dependencies (deploying-scpsl-plugins). This bites exactly when you start a separate instance next to an owner's server that was set up port by port. Do not "fix" it by copying DLLs into the owner's folders: for the test instance, either isolate its plugin paths (launch-and-console.md) or put what it needs into `dependencies/<test port>` and tell the user.

## Re-verifying on a new build

- Start a fresh port, run `list-created.sh`, and confirm every created path is classified and nothing outside the root changed.
- Check that the old-log archive is still created at start for ports with old logs, and its name.
- Re-measure idle mode versus 1 dummy, and re-run `slprobe report` with 30 dummies; replace the table with the new numbers and machine class.
