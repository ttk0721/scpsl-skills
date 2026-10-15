# Evidence recipes

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of verifying-scpsl-work. One section per claim in the SKILL.md table. Runtime sections apply only when the owner agreed to local tests (test-server.md).

## Compiles

```
dotnet build <Plugin>.csproj -c Release -p:SL_REFERENCES="<install>/SCPSL_Data/Managed"
```

`SL_REFERENCES` is the property name used by the csproj template in deploying-scpsl-plugins (build-and-references.md); use your project's own. Read the last lines: `0 Error(s)`. Warnings about missing references mean you built against the wrong set; fix the references (deploying-scpsl-plugins, build-and-references.md). A CS1061 on `KeyValuePair` deconstruction or `Dictionary.Remove(key, out v)` means the net48 pack leaked in. If the owner has no install, you cannot build against `Managed`: say so, and do not call the code compiled.

## Unit tests

Only pure logic can be unit tested: code with no Unity or LabAPI types (SlKit's `Core` folder is the model, run with `dotnet run --project <tests> -c Release`, exit 0). Put decisions (a rate limit, a threshold, an exclusion rule such as "staff are not rewarded") in plain classes so a test can cover them; the handler then only calls them. Report the count: "34 passed, 0 failed". A handler that touches `Player` is not unit-testable here.

## Lint

```
python3 tools/scpsl-lint/scpsl_lint.py <project dir>
```

Scan the whole project (SL015 looks across files). Exit 0 means no errors; warnings need a decision. A suppression is `// scpsl-lint: ignore SLxxx -- reason`; no reason, no suppression.

## Loads

Stop the server, copy the DLL to `plugins/<port>`, start, then:

```
grep -E "Successfully enabled|Couldn't|Failed to load the configuration|Potential issue|Forcefully|Unable to register" "<boot log>"
```

Pass: the enabled line for your `Name` and zero failure lines (full string table: deploying-scpsl-plugins, verify-and-brand.md). Silence is a folder or `properties.yml` problem. The line proves only that the loader got through `Enable()`; it says nothing about handlers.

## Handler works

1. Decide the trigger (a join, a kill, a role change) and the line the handler will write. Add one if there is none: first run per round, rate-limited, never per frame.
2. Trigger it for real. **`PlayerEvents.Joined` is never raised for a dummy** (nor for the host): a join handler needs a connected client, so with no client it stays NOT VERIFIED. A dummy (`/dummy spawn <name>`, Remote Admin command, leading slash) does raise `Left` when destroyed, takes part in role assignment and counts as a player; `/forcestart` works in the lobby only. Dummies skip sockets and auth, and a dummy's user id is `ID_Dummy`, so any rule keyed by user id sees one identity: say so.
3. Read the line in the log. For timing-sensitive handlers call `FrameProfiler.Mark("<name>")` from the handler: it lands in the sl-probe CSV, and only while the CSV is on (`slprobe mark <name>` does the same from the console).
4. Test the negative path too: the case the handler must NOT act on (staff, the host, a dummy, a disabled config).

## Cheap

Never `Server.Tps`. The method is in measuring-scpsl-performance (protocol.md); the minimum for a "no measurable cost" claim:

1. Same server, same config, same player or dummy count, not idle mode (`Server.IdleModeActive` is "no data"). Restart per condition; ignore the first minute.
2. Baseline without the plugin (DLL removed, or `is_enabled: false` in its `properties.yml`; both need a restart): `slprobe reset`, play, `slprobe report`; save it.
3. Candidate with the plugin: same steps. At least three repeats each; if the candidate sits inside the spread of your own baseline repeats, you have no evidence of a difference.
4. Compare in this order: % frames over 50 ms, p95 and p99 of the period and of busy time, then the worst frame.
5. Per-job cost: register work on a named `SlClock` job and read its microseconds (`SlClock.Stats`, or `slprobe status`). Time other sections with `Stopwatch`.
6. Allocation claims: list every allocation by reading the code, then compare `GC.CollectionCount(0)` per minute with the feature on and off (coarse).

Label each number MEASURED or ESTIMATE. A dummy load under-measures network, join and voice cost; write that next to the number.

## Survives a round restart

1. Have state worth losing: a counter above zero, a cached `Player`, a running timer.
2. End the round (the game restarts after `auto_round_restart_time`, default 10 s; a locked round never ends; a one-team dummy roster ends it shortly after a 15 s guard, exact figure unverified), or run `softrestart`. They are different paths: `softrestart` raises neither `RoundRestarted` nor sets the restart flag, so test the natural path and, if your code reacts to restarts, `softrestart` as well.
3. Read: the reset line at `WaitingForPlayers`, counters back to zero, no exception mentioning a destroyed object, the handler firing in round 2 (a join handler needs a client to reconnect). Dummies are destroyed at every restart; recreate them.
4. Unverified here: the exact Remote Admin name of a forced full restart on your build. Look it up with `help`.

## Deployed

On the TARGET server, not your test instance: the DLL is in `plugins/<port>` or `plugins/global` as intended, dependencies in `dependencies/`, no second copy or `.dll` backup anywhere below, the process was restarted after the copy, and the boot log has the enabled line with your version. If you cannot reach the target, "deployed" is not claimable: write the owner's steps (unverified-claims.md).

## Re-verifying on a new build

- Re-run the loads grep against a boot with a misplaced DLL to confirm the failure strings still match.
- Check that `slprobe` commands and `SlClock.Stats` output keep the names used here.
- Re-test the `softrestart` versus natural-restart difference when the game updates (scpsl-game-lifecycle, restart-and-state.md).
