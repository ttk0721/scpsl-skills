# SSS API reference

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Namespace `UserSettings.ServerSpecific` (game assembly; LabAPI has no wrapper). "Unverified" = inferred, not confirmed: the dedicated-server build has the client-side SSS code stripped, so anything about what the CLIENT does with a pack is unverified.

## How it works

- Definitions live in static `ServerSpecificSettingsSync.DefinedSettings` (one array, all plugins). The server sends a pack (`int Version`, `byte count`, then entries) to players; the client draws them in its Settings tab and saves values locally.
- The client sends a response (type code, id, payload) on every change. The server checks that an entry with that id AND exact type exists in global `DefinedSettings`, finds or creates the per-user instance, deserialises, and raises `ServerOnSettingValueReceived`.
- Join: when a player's authentication mode changes, the built-in hook sends the full list unless `SendOnJoinFilter` is set and returns false. So if `DefinedSettings` is already assigned, joiners are served automatically.
- `SendToAll()` packs once and writes the same bytes to every non-`Unverified` hub (not gated on `IsReady`). `SendToPlayer(hub, collection, versionOverride)` packs per call. `SendToPlayersConditionally(filter)` packs once like `SendToAll` but, unlike it, does not skip `Unverified` hubs: your predicate must.
- Per-player state (`ReceivedUserSettings`, `ReceivedUserStatuses`) is cleared when the player is removed.

## Static members

```csharp
static ServerSpecificSettingBase[] ServerSpecificSettingsSync.DefinedSettings { get; set; }
static Predicate<ReferenceHub> ServerSpecificSettingsSync.SendOnJoinFilter { get; set; }  // single slot
static int ServerSpecificSettingsSync.Version;      // defaults to 1; 0 disables the "new settings" banner
static event Action<ReferenceHub, ServerSpecificSettingBase> ServerOnSettingValueReceived;
static event Action<ReferenceHub, SSSUserStatusReport> ServerOnStatusReceived;   // tab open/close
static void SendToAll();
static void SendToPlayersConditionally(Func<ReferenceHub, bool> filter);
static void SendToPlayer(ReferenceHub hub);
static void SendToPlayer(ReferenceHub hub, ServerSpecificSettingBase[] collection, int? versionOverride = null);
static T GetSettingOfUser<T>(ReferenceHub hub, int id);          // creates + stores a default if none received
static bool TryGetSettingOfUser<T>(ReferenceHub hub, int id, out T setting);   // linear scan of that user's list
static bool IsTabOpenForUser(ReferenceHub hub);
static int GetUserVersion(ReferenceHub hub);
```

## Entry constructors

| Entry | Constructor (defaults omitted where obvious) |
|---|---|
| Keybind | `SSKeybindSetting(int? id, string label, KeyCode suggestedKey = None, bool preventInteractionOnGui = true, bool allowSpectatorTrigger = true, string hint = null, byte collectionId = 255)` |
| Dropdown | `SSDropdownSetting(int? id, string label, string[] options, int defaultOptionIndex = 0, DropdownEntryType entryType = Regular, string hint = null, byte collectionId = 255, bool isServerOnly = false)` |
| Two buttons | `SSTwoButtonsSetting(int? id, string label, string optionA, string optionB, bool defaultIsB = false, string hint = null, byte collectionId = 255, bool isServerOnly = false)` |
| Slider | `SSSliderSetting(int? id, string label, float min, float max, float defaultValue = 0, bool integer = false, string valueToStringFormat = "0.##", string finalDisplayFormat = "{0}", string hint = null, byte collectionId = 255, bool isServerOnly = false)` |
| Plaintext | `SSPlaintextSetting(int? id, string label, string placeholder = "...", int characterLimit = 64, TMP_InputField.ContentType contentType = Standard, string hint = null, byte collectionId = 255, bool isServerOnly = false)` |
| Button | `SSButton(int? id, string label, string buttonText, float? holdTimeSeconds = null, string hint = null)` |
| Header | `SSGroupHeader(int? id, string label, bool reducedPadding = false, string hint = null)` (also `(string label, ...)`, id = hash of label) |
| Text area | `SSTextArea(int? id, string content, FoldoutMode foldoutMode = NotCollapsable, string collapsedText = null, TextAlignmentOptions textAlignment = TopLeft)` |

Header and text area have `ResponseMode = None`: they never raise the event. The set of 8 types is fixed; you cannot add a type.

## Reading values inside the handler

| Entry | Properties | Notes |
|---|---|---|
| Keybind | `SyncIsPressed` | event on press AND release; the bound key is client-only |
| Slider | `SyncFloatValue`, `SyncIntValue`, `SyncDragging` | one event per drag step; NOT clamped by the server |
| Dropdown | `SyncSelectionIndexValidated`, `SyncSelectionText` | `SyncSelectionIndexRaw` is read from one unchecked byte (0..255) and not checked against the option count; the validated variants clamp against the original definition and return 0/empty if there is none |
| Two buttons | `SyncIsA`, `SyncIsB` | |
| Plaintext | `SyncInputText` | truncated server-side to the original `CharacterLimit`; null becomes `""` |
| Button | `SyncLastPress` (a `Stopwatch`, restarted on each received press) | press, or hold for `HoldTimeSeconds`, triggers the send |

## Live updates (server to client)

`SendLabelUpdate`, `SendHintUpdate`, `SendUpdate(label, hint, applyOverride, receiveFilter)` exist on every entry as instance methods; the single-text ones (`SendLabelUpdate`, `SendHintUpdate`, `SendTextUpdate`) take `(string text, bool applyOverride = true, Func<ReferenceHub, bool> receiveFilter = null)`. Per type: `SendValueUpdate` (only effective on `isServerOnly` entries), `SendDropdownUpdate`, `SendSliderUpdate`, `SendTwoButtonUpdate`, `SendPlaintextUpdate`, `SendClearRequest`, `SendButtonUpdate`, and on text areas `SendTextUpdate`.

- `receiveFilter == null` means every authenticated player.
- `applyOverride: true` (default) also rewrites the shared server-side definition, which later packs (joiners, `SendToAll`) serialise for everyone. For per-player text use `applyOverride: false` plus an explicit filter.
- A filtered update walks all hubs, calling the predicate once per hub, then packs once. One call per player makes it O(P^2). A filtered update does not skip `Unverified` hubs (only the null-filter path does), so make the predicate exclude them.

## Server-only entries

`isServerOnly: true` entries: the server dictates the value (`SendValueUpdate` works only on them) and the client does not persist it. The client still reports edits the user makes to such a control, so do not treat "server-only" as read-only.

## Ids, persistence, banner

- `SettingId` is an `int`. `null` id means a hash of the label (Mirror's weak 31-polynomial; "Aa" and "BB" collide).
- Server lookup: first entry in `DefinedSettings` matching id AND exact CLR type.
- Client persistence key embeds scope, `collectionId`, type code and id. Default `collectionId = 255` scopes saved values to the server. 0..19 shares saved values across servers (keyed by account, with a server-key fallback in some case; how the account is derived and when the fallback applies is unverified). Changing id, type or collection orphans the saved value; changing label, range or options does not.
- `Version` differing from the client's stored version (and `Version != 0`) shows a "new settings" warning until the tab is opened.

## Threading

Inbound events run on the main thread (the transport queues network callbacks and drains them in its `LateUpdate`), so handlers may touch game objects, and a slow handler blocks the frame. `DefinedSettings`, `ReceivedUserSettings` and `ReceivedUserStatuses` are unsynchronised: mutate and enumerate on the main thread only, and do not reassign `DefinedSettings` from inside a handler that is iterating it. Off-thread code hands strings to the main thread (see scpsl-main-thread-and-timing).

## Cost model (estimates unless stated)

| Operation | Cost |
|---|---|
| Idle | The game calls a virtual no-op `OnUpdate` on every defined entry each frame (verified no entry overrides it); negligible |
| `SendToAll` | One pack, N segment copies. About 12 B + label + hint per entry per player; 100 entries of ~60 B is ~6 kB per player (estimate) |
| `SendToPlayer(hub, subset)` | Repacks the subset per call; fine per join, bad in a loop on every event |
| Filtered update | O(P) predicate calls + one pack; per-player calls make O(P^2) |
| Slider drag, one message | O(S) prevalidate scan + O(S) per-user scan + subscribers; about 12 B on the wire, about 50 B garbage (estimate); one message per drag step |
| `SendValueUpdate` | Walks all hubs and linearly scans each user's received list |

Nothing here was benchmarked. Use sl-probe or FrameProfiler (see measuring-scpsl-performance) before claiming a number.

## Unverified

1. Whether a received pack REPLACES the client's whole list (assumed; `Refresh` reads one list).
2. Whether the client sends all persisted values right after receiving a pack (acquisition), in what burst, and what happens to saved values for ids a new pack drops. EXILED's handler skips the first value of non-button settings, consistent with, not proof of, that.
3. Whether the client has its SSS handler registered for a pack sent right after the server marks the player ready.
4. How "account" scoping for `collectionId` < 20 is derived.

## EXILED comparison (for reading other plugins)

EXILED's `SettingBase.Register` appends to `DefinedSettings` and sends; `Unregister` removes by reference; it registers one global handler and fans out to per-setting `OnChanged`. A plugin that assigns `DefinedSettings` directly erases EXILED registrations and vice versa. See porting-exiled-to-labapi.

## Re-verifying on a new build

- Check `ServerSpecificSettingsSync` still exposes `DefinedSettings`, `SendOnJoinFilter`, `Version`, `ServerOnSettingValueReceived`, `ServerOnStatusReceived` and the send methods above (decompile the game assembly locally; never publish decompiled code).
- Grep LabAPI for a new SSS wrapper or event; if one appears, this skill's "no wrapper" claims are stale.
- Re-check: the count byte in the pack header (still a `byte`?), dropdown option count byte, the id+type lookup, the exception handling around the receive handler (still disconnects?), and the list of 8 type codes.
- Re-test with a real client: pack replacement (item 1 above), acquisition on pack receipt (item 2), and the first-frames send (item 3). sl-probe can log message order.
