# Patch recipes: lifecycle, fingerprint, transpiler, fault containment, watchdog

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). All code below is original and illustrative: `GameType`, `"TargetMethod"` and `Helper` are placeholders you must replace after resolving a real target, and no snippet has been compiled against the game. Names from SlKit (`SlClock.Install`, `SlClock.Every`) follow the SlKit README; the watchdog uses it only for the timer and works with any wall-clock scheduler (`scpsl-main-thread-and-timing`).

## 1. Lifecycle skeleton

Fallible work first, subscribe last, idempotent re-entry. Nothing ever calls `Disable()`, so flush in `Shutdown`.

```csharp
using System;
using HarmonyLib;
using LabApi.Events.Handlers;
using LabApi.Loader.Features.Plugins;
using SlKit;
using Logger = LabApi.Features.Console.Logger;   // avoids a name clash if you use UnityEngine

public sealed class MyPlugin : Plugin
{
    public override string Name => "MyPlugin";
    public override string Description => "Harmony skeleton";
    public override string Author => "you";
    public override Version RequiredApiVersion => new Version(1, 1, 7);

    private static bool _started;

    public override void Enable()
    {
        SlClock.Install();
        Patcher.Apply("enable");                    // may log and leave Active=false; must not throw
        if (_started) return;                       // a repeated Enable re-applies but never double-subscribes
        _started = true;
        SlClock.Every(TimeSpan.FromSeconds(30), () => Patcher.Verify("timer"), "myplugin.patch-watch");
        ServerEvents.WaitingForPlayers += OnWaiting; // subscribe last
        ServerEvents.Shutdown += OnShutdown;
    }

    public override void Disable() { }              // never called by LabAPI or the game

    private static void OnWaiting() => Patcher.Verify("waiting-for-players");
    private static void OnShutdown() => Patcher.Report();
}
```

Keep `Enable()` idempotent in case anything runs it twice (LabAPI itself does not, as far as the loader code shows): `Apply` starts with `UnpatchAll(Id)`, and the `_started` flag stops duplicate subscriptions and timers.

## 2. Resolve, fingerprint, apply

```csharp
using System.Collections.Generic;
using System.Reflection;
using System.Text;

internal static class Patcher
{
    // Fixed, unique to you. Never reuse another plugin's id, never append timestamps.
    internal const string Id = "myorg.myplugin.core";

    // Updated by you after reading the log line printed on first run against a known-good build.
    private const ulong ExpectedFingerprint = 0UL;   // 0 = "learning": log the value, do not patch

    internal static volatile bool Active;            // hot-path code reads this, never the registry
    internal static long Hits;                       // main thread only: plain increment is enough
    private static Harmony _harmony;
    private static MethodBase _target;
    private static string _lastOwners = "";

    internal static void Apply(string reason)
    {
        try
        {
            Active = false;
            _harmony = _harmony ?? new Harmony(Id);
            _harmony.UnpatchAll(Id);                 // idempotent even on a fresh process: registration has no dedup
            _target = AccessTools.DeclaredMethod(typeof(GameType), "TargetMethod",
                new[] { typeof(int), typeof(bool) }); // FULL parameter list; null if it does not exist
            if (_target == null) { Logger.Error("[myplugin] target not found, patch refused"); return; }

            ulong fp = Fingerprint(_target);
            if (fp != ExpectedFingerprint)
            {
                Logger.Error($"[myplugin] IL fingerprint {fp:X16} differs from expected, patch refused ({reason})");
                return;                              // fail closed, loudly
            }

            _harmony.Patch(_target,
                transpiler: new HarmonyMethod(typeof(Patcher), nameof(Transpile)) { priority = Priority.Last });
            Active = true;
        }
        catch (Exception e) { Active = false; Logger.Error($"[myplugin] apply failed: {e}"); }
    }

    // Normalised opcode + operand NAME, not raw metadata tokens (they change between builds).
    internal static ulong Fingerprint(MethodBase m)
    {
        ulong h = 14695981039346656037UL;            // FNV-1a 64
        foreach (CodeInstruction ins in PatchProcessor.GetOriginalInstructions(m))
        {
            string op = ins.opcode.Name;
            string operand = ins.operand is MemberInfo mi ? mi.DeclaringType + "::" + mi.Name
                           : ins.operand is Type t ? t.FullName
                           : ins.operand is int || ins.operand is long || ins.operand is string ? ins.operand.ToString()
                           : "";                      // labels and locals are index-dependent: skip
            foreach (char c in op + " " + operand + ";") { h ^= c; h *= 1099511628211UL; }
        }
        return h;
    }
}
```

`GetOriginalInstructions` returns the pristine IL; `GetCurrentInstructions` returns the IL after every owner's transpiler. A pristine fingerprint tells you the game did not change; it says nothing about what EXILED emitted. Check that with `GetCurrentInstructions` at `WaitingForPlayers` if your transpiler depends on it.

## 3. One-for-one transpiler with a hit-count guard

Replace a call by a helper with the same stack effect. Match by operand, never by index. If the count is not exactly what you expect, return the input unchanged and say so.

```csharp
internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> input)
{
    List<CodeInstruction> original = new List<CodeInstruction>(input);
    List<CodeInstruction> result = new List<CodeInstruction>(original);

    MethodInfo from = AccessTools.DeclaredMethod(typeof(GameType), "Callee", new[] { typeof(int) });
    MethodInfo to = AccessTools.DeclaredMethod(typeof(Helper), nameof(Helper.CalleeReplacement));
    if (from == null || to == null) { Logger.Error("[myplugin] transpiler anchors unresolved"); return original; }

    int hits = 0;
    for (int i = 0; i < result.Count; i++)
    {
        if (!result[i].Calls(from)) continue;
        CodeInstruction replacement = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, to);
        replacement.MoveLabelsFrom(result[i]);       // keep jump targets valid
        result[i] = replacement;
        hits++;
    }
    if (hits != 1)
    {
        Logger.Error($"[myplugin] expected 1 anchor, found {hits}; target left unpatched");
        return original;
    }
    return result;
}
```

Rules for the replacement: identical stack signature (same inputs, same output type); add no locals and move no instructions in front of the replaced one, so another owner's index-based anchors still line up. On `FpcServerPositionDistributor.WriteAll`, EXILED's ghost-mode transpiler inserts a fixed number of instructions after the call to `VisibilityController.ValidateVisibility` and assumes two specific locals. Do not delete or reshape that window, and start your own replacement after it. The `ValidatedVisibility` LabAPI event is raised after EXILED's hook ran, so EXILED's ghost state already flows into it.

Design for a visibility replacement (not compiled or run): the code that builds the event args, raises `ValidatedVisibility` and reads `IsVisible` back is a four-instruction run (construct, `dup`, raise call, getter call) that takes three inputs and leaves one `bool`. One static call with that stack effect replaces all four. Match the whole run by operands, `dup` included, since a pattern that forgets it unbalances the stack. The helper must still raise the event whenever anyone has subscribed, or UCR, ProjectMER and other subscribers silently stop working. Start after EXILED's window, add no locals, and keep the hit-count guard.

## 4. Fault containment in a patch body

An exception in a prefix, postfix or helper propagates into game code. If the host is a per-pair method inside the position sender, one throw can stop position sync for everyone for that tick, every tick.

```csharp
internal static class Helper
{
    private static int _faults;
    private static long _windowStart;

    // Same parameters and return type as GameType.Callee (instance call: receiver first), so the stack effect matches.
    internal static bool CalleeReplacement(GameType self, int arg)
    {
        if (!Patcher.Active) return self.Callee(arg);
        try
        {
            Patcher.Hits++;
            return self.Callee(arg);                 // your logic here: no allocation, no LINQ, no stack walk
        }
        catch (Exception e)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _windowStart > System.Diagnostics.Stopwatch.Frequency * 60) { _windowStart = now; _faults = 0; }
            if (++_faults == 1) Logger.Error($"[myplugin] patch body threw: {e}");   // log the first one only
            if (_faults >= 5) { Patcher.Active = false; Logger.Error("[myplugin] patch self-disabled after repeated faults"); }
            return self.Callee(arg);                 // fall back to vanilla behaviour
        }
    }
}
```

`Active = false` makes the helper return the vanilla value without unpatching, so disabling never needs a rebuild. A Harmony finalizer is the alternative when you cannot edit the body.

A **postfix** must tolerate a default `__result` because it also runs when another owner's prefix skipped the original. A **void prefix** with only `__instance` and value-type parameters is the polite prefix: it neither depends on nor changes `runOriginal`. A prefix returning `false` is the only way to skip expensive original work; never do it on a method others transpile.

## 5. Conflict probe and watchdog

Read only `owner`, `index`, `priority`; avoid `Patch.PatchMethod` outside `Enable` because it scans every loaded module.

```csharp
// in Patcher
internal static void Verify(string reason)
{
    try
    {
        if (_target == null) { Apply(reason); return; }          // never applied: try again
        Patches info = Harmony.GetPatchInfo(_target);              // null if nobody ever patched it
        bool mine = info != null && Owns(info.Transpilers);        // check the category you used
        string owners = Owners(info);
        if (owners != _lastOwners) { Logger.Info($"[myplugin] owners on target ({reason}): {owners}"); _lastOwners = owners; }
        if (!mine)
        {
            Logger.Warn($"[myplugin] patch missing ({reason}); someone called UnpatchAll(null)? re-applying");
            Apply(reason);
        }
    }
    catch (Exception e) { Logger.Error($"[myplugin] verify failed: {e}"); }
}

private static bool Owns(IEnumerable<Patch> list)
{
    foreach (Patch p in list) if (p.owner == Id) return true;
    return false;
}

private static string Owners(Patches info)
{
    if (info == null) return "(none)";
    StringBuilder sb = new StringBuilder();
    foreach (Patch p in info.Prefixes) sb.Append("pre:").Append(p.owner).Append('/').Append(p.priority).Append(' ');
    foreach (Patch p in info.Transpilers) sb.Append("tr:").Append(p.owner).Append('/').Append(p.priority).Append(' ');
    foreach (Patch p in info.Postfixes) sb.Append("post:").Append(p.owner).Append('/').Append(p.priority).Append(' ');
    return sb.ToString();
}

internal static void Report() => Logger.Info($"[myplugin] shutdown: active={Active} hits={Hits}");
```

Where this runs:

| When | Why |
|---|---|
| `Enable()` | First apply; also log foreign prefixes with a `bool` return as a warning (inspect return types once, here only). |
| `WaitingForPlayers` | EXILED's lazy patches and other late subscribers are usually in; catches a wipe since the last round. |
| Slow timer, 30 s or slower, own targets only | Covers a mid-round wipe. A pure LabAPI plugin gets no signal; 1 Hz is not needed and costs a global lock and a deserialize per target. |

Re-applying mid-round rebuilds the method: a one-time hitch plus, for a tiny method, no protection against callers that already inlined it. Log it. Watchdog limits: it detects removed or foreign patches; it cannot detect an earlier inlining bypass. The hit counter does: compare `Hits` per second against the rate you expect (for a per-pair patch, about `N*(N-1)*tickrate`), via your own periodic log. A patch whose counter stays at zero is not running.

## 6. When Harmony is the wrong tool

- An event exists: use it. `scpsl-lint` SL001 flags subscriptions to hot events (per pair, per frame, per voice packet) are expensive to subscribe to; if you must, make the handler a flag test and subscribe only while active. See `labapi-events`.
- You only want to read state: a public field or wrapper member beats any patch.
- The target is under 20 B of IL: patch the caller instead.
- A **prefix on a per-pair private method taking a remembered signature** is the pattern that fails most: resolve with `DeclaredMethod`, print the resolved `MethodBase`, and confirm the variable you want to change is a parameter and not an IL local.

## Re-verifying on a new build

- On the first run against a new game build, start with `ExpectedFingerprint = 0` and read the printed value for every target; compare `GetOriginalInstructions` output to the previous build to see what changed before accepting a new fingerprint.
- Re-resolve every `AccessTools.DeclaredMethod` call: a changed parameter list returns null.
- Check that every anchor your transpiler matches still occurs exactly the expected number of times, and that EXILED's anchors on the same method are still intact (`GetCurrentInstructions`).
- Run `scpsl-lint` over your sources (SL017 flags `return false` prefixes on `WriteAll`, `ValidateVisibility`, `OnValidatedVisibility` and `get_CurrentRole` only).
- Re-run with EXILED, UCR and UCI installed; test shot audio, voice, role sync and 939/049 behaviour when patching visibility (`scpsl-networking-for-plugins`).
