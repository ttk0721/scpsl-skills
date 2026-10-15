# Harmony troubleshooting: symptom, cause, fix

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

| Symptom | Cause | Fix |
|---|---|---|
| Custom roles, MER schematics, UCI items and EXILED events all vanish after `exiled reload plugins` (with Exiled.CustomItems installed) or after UCI marks itself recalled | `UnpatchAll(null)` from UCR, UCI, ProjectMER or Exiled.CustomItems removes every owner's patches (if the two Harmony copies share a registry: UNVERIFIED). EXILED's per-event "patched" flag never resets, so it does not re-patch. | Re-verify and re-apply your own patches at `WaitingForPlayers` and on a slow timer; make re-apply idempotent. A restart is the only certain repair for EXILED's events. |
| Patch "does nothing" for some call sites | A caller was JIT-compiled before the patch and inlined the callee (possible only for callees under 20 B IL or with `AggressiveInlining`). | Patch at `Enable`; patch the callers instead; count hits against the expected rate. Diagnostic: run the server with `MONO_INLINELIMIT=0` (not exercised). |
| Event never fires, or code after your prefix never runs | Another owner's prefix returned false: the whole original body, including transpiler-inserted event raising, is skipped; postfixes still run. | Use void prefixes with `__instance`/value parameters, or postfixes. If you must skip, say so in your docs and set priority deliberately. |
| `InvalidProgramException` or `ArgumentOutOfRangeException` in position sync or voice after adding a transpiler or after a game update | EXILED transpilers hard-code offsets and locals (ghost-mode on `WriteAll`; receiving-voice and chatting on `VoiceTransceiver.ServerReceiveMessage`). A missing anchor in the ghost-mode one turns into a bogus index, with no guard. | `Priority.Last`; do not delete or reshape the instructions EXILED anchors on; fingerprint pristine IL and refuse on mismatch; check with `GetCurrentInstructions`. |
| `PatchAll` throws in `Enable`, or the patch silently does nothing | The signature was remembered, not resolved: wrong parameter list, a parameter that is actually an IL local, or an overload that is never called. | `AccessTools.DeclaredMethod` with full parameter types, null check, print the resolved method; for decisions held in a local, use the event or a call-site transpiler. |
| Patch applied twice (callback runs twice per call) | No dedup in registration; two `PatchAll` calls or two Harmony instances (ProjectMER does this). | One `PatchAll` per `Enable`, `UnpatchAll(Id)` first, assert owner count with `GetPatchInfo`. |
| Reload leaves old patches running | New Harmony instance each time with a timestamp or counter id, while the old instance's cleanup is `UnpatchAll(null)`. | One stable id, `UnpatchAll(Id)`. |
| Your patch is gone after someone else unpatched their own copy of the same method | Removal filters by patch method, not owner; another owner who registered the same patch method (a second copy of your plugin) took it down. | Never ship two copies; keep the patch class private to one assembly. |
| Hitch when a plugin subscribes to an EXILED event mid-round | First `Subscribe` applies the patch on the calling thread, rebuilding under the global lock. | Subscribe at `PluginsEnabled` or `WaitingForPlayers`. |
| Frame time rises after you probe | `GetPatchInfo` takes the global lock and deserializes; `Patch.PatchMethod` scans all modules; `HasAnyPatches`/`GetPatchedMethods` deserialize everything. | Own targets only, 30 s or slower, read `owner`/`index`/`priority` only. |
| Disguised-role code throws only in patched callers | A third-party prefix stack-walks and reads declaring types of frames; frames of patched methods become Harmony dynamic methods. How Mono reports those frames is UNVERIFIED. | Do not stack-walk in patches. Pass context with a thread-static flag set by a prefix on the caller, or `__originalMethod`. If a walk is unavoidable, use `Harmony.GetOriginalMethodFromStackframe`, not `GetMethodFromStackframe` (that returns the replacement). |
| Source says one thing, server does another | EXILED source tag differs from the installed DLL. | Verify against the installed binary and the boot log's version line. |
| Harmony writes debug files | `HARMONY_DEBUG=1` is set. | Test servers only. |

## Re-verifying on a new build

- Re-run the owner dump (`who-else-patches.md`) and compare the table above to what you see.
- Re-check that `GetPatchInfo` still returns `owner`, `index`, `priority` as public fields.
- Re-test one `UnpatchAll(null)` scenario (`exiled reload plugins` with Exiled.CustomItems installed) against your watchdog.
