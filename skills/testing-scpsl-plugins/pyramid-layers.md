# The pyramid, layer by layer

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of testing-scpsl-plugins. The compiled example is `examples/testing-pattern/`; every snippet below is taken from it unless marked FRAGMENT.

## L0: compile against the server's own assemblies

Build with the references pointing at the server's `SCPSL_Data/Managed`, never the desktop .NET Framework pack, or members the server lacks (or has) mislead you. Setup and the list of absent BCL members: deploying-scpsl-plugins.

```
dotnet build <Plugin>.csproj -c Release -p:SL_REFERENCES="<server>/SCPSL_Data/Managed"
```

Catches: wrong names, wrong signatures, missing BCL members. Does not catch: any runtime behaviour, including a handler that never runs. 0 errors is the entry ticket, not a result.

## L1: pure Core on plain .NET

A console project on `net10.0` that compiles the shared `.cs` files and returns exit code 0 or 1. No NuGet package, no test framework, no game DLL. Same shape as SlKit's own tests.

```xml
<ItemGroup>
  <Compile Include="../Core/**/*.cs" Link="Core/%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

Run the example's runner and build its plugin (from the repository root):

```
dotnet run --project examples/testing-pattern/Tests/TestingPattern.Tests.csproj -c Release
dotnet build examples/testing-pattern/TestingPattern.csproj -c Release
```

Rules for Core:
- No `using LabApi`, `UnityEngine`, `Mirror`; no `Player`, `ReferenceHub`, `Time.*`, `DateTime.Now`, statics holding state.
- Time is a parameter (`Tick(double nowSeconds)`), so a test advances a fake clock one second per call.
- Keys are `UserId` strings.
- Reuse SlKit's `src/Core` types (deadline timer, token bucket, ring buffer, percentiles, bounded queue) instead of rewriting them: they are already tested.

What belongs here: thresholds and boundaries, state machines, parsing and formatting, windowing and debouncing, classification of HTTP status codes into retry and stop, and any exclusion rule.

Two traps when the runner lives under the plugin folder:
- Put an empty `Directory.Build.props` in the runner's folder. Otherwise MSBuild walks up to the examples' props, which pin `net48` and the game DLLs.
- XML comments cannot contain `--`. A comment showing a command with a long option such as `--project` breaks the whole project file with MSB4025.

Exclude the runner from the plugin project: `<Compile Remove="Tests/**" />`.

## L2: handler logic behind small interfaces

The handler body is where bugs live (resets, leaks, double grants), and it is not testable while it holds a `Player`. Cut three seams:

```csharp
public struct PlayerSnapshot { public string UserId; public bool IsStaff; public bool IsAlive; /* ctor */ }

public interface IPlayerSource { void Fill(List<PlayerSnapshot> into); }   // world, read-only
public interface IRewardSink   { void Grant(string userId, int amount); }  // effect, may throw
// time: a double argument. rules: a Func<RewardRules> read at use time.
```

The plugin implements `IPlayerSource` over `Player.ReadyList` and copies plain values. That adapter is the only file that touches `Player`, and it must stay free of decisions: which players count and what "staff" means are Core rules with tests. The test fakes are 10 lines each (`FakeWorld`, `FakeSink` in `Tests/Program.cs`).

What the example's 15 tests cover, as a model for your own:

| Test shape | Example |
|---|---|
| Boundary | 9 s pays nothing, 10 s pays once, 30 s pays three times |
| Exclusion with a control | Staff never paid, and the non-staff player in the same run is |
| Reset | Round start clears credit and the time baseline; `Left` removes state; a rejoin starts at zero |
| Garbage in | Null or empty `UserId`, `OnLeft(null)`, threshold 0 |
| Hitch | A 100 s gap credits at most the cap |
| Kill switch | Disabled credits nothing, and re-enabling does not credit the gap |
| Config reload | A replaced rules object applies on the next tick |
| Failure policy | One failing sink call does not block others, is counted, is retried, lands exactly once after recovery |
| Bound | A 100 s outage leaves one reward owed, not ten queued for the moment the service returns |
| Allocation | 1000 ticks over 60 players allocate 0 bytes after warm-up |

The allocation test uses `GC.GetAllocatedBytesForCurrentThread()` on the .NET 10 runner. It is a tripwire for your own code (LINQ, closures, boxing, string building), not proof for the server's Mono runtime, where allocation behaves differently. It covers Core only: the adapter's `ReadyList` iterator and `UserId` string work are outside it (fine at 1 Hz, not per frame).

The sink is called on the main thread, so a real one (web service, database) enqueues and returns; a synchronous HTTP call in `Grant` would stall every frame while the service is down. The example's retry has no back-off: add one with its own test before pointing it at a real service.

**Threads (FRAGMENT).** For code that hops from a worker thread back to the main thread, model the hop as an explicit queue the test drains:

```csharp
sealed class ManualPoster { public Queue<Action> Pending = new Queue<Action>(); public void Post(Action a) => Pending.Enqueue(a); public void Drain() { while (Pending.Count > 0) Pending.Dequeue()(); } }
```

A fake transport completes "later". The test asserts game-side state is unchanged until `Drain()`, then changed. That catches callbacks that mutate state on a pool thread (scpsl-main-thread-and-timing).

What L2 cannot see: whether the right event is subscribed, whether it is unsubscribed, whether the adapter maps fields correctly. Those are L0, L3, review (reviewing-scpsl-plugins) and L4.

## L3: scpsl-lint

```
python3 tools/scpsl-lint/scpsl_lint.py <plugin folder>
```

Scan the whole project in one call. Exit 0 means no errors; 1 means at least one error finding. A suppression is a comment with a written reason: `// scpsl-lint: ignore SL004 -- why`. Rule list: tools/scpsl-lint/README.md. It is a text scanner: clean means "no known trap", never "correct".

## L5: dummy load and measurement

Only with consent (`server-test-protocol.md`). Dummies spawn with `/dummy spawn <name>` on the server console (leading slash). They keep the server out of idle mode, are destroyed on every round restart, and become spectators if spawned mid-round. All of them share the user id `ID_Dummy`, so state keyed by `UserId` collapses to one entry (and the example's adapter skips dummies on purpose). Lock the round with `Round.IsLocked = true` for a long run; the static survives restarts, so reset it yourself. Dummies skip sockets, auth, voice, client commands, hints and latency, so a dummy run under-measures network, join and client cost: state that gap in the report.

Measure with `sl-probe` (`slprobe status`, `slprobe report`, `slprobe reset`): baseline first, same player count, p95/p99 and the share of frames over 50 ms, never `Server.Tps`. Protocol: measuring-scpsl-performance. Dummy details: scpsl-game-lifecycle.

## Re-verifying on a new build

- Rebuild the example against the new `Managed` folder; fix names that moved.
- Re-run the runner and confirm the stub still gives red and the implementation green.
- Confirm `Player.ReadyList` and the dummy rules in the lifecycle skill still hold.
- Re-run scpsl-lint on the example: it must stay at 0 errors.
- Check whether the console still needs a leading slash for `dummy`.
