---
name: scpsl-server-specific-settings
description: "Use when a SCP:SL LabAPI or EXILED plugin adds client-side settings, keybinds, sliders, dropdowns, buttons or text inputs through Server-Specific Settings (SSS, ServerSpecificSettingsSync, DefinedSettings, ServerOnSettingValueReceived), or when settings from another plugin vanish, a setting id collides, a handler fires many times, or a player is kicked after touching a setting."
---

# Server-Specific Settings (SSS)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

SSS puts server-defined controls in the player's Settings tab and reports values back. It is one global, unsynchronised, last-writer-wins static state shared by every plugin. LabAPI 1.1.7 has no wrapper or event; use `UserSettings.ServerSpecific.*` from the game assembly. Shared infrastructure: one registry, namespaced ids, defensive handlers.

## When to Use

- Adding a keybind, slider, dropdown, two-button toggle, text input, button, header or text area.
- Per-player or per-role settings views, reading a saved choice, live-updating a label.
- Symptoms: another plugin's settings vanish, the wrong plugin reacts, a player is kicked, a slider floods the handler, values read as defaults.

Not for in-play information (scpsl-hints-and-hud; text areas show only while the Settings tab is open) or commands (scpsl-commands-and-permissions).

## Quick Reference

| Need | Use |
|---|---|
| Entry types (8, fixed) | `SSKeybindSetting`, `SSSliderSetting`, `SSDropdownSetting`, `SSTwoButtonsSetting`, `SSPlaintextSetting`, `SSButton`, `SSGroupHeader`, `SSTextArea` |
| Define | `ServerSpecificSettingsSync.DefinedSettings` (one array, all plugins) |
| Send | `SendToAll()`, `SendToPlayer(hub[, subset])`, `SendToPlayersConditionally(pred)` |
| Receive | static event `ServerOnSettingValueReceived(ReferenceHub, ServerSpecificSettingBase)`, main thread |
| Read saved | `TryGetSettingOfUser<T>` (`GetSettingOfUser` creates a default) |
| Live update | `SendLabelUpdate`, `SendValueUpdate` (server-only entries), `SSTextArea.SendTextUpdate` |

Signatures, value properties, cost table: sss-api-reference.md. Registry, handlers, per-player views as code: sss-registry-pattern.md. All 23 pitfalls: sss-rules.md.

## Core Rules

1. **One registry owns `DefinedSettings` and `SendOnJoinFilter`.** It concatenates, rejects duplicates, resends. No other plugin assigns either.
2. **Ids** are an `int` shared by all plugins; the lookup key is (id, exact type). Use a hash you own over `"plugin:key"` (FNV-1a). Never small enums, never a `null` id (weak label hash).
3. **Handler line one:** ignore ids/types you do not own; the event is global.
4. **try/catch the whole handler.** An escaped exception makes Mirror disconnect the player who moved the control.
5. **Values are client data.** No check that the entry was sent to that player, no slider clamp, no rate limit. Authorise inside every handler, reject non-finite floats then clamp (`Mathf.Clamp` passes NaN), use `SyncSelectionIndexValidated`.
6. **Events fire per step:** every slider drag step, keybind press AND release (`SyncIsPressed`). `SyncDragging == false` is not a reliable final value. Debounce on a clock.
7. **Per-player views:** keep every entry in `DefinedSettings`; `SendToPlayer(hub, subset)` only controls visibility. Send the merged list (a client likely keeps only the last pack: unverified).
8. **Timing:** assign `DefinedSettings` in `Enable()` so later joiners are served. Already-connected players need an explicit send. Gate sends on `IsReady`, skip dummies. Client readiness for a pack sent in the first frames is unverified.
9. **Limits:** 255 entries per pack, 255 dropdown options; counts wrap silently, also for the full union.
10. **Released ids and types are immutable** (saved values are keyed by them). Bump `Version` only for new settings.
11. **Per-player text updates:** explicit `receiveFilter` AND `applyOverride: false`.
12. **Cost:** idle is near zero; a join pack is about 6 kB at 100 entries (estimate); no per-player send loops per event (O(P^2)).

## Common Mistakes

| Wrong | Right |
|---|---|
| `DefinedSettings = mine` from your plugin | Registry that concatenates and resends |
| `switch (SettingId)` over enum ids 0..5 | Namespaced hash ids; ownership and `is T` check first |
| Unguarded handler kicks the player | try/catch, rate-limited log |
| Staff action gated by "only staff got the entry" | Permission check in the handler |
| Using the slider value as sent | Reject NaN/Infinity, then clamp |
| `GetSettingOfUser` at join taken as the choice | Default until the first client message |
| Per-player entries outside `DefinedSettings` | Union in `DefinedSettings`, subset via `SendToPlayer` |
| `SendTextUpdate` with null filter | `h => h == target`, `applyOverride: false` |
| Hint with hardcoded key name | `SSKeybindHintParameter(id)` (scpsl-hints-and-hud) |

See reviewing-scpsl-plugins when auditing a plugin that uses SSS.
