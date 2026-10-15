# Grep recipes for engine questions

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of researching-scpsl-internals. `<tree>` is a local `ilspycmd -p` decompile of `Assembly-CSharp.dll` (sources-and-decompiling.md); `<labapi>` is a checkout of the LabAPI source. Every recipe below was run on 14.2.7; results are named at type/method level only.

| Question | Recipe |
|---|---|
| Where does the game raise event X? | 1 |
| Who reads the args after the call? Is a setter honoured? | 2, 3 |
| Does this run per frame, per message, per request? | 4 |
| Which SyncVars does a component have? | 5 |
| Which plugins patch this method? | 6 |
| Does this API exist on the server runtime? | 7 |
| Could the JIT inline this method (patch bypass)? | 8 |
| Is the event catalog still true? | `scripts/event-sites.py` (reverifying-a-build.md) |

**Keep output small.** First `-l` (files) or `-c` (counts), then `-n` on one file or one namespace directory, `-m N` to stop after N hits per file. A broad pattern over the whole tree is the expensive mistake: `[Command]`/`[ClientRpc]`/`[TargetRpc]` file names alone are about 35 KB of output [M]; `RegisterHandler|ReplaceHandler` file names about 6 KB [M]. Quote globs (`--include='*.cs'`); fish and zsh abort on an unquoted glob that matches nothing. `-rc` prints a line for every file, so filter it: `grep -rc <pattern> <tree> | grep -v ':0$'`. Types in the root namespace are loose files at the top of the tree (`<tree>/RoundSummary.cs`), not in a directory: grep the file, not `<tree>/<Namespace>`.

## 1. Where is event X raised

LabAPI declares `public static event LabEventHandler<TArgs> Name` in `<Category>Events.EventHandlers.cs` and raises through a matching `On<Name>` method. The game calls that method, so:

```
grep -rn "PlayerEvents.OnHurting(" <tree>
```

returns the call sites (one on 14.2.7: the damage entry method of `PlayerStats`). `-ing` and `-ed` are separate methods and separate sites. Count with `-c`, list files with `-l`.

- Zero hits means one of: the event is raised by LabAPI itself (14.2.7: `ServerEvents.PickupCreated`, `PickupDestroyed`, `PluginsEnabled`, found with the same grep inside `<labapi>`), the game raises it through a helper (grep the `On` name without the category prefix), or nothing raises it. `never-fired` events on 14.2.7: `PlayerEvents.SearchedAmmo`, `SearchedArmor`, `ServerEvents.CassieAnnouncing`, `CassieAnnounced`. (`PlayerEvents.AimingWeapon` is commented out in LabAPI's source, so it is not an event and not in the catalog.)
- Declarations without a type argument exist (for example the round and lifecycle events), so a regex over `LabEventHandler<` alone misses some. `scripts/event-sites.py` handles both.
- To see which game method encloses a hit, read upward from the line with `sed -n 'A,Bp' <file>` until the method header.

## 2. Who reads the args after the call

Read the lines after the raise:

```
sed -n 'A,Bp' <file>    # A = line of the call, B = A+25 (sed has no arithmetic)
```

Look for: an `if (!e.IsAllowed) return` (cancel honoured), reads of `e.Something` below the call (setter honoured), and local variables built **before** the call that the code keeps using instead of the args (setter ignored). 14.2.7 example: after raising `Hurting` the game tests `IsAllowed`, then applies its own local damage-handler variable; reassigning `ev.DamageHandler` has no effect, mutating the handler object does (this is the rule in labapi-events).

## 3. Is a setter honoured

1. In `<labapi>` open the args class (`Events/Arguments/<Category>Events/`). List public setters.
2. In the raising method, grep `e\.<Property>` (or the args variable name) for the lines after the call.
3. A setter with no read after the call is a trap: record it as an ignored setter. A getter-only property cannot be a setter at all. Also check the **pair** event: a value read from the `-ed` args reflects what the game used, not what you set.

## 4. Per frame, per message or per request

A raise is as frequent as its caller. Find the caller class, then classify:

- **Unity callbacks:** `grep -rlE "void (Update|LateUpdate|FixedUpdate)\(\)" <tree>/<Namespace>` lists classes with per-frame code (the `Respawning` namespace has one, the wave manager). Check also the game's static per-frame events: `grep -rn "StaticUnityMethods.On" <tree>`.
- **MEC loops:** `grep -rn "RunCoroutine(" <tree>/<Namespace>`; read the segment argument (`Segment.FixedUpdate`, `Update`, `SlowUpdate`) and the coroutine body (`while (true)` plus `yield return Timing.WaitForSeconds(x)` gives the period). Round-end logic runs coroutines in the `FixedUpdate` segment. Also grep `CallPeriodically`, `CallContinuously`, `InvokeRepeating`, `StartCoroutine(`.
- **Mirror message handlers:** `grep -rnE "(Register|Replace)Handler<" <tree>`. Both spellings exist: the voice message is registered with `ReplaceHandler<VoiceMessage>` in `VoiceTransceiver`, so grepping `RegisterHandler` alone misses it. A handler runs once per received message, so the rate is the client's send rate times the clients.
- **Client requests:** `grep -rnE "^\s*\[(Command|ClientRpc|TargetRpc)" <tree>/<Namespace>` (always scoped). `[Command]` runs per client request; `[ClientRpc]`/`[TargetRpc]` are server-to-client calls, so they show wire cost.
- **Multipliers:** if the raise sits inside a `foreach` over players or hubs, multiply by N (or N x N for observer/target pairs). The catalog calls these `per-frame-per-pair`.
- **Conditions:** note anything gating the raise (a role check, an effect, a timer); that is the catalog's `hotConditions`.

## 5. SyncVars of a component

```
grep -n "SyncVar" <tree>/<Namespace>/<Type>.cs
```

lists `[SyncVar]` fields, `hook = "Name"` where a client-side hook exists, and the generated setters whose bit constants (`1uL`, `2uL`, `4uL`...) give the dirty-bit order. `SyncList`, `SyncDictionary`, `SyncHashSet` fields are sync objects; grep `Sync(List|Dictionary|HashSet)` too. Without a tree: `ilspycmd -t <Type> -o <out> <dll>` and grep the file (`NicknameSync` has five SyncVars, three with hooks). Assigning the property marks it dirty, so every write costs wire time (scpsl-networking-for-plugins).

## 6. Who patches a method

Runtime, in a probe plugin (member names are from the installed Harmony assembly; the snippet is unverified on a live server):

```csharp
// using HarmonyLib; using System.Linq; Logger is LabApi.Features.Console.Logger
var info = Harmony.GetPatchInfo(method);          // static; null when unpatched
if (info != null)
    foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
        Logger.Info($"{method.DeclaringType.Name}.{method.Name}: {p.owner} priority {p.priority} index {p.index}");
```

`Patches` also has `Owners`; static `Harmony.GetAllPatchedMethods()` lists every patched method. Cheap to run once at `WaitingForPlayers` per hot target (scpsl-harmony-patching lists targets).

Static, in plugin source: `grep -rln "\[HarmonyPatch" <plugin source>` finds attribute patches (EXILED's Scp096 patch folder alone holds seven files). Also grep `\.Patch(`, `PatchAll`, `Unpatch` and `UnpatchAll` for manual patching and for plugins that unpatch the world. For a DLL without source, decompile it (`-p`) and grep the same patterns. Read the `Harmony` copy beside the plugins (`dependencies/`), not the one you build against.

## 7. Does an API exist on the server runtime

The server's own libraries decide, not the net48 reference pack or docs.

- Member of a known type: `ilspycmd -t 'System.Collections.Generic.KeyValuePair`2' -o <out> <Managed>/mscorlib.dll`, then `grep Deconstruct <file>`. 14.2.7: `KeyValuePair<K,V>.Deconstruct` and `Dictionary<K,V>.Remove(key, out value)` are present.
- Does the type exist at all: `ilspycmd -l s -o <out> <Managed>/mscorlib.dll` (or `c`), then `grep` the list file. 14.2.7 `mscorlib` has `Span<T>`, `ReadOnlySpan<T>`, `Index`, `Range`, `ValueTuple` family.
- Which assembly holds it: list `<Managed>`; `System.Text.Json.dll`, `System.Net.Http.dll`, `System.Memory.dll`, `YamlDotNet.dll`, `Newtonsoft.Json.dll` (from plugin `dependencies/`, not `Managed`) are separate files.
- Present at runtime and absent from your compile references are different problems (deploying-scpsl-plugins).

## 8. IL size, inlining risk

`-il` dumps the whole assembly, so do it once per build into a file:

```
ilspycmd --disable-updatecheck -il -o <out> <Managed>/Assembly-CSharp.dll        # about 40 MB file [M]
grep -E "Code size|end of method ReferenceHub::" <out>/Assembly-CSharp.il | grep -B1 "end of method"
```

The second grep keeps the line above each `end of method` line, which is that method's `Code size: N` (the comment opens the body, and nothing else passes the first filter in between). The `end of method` lines use the bare type name. 14.2.7 sample: `ReferenceHub::get_PlayerId` 12 B, `get_IsDummy` 10 B, `get_IsHost` 25 B. Mono inlines bodies under 20 B of IL (and `AggressiveInlining` ones), so a Harmony patch on `get_PlayerId` can be bypassed by callers JIT-compiled earlier and one on `get_IsHost` cannot, unless the method carries `AggressiveInlining` (rule and caveats: scpsl-harmony-patching). Rows with the same name are overloads; match them by order or decompile the type.

## Common recipe mistakes

- Treating zero hits as "never runs" without checking the three zero-hit causes in recipe 1.
- `grep -rn` with a common word over the whole tree and pasting the result. Count first.
- Reading only the raise line. The consuming code is below it, and the args are built above it.
- Classifying frequency from the name. `Staying`, `Using` and `Updating` events are per-frame or per-pair in places.
- Stopping at `RegisterHandler` and forgetting `ReplaceHandler`.

## Re-verifying on a new build

- Re-run recipes 1, 4 and 5 on one event, one handler and one component each; if the output shape changed, fix the regex first.
- Re-check the naming convention (`<Category>Events.On<Name>(`) by running `scripts/event-sites.py` and reading the "no site" list.
- Re-run recipe 8 on three methods after every build; sizes move with refactors and the inline gate depends on them.
- Re-list `<Managed>` and the `-l` lists before claiming a BCL type is present or absent.
- Re-read the Harmony member names from the installed `0Harmony.dll` (`Patches`, `Patch.owner`, `Patch.priority`, `Patch.index`).
