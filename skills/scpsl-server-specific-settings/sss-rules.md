# SSS rules and pitfalls

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Each rule gives the symptom, the cause and the fix. "Unverified" = the client side is stripped from the server build, so client behaviour is inferred. Vanilla example classes named below ship in the game assembly and are public type names; their code is not reproduced here.

## Shared state

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 1 | Another plugin's settings vanish | `DefinedSettings` is one static array; assignment replaces it. The vanilla examples (e.g. `SSPagesExample`) assign it directly | One registry that concatenates and resends (EXILED's `Register` does the same). Never assign from a non-owner |
| 2 | A client's tab shows only one plugin's settings | `SendToPlayer(hub, list)` probably replaces the client's whole list (unverified; implied by the client refresh reading one list) | Always send the merged list for that player |
| 3 | Per-player settings do nothing when changed | The server accepts a response only if the id AND type exist in global `DefinedSettings` | Keep every entry in `DefinedSettings`; use `SendToPlayer(hub, subset)` for visibility only (the vanilla `SSPagesExample` pattern) |
| 4 | Joiners lose their filter or see the wrong entries | `SendOnJoinFilter` is one static `Predicate`; the vanilla `SSPrimitiveSpawnerExample` sets it on activate and nulls it on deactivate, erasing any other owner's | One owner; compose conditions in the registry |
| 5 | Settings reach nobody after hot reload / late enable | Join hook serves only players who join after `DefinedSettings` is set, and only if the filter admits them | Set `DefinedSettings` in `Enable()`; `SendToPlayer` for players already connected |

## Ids and persistence

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 6 | Wrong plugin reacts, wrong value read | Id is an `int` shared by all plugins. Lookup is first match by id + exact type. Same-type collisions merge definitions, events and client-saved values | Hash of `"plugin:key"` with a hash you own; check duplicates at registration; ignore events you do not own |
| 7 | Two entries share an id with no code collision | `null` id means a hash of the label with Mirror's weak 31-polynomial: equal labels collide, and so do strings like "Aa" and "BB" | Always pass an explicit id |
| 8 | `switch (SettingId)` hits the wrong setting | Handlers that ignore type conflate different-type settings with equal ids | Check `setting is T` and ownership first |
| 9 | Saved player values reset, or "new settings" banner nags | Client key embeds collection, type code and id; banner shows while `Version` differs | Never change id or type of a live setting. Bump `Version` only for genuinely new settings. `Version = 0` disables the banner. Label, range and option changes do not orphan values |

## Receiving

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 10 | A player is kicked when they use a setting | Mirror's handler wrapper catches an exception from the handler (including your subscriber) and disconnects the sender. A bad type code in the message does the same | try/catch the entire subscriber body. An uncaught throw also stops later subscribers (other plugins) from seeing that event |
| 11 | Handler runs a hundred times per drag | Slider sends every step with `SyncDragging = true`; keybinds send press and release | Debounce on a clock; branch on `SyncIsPressed`. `SyncDragging == false` arrives only on pointer-up or tab close, so typed and keyboard values stay `true`: do not wait for `false` |
| 12 | Players trigger staff-only actions | The server never checks the entry was sent to that player | Authorise by permission/role in every handler (the vanilla `SSPrimitiveSpawnerExample` does this inside its handler) |
| 13 | Out-of-range values | Slider is not clamped; the raw dropdown index comes from one unchecked byte and is not checked against the option count; floats are client data (NaN, Infinity) | Clamp and reject non-finite values (`Mathf.Clamp` returns NaN unchanged; Infinity is clamped to a bound, but reject it anyway), use `SyncSelectionIndexValidated`; plaintext is truncated to the definition's limit |
| 14 | CPU abuse | No rate limit; each message costs O(S) scans | Per-player token bucket, O(1) handler, defer heavy work to your clock |
| 15 | Defaults read as the player's choice right after join | `GetSettingOfUser` returns a default and stores it when nothing was received; the per-user list is empty until the client sends | Use `TryGetSettingOfUser`; treat values as unknown until the first event. EXILED hides the first value of non-button settings |
| 16 | Button reacts late or on hold, or header/text area never reports | `SSButton` sends on press, or after `HoldTimeSeconds` when set (`SyncLastPress` is a `Stopwatch` restarted per received press); `SSGroupHeader` and `SSTextArea` have `ResponseMode = None` and never raise the event | Set `holdTimeSeconds` deliberately; do not expect events from display-only entries, use a button or keybind |

## Sending

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 17 | One player's text appears for everybody | `SendTextUpdate`/`SendUpdate` with no filter go to all authenticated players; the vanilla `SSPrimitiveSpawnerExample` does exactly that | `receiveFilter = h => h == target` and `applyOverride: false`. The default `true` also rewrites the shared definition that joiners receive |
| 18 | Entries or options silently wrap, stream desyncs | Pack header count and dropdown option count are single bytes | At most 255 entries per pack and 255 options. Applies to the whole `DefinedSettings` union, which the join hook, `SendToAll`, `SendToPlayersConditionally` and `SendToPlayer(hub)` all serialise. If a paged union exceeds 255, keep `SendOnJoinFilter` returning false and never call whole-list senders |
| 19 | Sending "too early" | `SendToAll` skips only `Unverified` hubs (`SendToPlayersConditionally` and filtered updates do not even do that); `Player.IsReady` additionally needs the nickname set, and `Joined` fires when it is set | Gate your own sends on `IsReady`, skip dummies and the host. Whether the client accepts a pack in its first frames is unverified |
| 20 | O(P^2) traffic or CPU | Filtered update walks all hubs once per call; per-player calls multiply | One filtered call per change, or `SendToPlayersConditionally`; coalesce bursts (see scpsl-hints-and-hud for the same dirty-flag design) |
| 21 | Live text spam to players who cannot see it | Text areas show only while the Settings tab is open | Throttle with `IsTabOpenForUser` / `ServerOnStatusReceived`; re-push when the tab opens, skipped updates are not replayed. Text areas are not a HUD |

## Threading

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 22 | Collection modified / torn reads | `DefinedSettings` and the per-user stores are unsynchronised | Main thread only; do not reassign `DefinedSettings` while a handler iterates it. Inbound events already run on the main thread, so no marshalling is needed there |
| 23 | Wrong effect from a server-only entry | `isServerOnly` entries are not persisted by the client, but the client still reports edits | Do not rely on server-only for read-only; reassert the value with `SendValueUpdate` if you need it fixed |

## EXILED interop

If EXILED is also loaded, its `SettingBase.Register` appends to the same array and installs its own handler. Registering through it and assigning `DefinedSettings` directly in a LabAPI plugin erases one side. Pick one owner per server. See porting-exiled-to-labapi.

## Re-verifying on a new build

Same checklist as sss-api-reference.md. Additionally re-test rules 2, 11, 15 and 19 with a real client; those depend on stripped client code.
