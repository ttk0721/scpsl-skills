# Harmony on the server: mechanics and cost

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Harmony 2.2.2.0. Per-call costs below are structural (what executes), not measured; no profiler data exists for Harmony overhead. Treat every nanosecond figure as an estimate.

## Which Harmony, how it is loaded

| Fact | Status |
|---|---|
| Version is 2.2.2.0. Another copy in a dependencies folder would change everything below, so check yours. | verified (one server) |
| LabAPI neither uses nor bundles Harmony. It loads every `*.dll` from its `dependencies/global` and `dependencies/<port>` folders (byte load), before any plugin. | verified |
| EXILED loads its own copy from its own dependencies folder; the two files are byte-identical. | verified |
| Harmony keeps a shared registry (serialized patch info per patched method) in a generated type found by name. Whether the two byte-loaded copies resolve to one registry is not provable offline. If they are separate, `UnpatchAll(null)` would only reach one side. | UNVERIFIED |
| On Mono the runtime detours once, at patch time (no recompile tracking). | verified |
| Probe to settle sharing: compare `typeof(Harmony).Assembly` across two plugins, or compare `Harmony.GetAllPatchedMethods()` counts from both. | not run |

Do not ship another copy of `0Harmony.dll`; reference the existing one (`deploying-scpsl-plugins`).

## What one `Patch` call does

1. Takes a global lock, loads the stored patch info, appends your prefix/postfix/transpiler/finalizer.
2. Re-reads the **pristine** original IL, runs all transpilers in sorted order (each one's output is the next one's input), emits a new dynamic method, and writes a native jump at the original's entry.
3. Consequences:
   - n patches on one method cost n full rebuilds.
   - **Unpatching also rebuilds** and re-detours, even when zero patches remain. The registry never drops the key, so `GetAllPatchedMethods` keeps listing fully unpatched methods.
   - The original's native entry stays a jump for the rest of the process.
   - Your transpiler is re-run against whatever IL the others emit on **every** later rebuild of that method, so it must be stateless and idempotent.
4. Very short methods (IL under 16 B) on non-Windows get a dummy JIT first so the jump cannot overrun neighbouring code.
5. Patching runs on the calling thread, in practice the main thread, and blocks it: EXILED's 107 always-on patch classes take 0.6 s at startup. A lazy EXILED event patch mid-round is a frame hitch for whoever subscribed.

## Inlining

- Harmony sets Mono's NoInlining bit when it patches. A caller already JIT-compiled with the callee inlined keeps the inlined copy and **bypasses your patch**; a caller compiled later calls the patched method.
- Mono's JIT is lazy per method, and plugins enable before the first round, so "later" is the norm for anything not run before `Enable`.
- **Mono's inline gate (verified in the server's Mono binary):** callee with NoInlining or Synchronized flags is rejected; default size limit is 20 B of IL (`MONO_INLINELIMIT` overrides); `AggressiveInlining` bypasses the limit; inline depth over 10 is rejected. So **IL under 20 B can be inlined, 20 B or more cannot** (this is the size gate; other JIT conditions may still reject a small method).
- Measured IL sizes on 14.2.7 (for judging hazard; re-measure on updates):

| Method | IL size | Inlinable | Note |
|---|---|---|---|
| `PlayerEvents.OnValidatedVisibility` (LabAPI) | 12 B | yes | A patch applied after `WriteAll` compiled can be bypassed. |
| `PlayerRolesUtils.GetRoleId(ReferenceHub)` | 17 B | yes | Dozens of call sites; UCR patches it. |
| `PlayerRoleManager.get_CurrentRole` | 25 B | no | Hundreds of call sites; timing irrelevant, only the extra call and lookup cost. |
| `VisibilityController.ValidateVisibility` (base) | 51 B | no | Reached via `callvirt`; the overrides call it with a non-virtual `call`. |

- Re-validation at `WaitingForPlayers` detects missing or foreign patches. It cannot detect that a caller inlined your target earlier. Only a hit counter against an expected rate can.
- Diagnostic only, not exercised: `MONO_ENV_OPTIONS=-O=-inline`; `MONO_INLINELIMIT=0` (the variable is read by the runtime).

## Priority, order and prefix semantics

- `Priority`: Last=0, VeryLow=100, Low=200, LowerThanNormal=300, Normal=400, HigherThanNormal=500, High=600, VeryHigh=700, First=800. Attribute default (-1) becomes 400. Sort is higher first, ties by registration index. `[HarmonyBefore]`/`[HarmonyAfter]` add owner-id dependencies, which are fragile against ids with counters (EXILED appends one).
- None of EXILED, UCR, UCI, ProjectMER, ZAMERT sets a priority on the versions checked, so on a shared target order is **who patched first**; for example UCI, UCR, MER, ZAMERT, then EXILED's always-on patches, then EXILED's lazy event patches (when a plugin first subscribes), then yours if you apply at `WaitingForPlayers`.
- A prefix "affects the original" if it returns `bool` or has an `out`/`ref`/class-typed parameter other than `__instance`, `__originalMethod`, `__state`. Such prefixes are gated by `runOriginal`: once any returns false, later affecting prefixes do not run. Void prefixes with only `__instance`/value-type parameters always run.
- When `runOriginal` is false the original body, **including every transpiler's injected code**, is skipped. Postfixes still run, so a postfix must tolerate a default `__result`.
- Finalizers wrap the whole thing in try/catch; there is none unless you ask for one. A throwing prefix or postfix propagates into game code.
- `__args` allocates an `object[]` per call. Struct `__result` boxing can allocate in some forms.
- `Priority.Last` means "after everything of higher priority". Two plugins both at `Last` fall back to registration order.

## Declared members, virtuals, generics

- Attribute patches resolve with `AccessTools.DeclaredMethod`, and `Patch()` rejects non-declared members. Patching a base virtual does not touch an override's own body.
- A base patch is reached by every override that calls the base with a non-virtual call. All four `ValidateVisibility` overrides (939, 096, 1507, zombie visibility controllers) do, so the base is one choke point. It can force *invisible* globally; it cannot force *visible* where an override still says no after the base returns true.
- Interface and abstract members have no body: patch the implementing override. Static abstract interface members: UNVERIFIED on this Mono.
- `EventManager.InvokeEvent<TEventArgs>` constrains to reference types, so Mono compiles one shared body for all event types; a patch there hits every LabAPI event. Harmony's own docs warn that generic methods are shared and inlined methods cannot be patched.

## Duplicates and owner-blind removal

- `Patch` has no dedup. Registering the same patch method twice, even under different owners, runs it twice, and the sorter emits the duplicates. ProjectMER does this with two Harmony instances in one `Enable` (version 2026.7.6.1); for a prefix that always returns false the second copy is skipped at run time, others run twice.
- `UnpatchAll(id)` removes each matching patch through the patch method, and the removal filters by patch method only, not owner. Another owner who registered the **same patch method** (a second copy of your plugin, a duplicate `PatchAll`) loses it too. `Unpatch(original, patchMethod)` is owner-blind in the same way.
- A patch's method is re-resolved after deserialization by scanning loaded modules for its module id. A plugin DLL loaded twice (same module id) resolves to the first one found: consequence UNVERIFIED.

## Cost model

| Item | Cost | Status |
|---|---|---|
| Patch or unpatch | IL re-read, all transpilers, emit, JIT of original and replacement, per call, on the calling thread. | structural |
| `GetPatchInfo(m)` | Global lock, one binary deserialize, a new `Patches` object. Every `Patch.PatchMethod` access scans all loaded modules. | structural |
| `GetAllPatchedMethods()` | Copies the key array under the lock, no deserialization. | structural |
| `HasAnyPatches(id)`, `GetPatchedMethods()`, `UnpatchAll(...)` | Deserialize every patched method. | structural |
| Native jump into the replacement | One jump plus one static call per prefix/postfix. | estimate: a few ns per call, unmeasured |
| Lost inlining | Dominant cost for tiny hot getters: every call site pays a call. | structural; magnitude unmeasured |
| Per-pair patch at scale | At 100 players, `N*(N-1)` = 9,900 pairs per send tick, 60 ticks/s = 594,000 pairs/s. A 50 ns body is ~30 ms/s, 100 ns is ~60 ms/s (arithmetic on an assumed body cost). | arithmetic |

`GetPatchInfo` also blocks while another thread is patching; EXILED's lazy `Subscribe` holds the lock for the whole rebuild. So poll only your own few targets, read only `owner`/`index`/`priority`, and not per frame.

Harmony debug file logging turns on with the `HARMONY_DEBUG=1` environment variable: test servers only.

## Re-verifying on a new build

- Re-decompile or re-dump the pristine IL of every target you patch and compare against your fingerprint (see patch-recipes.md); a changed fingerprint is the signal.
- Re-measure IL size of every tiny method you rely on (`ilspycmd`/`monodis` on `Assembly-CSharp.dll`); the 20 B gate is a Mono property, so re-check only if the Unity/Mono build changes (`MONO_INLINELIMIT` string in the runtime library).
- Check `0Harmony.dll` version and hash in LabAPI's and EXILED's dependencies folders; `Harmony` assembly version in the boot log.
- Re-run the shared-registry probe (UNVERIFIED item above) once per LabAPI or EXILED major update.
- Grep your plugin set for `UnpatchAll(` and `Unpatch(` with `null` or `"*"`.
- Confirm LabAPI plugin loader still orders by priority and that EXILED's loader priority is still 255.
