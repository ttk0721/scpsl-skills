# Example task (writing-scpsl-plans)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

One complete task from a plan for "count active play time; AFK players, spectators and staff earn
nothing". The finished code is compiled as `examples/activity-time` (`ActivityTime.csproj`, 0
errors against the server's Managed folder, scpsl-lint clean) and its Core tests run with
`dotnet run --project examples/activity-time/tests/ActivityTime.Core.Tests -c Release`.

## The plan's constraints this task inherits (excerpt, copied from the spec)

```markdown
- Environment: peak 60 players; LabAPI only; another plugin rewrites hints on spawn (this plan sends
  no hints); one server; round restart every 15-20 min; empty and idle most nights.
- Business rules: groups owner/admin/moderator earn nothing; standing still for 30 s or being
  dead earns nothing; totals survive round restarts and are flushed at shutdown.
- Test server: own private instance on port 9999; owner's 7777 untouched.
```

| Event or job | Catalog class | Hot | Work per raise | Budget |
|---|---|---|---|---|
| `PlayerEvents.Left` | per-lifecycle | no | one dictionary lookup | - |
| `ServerEvents.WaitingForPlayers` | per-round | no | flag reset per tracked user | - |
| `ServerEvents.Shutdown` | once | no | synchronous write of one line per user | small file: the shutdown window is about 0.6-2 s |
| job `activity.sample` | `EveryStaggered`, 1 s period | - | 1 player per frame at 60 players and 60 FPS | avg <= 20 us per frame (spec figure) |

## Task 2: Active-time accrual (Core ledger, staggered sampling)

**Files:**
- Create: `Core/ActivityPolicy.cs`, `Core/ActivityLedger.cs`
- Create: `tests/ActivityTime.Core.Tests/Program.cs` (runner from Task 1, tests added here)
- Modify: `ActivityTimePlugin.cs`: `LoadConfigs`, `Enable`, `Teardown`, new `FillBatch`, `SampleOne`, `OnWaitingForPlayers`, `OnLeft`

**Skills:** adding-per-player-tracking (recipe); scpsl-game-lifecycle, labapi-wrappers, scpsl-main-thread-and-timing.

**Interfaces:**
- Consumes (Task 1): `ActivityTimePlugin : Plugin<ActivityConfig>` with `Cfg`, try/rollback `Enable()`, `Teardown()`; `ActivityConfig.SamplePeriodSeconds`, `MinMoveMeters`, `IdleWindowSeconds`, `ExcludedGroups`.
- Produces: `readonly struct Pos3` with constructor `Pos3(float x, float y, float z)`;
  `ActivityPolicy(double minMoveMeters, double idleWindowSeconds, double maxCreditSeconds, IEnumerable<string> excludedGroups)` with `bool IsExcluded(string permissionsGroup)` and `bool ShouldTrack(bool isHostOrDummy, bool isReady, string permissionsGroup)`;
  `ActivityLedger`: `double Sample(string userId, Pos3 pos, bool alive, double now, ActivityPolicy policy)`, `void ForgetMotion(string userId)`, `void ResetMotion()`, `double SecondsOf(string userId)`, `void CopyTotals(List<KeyValuePair<string, double>> into)`.
- Game/SlKit members (each looked up in LabAPI source or the SlKit README): `Player.List`, `IsHost`, `IsDummy`, `IsReady`, `IsDestroyed`, `IsAlive`, `UserId`, `Position`, `PermissionsGroupName`; `PlayerEvents.Left` (`PlayerLeftEventArgs.Player`); `ServerEvents.WaitingForPlayers`; `RoundRestarting.RoundRestart.IsRoundRestarting`; `SlClock.EveryStaggered<T>(TimeSpan, Func<IReadOnlyList<T>>, Action<T>, string, ClockScope)`.
- Budget rows: `activity.sample`, `Left`, `WaitingForPlayers`.

- [ ] **Step 1: Write the failing tests** (9 in the example; three that pin business rules):

```csharp
private static void StandingStillStopsEarningAfterIdleWindow()
{
    var ledger = new ActivityLedger();
    ActivityPolicy policy = NewPolicy();               // 0.5 m, 30 s window, 2 s max credit
    for (int t = 0; t <= 60; t++)
        ledger.Sample("afk@steam", new Pos3(5, 0, 5), true, t, policy);
    Near(30, ledger.SecondsOf("afk@steam"));           // credited only inside the window
}

private static void RoundRestartKeepsTotalsAndDropsPositions()
{
    var ledger = new ActivityLedger();
    ActivityPolicy policy = NewPolicy();
    ledger.Sample("u1@steam", new Pos3(0, 0, 0), true, 0, policy);
    ledger.Sample("u1@steam", new Pos3(1, 0, 0), true, 1, policy);
    ledger.ResetMotion();                                                    // WaitingForPlayers
    Near(0, ledger.Sample("u1@steam", new Pos3(90, 0, 0), true, 2, policy)); // new map: re-baseline
    Near(1, ledger.SecondsOf("u1@steam"));
}

private static void StaffGroupsAreExcludedCaseInsensitively()
{
    ActivityPolicy policy = NewPolicy();               // excluded: "owner", "Admin"
    True(policy.IsExcluded("admin"), "admin excluded");
    True(!policy.IsExcluded("vip"), "vip earns");
    True(!policy.IsExcluded(null), "no group earns");
}
```

- [ ] **Step 2: Run, expect failure.** `dotnet run --project tests/ActivityTime.Core.Tests -c Release`
  → build errors `CS0234` (namespace `Examples.ActivityTime.Core`) and `CS0246` (`ActivityLedger`). A compile failure counts as the failing run.

- [ ] **Step 3: Implement in Core/.** The decision in full (rest in `Core/ActivityLedger.cs`):

```csharp
double elapsed = now - e.LastSampleAt;
e.LastSampleAt = now;
if (elapsed <= 0) return 0;
if (pos.DistanceTo(e.LastPos) >= policy.MinMoveMeters) { e.LastPos = pos; e.LastMoveAt = now; }
if (!alive || now - e.LastMoveAt > policy.IdleWindowSeconds) return 0;
double credit = Math.Min(elapsed, policy.MaxCreditSeconds);   // a hitch never over-credits
e.Seconds += credit;
return credit;
```

- [ ] **Step 4: Run, expect pass.** → `ALL 9 TESTS PASSED`, exit code 0.

- [ ] **Step 5: Wire it.** One staggered job instead of a coroutine per player; the adapter copies
  plain values and Core decides who counts; nothing but `UserId` stored:

```csharp
// Enable(), before _enabled = true; Teardown() disposes _sampleJob and removes every handler.
ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
PlayerEvents.Left += OnLeft;
TimeSpan period = TimeSpan.FromSeconds(Math.Max(0.25, Cfg.SamplePeriodSeconds));
_sampleJob = SlClock.EveryStaggered<Player>(period, FillBatch, SampleOne, "activity.sample");

private IReadOnlyList<Player> FillBatch()            // once per period, reused list
{
    _batch.Clear();
    if (!Cfg.Enabled) return _batch;                 // kill switch, read at use
    foreach (Player p in Player.List)
        if (_policy.ShouldTrack(p.IsHost || p.IsDummy, p.IsReady, p.PermissionsGroupName))
            _batch.Add(p);
    return _batch;
}

private void SampleOne(Player p)
{
    if (p.IsDestroyed) return;                       // left during the period
    Vector3 v = p.Position;
    double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    _ledger.Sample(p.UserId, new Pos3(v.x, v.y, v.z), p.IsAlive, now, _policy);
}

private void OnWaitingForPlayers() => _ledger.ResetMotion();
// OnLeft: return while RoundRestart.IsRoundRestarting, else _ledger.ForgetMotion(ev.Player.UserId).
```

- [ ] **Step 6: Gates.**
  - `dotnet build ActivityTime.csproj -c Release` → `0 errors`
  - `dotnet run --project tests/ActivityTime.Core.Tests -c Release` → exit 0 (Core compiles with no game DLL)
  - `python3 <scpsl-skills>/tools/scpsl-lint/scpsl_lint.py .` → `0 error(s)`

- [ ] **Step 7: Measure (hot path: per player per period).**
  - Test instance on port 9999 (consent recorded): with 30 dummies and sl-probe installed, read
    `slprobe status` → `clock: ... own cost avg` with and without this plugin (the `Enabled` kill
    switch plus `labapi reload configs` switches it without a restart; one shared
    `SlKit.dll` in `dependencies/`, so that line covers every plugin's jobs, this one included;
    `slprobe status` lists per-job stats only for `slprobe.*` jobs, so `activity.sample` has no line
    of its own there). **Gap to state:**
    `ShouldTrack` skips dummies, so this run measures the list scan, not `Sample`.
  - Real players (in the deploy task, when the owner deploys): the same line at a similar player
    count before and after enabling. Pass: the increase is inside the 20 us budget. Record both
    numbers as MEASURED with the player count; `Server.Tps` is not evidence.

- [ ] **Step 8: Commit** `Core/`, `tests/`, `ActivityTimePlugin.cs`:
  `feat: accrue active play time per UserId with one staggered sampling job`.

## What the task shape prevents

| Element | Prevents |
|---|---|
| Staff and `ShouldTrack` tests from business rules in Global Constraints | rewarding staff because nobody asked; untested filters hidden in the adapter |
| `ResetMotion` at `WaitingForPlayers` and its test | crediting the jump between two maps at every 15-20 min restart |
| `MaxCreditSeconds` and the stall test | a hitch, a late sample or an idle-mode frame paying out many seconds |
| `IsRoundRestarting` check in `Left` | treating every hub destroyed by a restart as a real disconnect |
| One `EveryStaggered` job, reused list | a coroutine per player and a per-tick allocation |
| `UserId` keys, `IsDestroyed` check | stale `Player` objects across restarts and leaves |
| The dummy gap stated in Step 7 | a load test reported as proof of code it never ran |

## Re-verifying on a new build

- The `Player` members listed in Interfaces still exist with the same meaning (`IsReady`,
  `IsDummy`, `PermissionsGroupName` returning the `config_remoteadmin` group key or null).
- `PlayerEvents.Left` and `ServerEvents.WaitingForPlayers` keep their catalog classes, and `Left`
  still fires for hubs destroyed by a restart while `RoundRestart.IsRoundRestarting` is true.
- `SlClock.EveryStaggered<T>` keeps its signature (SlKit README).
- `slprobe status` still prints the `clock: ... own cost avg` line.
- Rebuild `examples/activity-time` and rerun its tests.
