# Who else patches your targets

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Versions covered: EXILED 9.14.2, UncomplicatedCustomRoles (UCR) 9.5.0, UncomplicatedCustomItems (UCI) 4.0.5, ProjectMER 2026.7.6.1, ZAMERT. Versions move; re-check the installed binaries, not the source tree.

## Rules of the neighbourhood

- Patching is global. Anyone's `UnpatchAll(null)` removes yours (assuming one shared registry, which is UNVERIFIED across the two Harmony copies, see harmony-on-the-server.md), and nothing re-applies the victims.
- Nobody sets priorities, so order is registration order. Example load order: UCI, UCR, MER, ZAMERT, then EXILED always-on patches, then EXILED lazy event patches, then late plugins.
- Your own scoped `UnpatchAll(id)` is owner-blind for a patch method another owner registered too.

## Who calls an unscoped unpatch

| Component | Where it happens | Trigger |
|---|---|---|
| UCR | `UnpatchAll(null)` in its disable path; also `Unpatch(method, All, "*")` on every `PlayerEvents.On*` method it patched | Its own disable/reload |
| UCI | `UnpatchAll(null)` in disable; also reachable from a remote "recalled version" flag, and from the "both EXILED and LabAPI UCI installed" conflict | Disable; remote flag |
| ProjectMER | `UnpatchAll(null)` in disable | Disable |
| Exiled.CustomItems | `UnpatchAll(null)` from `OnDisabled` | `exiled reload plugins`, `exiled pm disable` |
| EXILED itself | scoped `UnpatchAll(its id)` | Reload |

A LabAPI plugin gets no notification of any of this. Only EXILED code sees its `Server.ReloadedPlugins` event. LabAPI itself never calls `Plugin.Disable()`, so the practical triggers are administrative commands and plugin self-disable.

EXILED's own recovery after a foreign wipe: its event patches are applied once on first `Subscribe`, and the per-event "patched" flag is a static that is never reset. After a foreign `UnpatchAll(null)`, EXILED will not re-patch those events. Whether `exiled reload plugins` repairs that is UNVERIFIED (depends on whether the second byte-load returns the already-loaded assembly). Treat a restart as the only certain repair.

## EXILED's patch set (installed 9.14.2)

344 patch classes by class-level attribute count (heuristic, plus or minus a few): 107 always on, applied at startup; the rest applied lazily on the first `Event.Subscribe` (`use_dynamic_patching: true`). A class can patch several methods, so these are class counts, not method counts.

Hot targets it patches:

| Target | EXILED patch | Anchored on |
|---|---|---|
| `FpcServerPositionDistributor.WriteAll` | ghost-mode transpiler | A fixed offset after the call to `VisibilityController.ValidateVisibility`, with two hard-coded local indices. If the call is gone, the offset falls back to a bogus index and emits corrupt IL. |
| `VoiceTransceiver.ServerReceiveMessage` | two transpilers (receiving-voice, chatting) | LabAPI's own `PlayerReceivingVoiceMessageEventArgs` constructor call and a hard-coded local |
| `Scp939VisibilityController.ValidateVisibility` | event transpiler | |
| `RadioItem.Update`, `UsableItemsController.Update`, `TeslaGateController.FixedUpdate`, `HitscanHitregModuleBase.ServerAppendPrescan`, `SimpleInspectorModule.ServerProcessCmd`, `DrawAndInspectorModule.ServerProcessCmd` | transpilers / prefixes | |

Not patched by EXILED: `PlayerRoleManager.get_CurrentRole`, `PlayerRolesUtils.GetRoleId`, base `VisibilityController.ValidateVisibility`.

EXILED source 9.15.0 differs from installed 9.14.2: a second `WriteAll` transpiler (role-sync extras, anchored on a net-id comparison and an `IFpcRole` type test) exists in source and not in the 9.14.2 binary. Always inspect the installed DLL, and re-test when the server updates EXILED.

Hitch warning: a plugin that subscribes to an EXILED event mid-round pays the patch on the calling thread. EXILED also enables after every LabAPI plugin, so a LabAPI plugin cannot subscribe to EXILED events in `Enable()`; do it from `ServerEvents.PluginsEnabled` or `WaitingForPlayers`.

## Third-party footprints that multiply your costs

- **UCR** (9.5.0): a prefix on essentially every `PlayerEvents.On*` method (about 186 exist; 183 are patched), on `PlayerRoleManager.get_CurrentRole` (hundreds of IL call sites), on `PlayerRolesUtils.GetRoleId`, plus a handful of others. Its per-event prefix enumerates a `ConcurrentDictionary`'s `Values`, which takes all lock stripes and allocates a list per call. Its `GetRoleId` prefix walks the stack for disguised players. Whether that prefix really runs inside `WriteAll` per pair depends on JIT order (UNVERIFIED; settle with a hit counter against `N*(N-1)*tickrate`).
- **UCI**: about 50 patch methods, several overlapping EXILED targets (inspect modules, `Scp1344NetworkHandler.TryInspect`, `ServerConsole.ReloadServerName`).
- **ProjectMER**: two Harmony instances plus `PatchAll` in one `Enable`, so duplicates; the first instance reference is overwritten.
- **PluginModules**: scoped `UnpatchAll(id)`. **ZAMERT**: fixed id. These are the pattern to copy.

If a UCR-style prefix does run per pair, the arithmetic is one allocating call per pair, up to 594,000 per second at 100 players and 60 ticks (estimate, UNVERIFIED that it runs there). Budget your patch on top of that (`scpsl-engine-cost-model`).

## Re-verifying on a new build

- Dump patch owners at runtime: for each hot target (`WriteAll`, `ServerReceiveMessage`, `get_CurrentRole`, `GetRoleId`, `ValidateVisibility`, `OnValidatedVisibility`) call `Harmony.GetPatchInfo` and list `owner`, `priority`, `index` per category. Compare to this file.
- Grep every installed plugin DLL (or its source) for `UnpatchAll`, `Unpatch(` and the `"*"` id.
- Compare EXILED's installed version with its source tag before trusting a source reading.
- Re-count EXILED patch classes with `GetAllPatchedMethods()` rather than reading attributes.
- Re-run the shared-registry probe from harmony-on-the-server.md.
