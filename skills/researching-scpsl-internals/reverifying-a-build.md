# Re-verifying the library after a game or LabAPI update

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of researching-scpsl-internals. Tools used: `scripts/event-sites.py` (this skill), `diff`, `grep`, ilspycmd, `tools/check-publishable.py`, `tools/sl-probe`.

Principle: let `diff` and scripts find what changed, then spend model time only on the changed call sites. Everything that did not change keeps its verdict; stamp the new build on it.

## 1. Mechanical pass (no model, minutes)

1. Install the new server. Note the build id (Steam manifest of app 996560) and the version string. Update LabAPI source to the tag that matches the new `LabApi.dll`.
2. Decompile `Assembly-CSharp.dll` into a **new** directory with the **same** ilspycmd version as the old tree (`ilspycmd --version`). A re-decompile of an unchanged build is byte-identical except the `.csproj` [M], so any other difference is a real change.
3. File-level diff: `diff -rq -x '*.csproj' <old tree> <new tree>`. Added, removed and changed files, nothing else. Repeat for `Mirror.dll` and, when its claims matter, `Assembly-CSharp-firstpass.dll` (MEC).
4. Event-focused diff, which answers "did a raise site move or change?":

   ```
   python3 scripts/event-sites.py --labapi <labapi>/LabApi --old <old tree> --game <new tree>
   ```

   Section 1 lists raise sites that appeared or vanished (a moved or deleted call changes the catalog row). Section 2 lists every file that raises events and whose content changed, with the events it raises: those are the call sites to re-read. On an unchanged re-decompile the output is empty.
5. Catalog drift: `python3 scripts/event-sites.py --labapi <labapi>/LabApi --game <new tree> --catalog tools/catalog/labapi-events.json`. It prints duplicate rows, events declared but missing from the catalog (new in LabAPI), the reverse (removed), events with no game site that are not marked `never-fired`, events marked `never-fired` that now have a site, and rows whose number of raise sites differs from `raisedFrom`. On 14.2.7 against the current catalog it reports 377 declared events, 377 distinct catalog rows, no duplicates, nothing missing either way, three LabAPI-raised events with no game site (`PickupCreated`, `PickupDestroyed`, `PluginsEnabled`), and three events whose site count differs from `raisedFrom` (`SearchedPickup` 5 vs 6, `UpdatingEffect` 1 vs 2, `CommandExecuted` 5 vs 3; read those by hand, a row may list methods rather than call lines). Commented-out declarations (`PlayerEvents.AimingWeapon`) are not counted: they are not events. The generator keeps one row per event (the independently verified one) and merges call sites.
6. LabAPI diff: `diff -rq <old labapi> <new labapi>` or the GitHub compare view between tags. Read changes in `Events/`, `Loader/`, `Features/Wrappers/Server`, `Features/Wrappers/Players`.
7. Run each reference file's own "Re-verifying on a new build" list for the skills whose files appear in step 3 or 6 (`grep -rn "Re-verifying" skills` finds them).

## 2. What to re-check first, in order

The order is by how far a wrong fact spreads. Each row: command, what a change means.

| # | Check | How | If it changed |
|---|---|---|---|
| 1 | Hot events: call sites and frequency | Rows with `hot: true` in the catalog (14 on 14.2.7). Read the enclosing method and its caller (grep-recipes.md 1, 4) | `labapi-events`, `scpsl-engine-cost-model`, lint rules that read the catalog |
| 2 | Cancellable events and ignored setters | grep-recipes.md 2, 3 on every `cancellable: true` row whose raise file changed | A setter that became honoured (or stopped being) flips a Common Mistake |
| 3 | `Server.Tps` formula | `grep -rn smoothDeltaTime <labapi> <tree>`. 14.2.7: both the LabAPI property and the game's `tps` command use `round(1 / Time.smoothDeltaTime)` | `measuring-scpsl-performance`, `scpsl-main-thread-and-timing` |
| 4 | Tickrate override and PlayerLoop | `grep -rn targetFrameRate <tree> <mirror tree>` (14.2.7: Mirror's `NetworkManager.ConfigureHeadlessFrameRate` sets it to `sendRate`; the `ServerTickrate` setter writes it when dedicated; idle mode writes it; `Headless.InitializeHeadless` writes it when the headless runtime asset limits the frame rate) and `grep -rln PlayerLoop <tree> <mirror tree>` (14.2.7: only Mirror's network loop) | Frame-probe insertion rule, "lower `server_tickrate` is inert" |
| 5 | Loader folders and order | In the LabAPI loader: `grep -n "SearchOption\|OrderBy\|FolderName" Loader/PluginLoader.cs Loader/Features/Paths/PathManager.cs` (inside `<labapi>/LabApi`). 14.2.7: recursive `.dll` scan of `plugins` and `dependencies`, enable order `OrderBy(Priority)`, folders `plugins`, `dependencies`, `configs` | `deploying-scpsl-plugins`, `scpsl-game-lifecycle` |
| 6 | BCL presence | grep-recipes.md 7 for each API the library says is present or absent (deconstruction, `Dictionary.Remove(key, out v)`, `Span<T>`, `ValueTuple`, `string.Contains(char)`) | Build-reference guidance in `deploying-scpsl-plugins`, polyfills in SlKit |
| 7 | Hint parameters and limits | List the `Hints` namespace and the hint serialisers in `Utils.Networking`; compare parameter type count and the per-update state hook with the hints skill | `scpsl-hints-and-hud` tables |
| 8 | Lifecycle: restart, idle mode, config reload | `IdleMode`, `CharacterClassManager` (round start and waiting statics), `GameCore.ConfigFile` reload; then a probe log of `Application.targetFrameRate`, `Time.timeScale`, `Server.IdleModeActive` | `scpsl-game-lifecycle` |
| 9 | Hot-method IL sizes | grep-recipes.md 8 on every method a skill calls inlinable or not | `scpsl-harmony-patching` |
| 10 | Unity, Mono or Harmony version | Server's `UnityEngine.CoreModule.dll` and the `0Harmony.dll` in `dependencies/`; Mono runtime string for the inline limit | Inline gate, GC and threading claims |
| 11 | Public API names the skills use | `grep` each wrapper member in `<labapi>`; a rename is a compile error for readers | `labapi-wrappers`, `porting-exiled-to-labapi` mapping |

Stop and re-research (research-workflow.md) when step 1 or 2 shows a moved raise site, a new loop around one, or a changed read-after-call. Do not stop for renamed local variables.

## 3. Regenerating the event catalog

1. Run the catalog drift report (step 1.5). New events and removed events are rows to add or delete. A row whose raise-site files did not change in the file diff keeps its verdict; only update `meta` and the version stamp.
2. For new events and for rows touched by the event-focused diff, run one catalog-builder agent per small batch (research-workflow.md, template C), then a skeptic on every hot or cancellable row in the batch.
3. Re-derive `hot` from the frequency class, not by hand. Remove duplicate rows (a chunked build can emit the same event twice). Set `meta.count` to the number of distinct events and `generated` to the new date.
4. Re-run the drift report: it must show no duplicates, nothing missing in either direction, and an empty "never-fired but has a site" list.
5. Bring the human-readable `events-catalog.md` in line with the JSON, then run the `scpsl-lint` tests (the linter reads the catalog at runtime).

Catalog row shape: `event`, `args`, `cancellable`, `settable`, `ignoredSetters`, `raisedFrom`, `frequency`, `frequencyDetail`, `hot`, `hotConditions`, `pair`, `consumption`, `notes`, `confidence`, `independentlyVerified`. Frequency classes: `per-frame-per-pair`, `per-frame`, `high-rate-network`, `per-action`, `per-lifecycle`, `per-round`, `on-request`, `once`, `never-fired`, `unknown`.

## 4. Finish

- Update the "Verified against LabAPI x / SCP:SL y (date)" line only in files whose checks you actually re-ran. A blanket sed hides which claims were not rechecked.
- Mark anything you could not re-check UNVERIFIED and say so in the commit message.
- Run `python3 tools/check-publishable.py --corpus <new Assembly-CSharp tree>`; fix findings in your files.

## 5. Measurement as ground truth

Static reading cannot settle: how long something takes, how much it allocates, interaction between plugins, thread timing, client-side rendering, or anything gated on live state. Measure those on a **test port** (own port, own config folder), never first on production.

- **Frame time.** Install `sl-probe` (it wraps `SlKit.FrameProfiler`, PlayerLoop `Stopwatch` markers). Read `slprobe report` and the CSV; compare p95, p99 and the share of frames over 50 and 100 ms between a baseline run and a candidate run, at least three of each, with players connected. `tools/sl-probe/README.md` has the A/B protocol and the comparison script, and states what has been exercised on a live server; read its status line before trusting a number. Method: measuring-scpsl-performance.
- **Dummies.** `NetworkManagerUtils.Dummies.DummyUtils.SpawnDummy(name)` and `DestroyAllDummies()` create and remove them (LabAPI 1.1.7 has no wrapper). They give the server real per-receiver serialisation work. They do **not** simulate the handshake and bans, sockets and batching, client handlers, voice, latency, client scene loading or hints, so a dummy load test under-measures network, join and client cost. They share one user id, are destroyed at every restart, keep the server out of idle mode, and a one-team dummy round ends by itself unless the round is locked. Details: scpsl-game-lifecycle. State the gap in every report.
- **Probes for facts.** A throwaway plugin that logs the value in question (target frame rate, time scale, thread id, patch owners) at the moments you care about is cheaper than another reading session. Delete it afterwards.
- **Label every number** [M] measured, [C] counted from code, [E] estimate, [U] unverified, with the assumption. A static count of calls is [C], not [M].
- **Same hardware, same config, same player-count bucket**, and ignore the first minute after boot. One pair of rounds proves nothing.

## Re-verifying on a new build

- Confirm `diff -rq -x '*.csproj'` of two decompiles of the same build is still empty; if not, the diff pipeline is polluted.
- Re-run both `event-sites.py` modes against the old and new trees and check the three LabAPI-raised events with no game site still behave the same.
- Re-check that the ordered checklist in section 2 still names the fragile facts: when a verdict flips in a later release, add its row here.
- Re-read `tools/sl-probe/README.md` status; if the probe has since been run on a live server, update the measurement section.
