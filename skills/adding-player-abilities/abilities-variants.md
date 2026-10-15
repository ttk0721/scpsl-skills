# Ability variants

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of adding-player-abilities. Each section says what changes against the example in `examples/adding-player-abilities/`; the guard chain in SKILL.md stays. Client-side behaviour is stripped from the server build, so statements about what the client sends are marked unverified.

## Hold-style abilities (sprint, shield, channel)

Press and release both arrive on the same event, so `SyncIsPressed` is a level. Start on `true`, stop on `false`. Three ways the release never comes: the player dies, leaves, or changes role while holding. Stop the ability on `PlayerEvents.Left`, `PlayerEvents.ChangedRole` and `WaitingForPlayers`, and give every held ability a maximum duration enforced by the same `SlClock` job that serves the ready notices. Keep the held set in a plain `Dictionary<string, long>` of `UserId` to start tick; the job walks it. Do not start a coroutine per hold.

The per-player token bucket still applies, but size its burst for press plus release (two events per use).

## Toggles

Flip a per-player flag on the press edge. Store it in `PlayerAbilityState`, clear it in the same places as a hold. A toggle that drains something each second is one more `SlClock` job over the set of active players, not one timer per player.

## Charges instead of a cooldown

`SlKit.Core.TokenBucket(capacity: charges, refillPerSecond: 1 / secondsPerCharge, now)` already is a charge counter: `TryTake` spends one, refill is lazy, nothing runs between uses. Replace `ReadyAt` with one bucket per ability. Show the count in the hint, not in the settings tab.

## Staff-only or role-specific entries

Keep every entry in the global `DefinedSettings` union (the server drops responses for ids not in it), set `SendOnJoinFilter` in exactly one place, and control visibility with `SendToPlayer(hub, subset)` where the subset is the merged list for that player. The code is in the sss-registry-pattern.md of scpsl-server-specific-settings. Visibility is cosmetic: the handler still checks permission, because the server never checks whether the entry was sent to that player. The union must stay under 255 entries.

## Showing cooldowns in the settings tab

Avoid it. A per-player label update is one filtered send per player per change, which is O(players squared) if done per event, and the text area or label is visible only while the Settings tab is open. If it is required: explicit `receiveFilter` for that one player, `applyOverride: false`, skip players whose tab is closed (`IsTabOpenForUser`), and re-push when it opens. In play, use hints.

## Changing entries after release

Label, hint, suggested key and ranges can change freely. Changing an id or the entry type orphans every player's saved binding. Bump `ServerSpecificSettingsSync.Version` only for genuinely new settings; it triggers the "new settings" notice on every client. `Version` is one static shared by all plugins: coordinate with whoever owns the SSS setup.

The example builds its entries once in `Enable()`. Config values that appear in entry text (the hint line) change only after a plugin reload; values read inside handlers change at once.

## Servers sharing bindings

`collectionId` 0 to 19 scopes a client's saved binding to the account instead of one server, so a player keeps their key across the owner's servers. How "account" is derived is unverified. Default (255) scopes per server.

## EXILED on the same server

EXILED's settings registration appends to the same array and installs its own handler. A LabAPI plugin that assigns `DefinedSettings` outright erases EXILED plugins' entries and the reverse. Appending, as the example does, survives both; two plugins appending the same id do not, which the collision check reports.

## Unverified

- Whether a client sends an initial value for each keybind right after it receives the pack. If it does, the first event per key may have `SyncIsPressed == false`. The press-edge gate makes this harmless.
- Whether a client accepts a pack sent in its first frames after `Joined`. If fast joiners miss the entries, delay the send with an `SlClock.After` job, not a coroutine, and test with a real client.
- Which keys the client allows while a menu is open: `preventInteractionOnGui` is a client-side flag.

## Re-verifying on a new build

- Compile `examples/adding-player-abilities` against the new assemblies: `SSKeybindSetting` constructor parameters (`allowSpectatorTrigger`, `preventInteractionOnGui`, `hint`), `SSGroupHeader`, `SyncIsPressed`, `SendToPlayer`, `SSKeybindHintParameter`, `TimespanHintParameter.FromOffset`.
- Check that `Player.ReadyList`, `HasPermissions(params string[])`, `IsAlive`, `IsSCP`, `Heal`, `EnableEffect<T>` still exist and mean the same.
- With a real client: press and release each key, hold it, rebind it, rejoin during a cooldown, and run a second SSS plugin beside it.
