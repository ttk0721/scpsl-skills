# SSS registry pattern

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Original example code, not compiled against the game here: confirm member names with a build (see "Re-verifying"). Why each line exists is in sss-rules.md.

## Shared registry

One class owns `DefinedSettings`, `SendOnJoinFilter` and the single event subscription. Other plugins in the same assembly (or a shared dependency DLL) register through it.

```csharp
using System;
using System.Collections.Generic;
using LabApi.Features.Permissions;   // Player.HasPermissions(string)
using LabApi.Features.Wrappers;
using UnityEngine;
using UserSettings.ServerSpecific;

// UnityEngine also defines a Logger type; without this alias the name is ambiguous.
using Logger = LabApi.Features.Console.Logger;

public static class SssRegistry
{
    private static readonly List<ServerSpecificSettingBase> Entries = new List<ServerSpecificSettingBase>(32);
    private static readonly Dictionary<int, Action<Player, ServerSpecificSettingBase>> Handlers
        = new Dictionary<int, Action<Player, ServerSpecificSettingBase>>();
    private static bool _subscribed;

    // FNV-1a over "<plugin>:<key>". A hash you own: deterministic across runs and builds, whereas
    // string.GetHashCode() carries no cross-runtime stability guarantee.
    public static int Id(string plugin, string key)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in plugin + ":" + key) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }

    // Call from Enable(), main thread. Returns false (and registers nothing) on a duplicate (id, type).
    public static bool Register(ServerSpecificSettingBase entry, Action<Player, ServerSpecificSettingBase> onChange = null)
    {
        foreach (ServerSpecificSettingBase e in Entries)
            if (e.SettingId == entry.SettingId && e.GetType() == entry.GetType())
            {
                Logger.Error("SSS id collision: " + entry.SettingId + " (" + entry.Label + " vs " + e.Label + ")");
                return false;
            }
        Entries.Add(entry);
        if (onChange != null) Handlers[entry.SettingId] = onChange;
        Publish();
        return true;
    }

    private static void Publish()
    {
        ServerSpecificSettingsSync.DefinedSettings = Entries.ToArray();   // the ONLY assignment in the plugin set
        if (!_subscribed)
        {
            ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnValue;
            _subscribed = true;
        }
        // Players already connected (late enable, hot reload) are not served by the join hook.
        foreach (Player p in Player.ReadyList)
            if (!p.IsDummy) ServerSpecificSettingsSync.SendToPlayer(p.ReferenceHub);
    }

    private static void OnValue(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        try
        {
            if (!Handlers.TryGetValue(setting.SettingId, out var handler)) return;   // not ours
            Player player = Player.Get(hub);
            if (player == null || player.IsDummy) return;
            handler(player, setting);
        }
        catch (Exception e)
        {
            // An escaped exception would disconnect the player who moved the control.
            Logger.Error("SSS handler failed: " + e);
        }
    }
}
```

Notes on the sketch:

- Every `Register` call republishes and resends the whole list to connected players. At startup nobody is connected, so this is free; for late or repeated registration, add all entries first and publish once.
- `Handlers` is keyed by id only. `Register` already rejects a duplicate (id, type), but two entries of different types with the same id would share a handler; reject any id duplicate if you want that stricter.
- Do not log per event from a slider handler (see scpsl-engine-cost-model); the `catch` above is a rare path. Rate-limit if a bad client could trigger it repeatedly.
- Plugin teardown: `Disable()` is never called (see scpsl-game-lifecycle), so unregistering is not a case to design for; a hot reload re-runs `Enable()` and `Register` would hit the duplicate check. Make registration idempotent per plugin (clear that plugin's entries first) if you support reload.

## Defining entries

```csharp
public static class MyIds
{
    public static readonly int Header = SssRegistry.Id("myplug", "header");
    public static readonly int Dash   = SssRegistry.Id("myplug", "dash");
    public static readonly int Volume = SssRegistry.Id("myplug", "volume");
    public static readonly int Mode   = SssRegistry.Id("myplug", "mode");
}

SssRegistry.Register(new SSGroupHeader(MyIds.Header, "My Plugin"));
SssRegistry.Register(new SSKeybindSetting(MyIds.Dash, "Dash", KeyCode.LeftAlt, hint: "Hold to dash"), OnDash);
SssRegistry.Register(new SSSliderSetting(MyIds.Volume, "Volume", 0f, 100f, 50f, integer: true), OnVolume);
SssRegistry.Register(new SSDropdownSetting(MyIds.Mode, "Mode", new[] { "Off", "Low", "High" }), OnMode);
```

## Handlers: authorise, clamp, debounce

```csharp
private static void OnDash(Player p, ServerSpecificSettingBase s)
{
    if (!(s is SSKeybindSetting k)) return;
    if (!p.HasPermissions("myplug.dash")) return;      // never rely on who was sent the entry
    SetDashing(p, k.SyncIsPressed);                    // press AND release both arrive; use the level
}

private static void OnVolume(Player p, ServerSpecificSettingBase s)
{
    if (!(s is SSSliderSetting sl)) return;
    float v = sl.SyncFloatValue;
    if (float.IsNaN(v) || float.IsInfinity(v)) return;     // Mathf.Clamp lets NaN through
    v = Mathf.Clamp(v, 0f, 100f);                           // server does not clamp
    PendingVolume[p.PlayerId] = v;                          // apply on your own clock, not per drag step
}

private static void OnMode(Player p, ServerSpecificSettingBase s)
{
    if (!(s is SSDropdownSetting d)) return;
    int mode = d.SyncSelectionIndexValidated;               // not SyncSelectionIndexRaw
    ApplyMode(p, mode);
}
```

`HasPermissions` takes LabAPI string permissions; for game `PlayerPermissions` flags see scpsl-commands-and-permissions. Apply `PendingVolume` from one periodic job (see scpsl-main-thread-and-timing, SlKit `SlClock.Every`); drag steps only write a dictionary entry. Per-player token buckets (SlKit.Core `TokenBucket`) cap abusive clients. Clear per-player caches on `Left` and on round boundaries (PlayerIds are recycled; see scpsl-game-lifecycle).

## Per-player views (role pages, staff-only entries)

Every entry stays in the global `DefinedSettings` union (responses for ids not in it are silently dropped). Visibility is controlled by what you send.

```csharp
// Owner of SendOnJoinFilter: one place only, composed here.
ServerSpecificSettingsSync.SendOnJoinFilter = _ => false;   // joiners get nothing automatically

static ServerSpecificSettingBase[] ViewFor(Player p)
{
    var list = new List<ServerSpecificSettingBase>(16);
    list.AddRange(CommonEntries);
    if (p.HasPermissions("myplug.staff")) list.AddRange(StaffEntries);
    return list.ToArray();                                   // always the MERGED list for this player
}

// PlayerEvents.Joined handler (fires when the nickname is set); gate on readiness.
static void OnJoined(PlayerJoinedEventArgs ev)
{
    if (ev.Player.IsHost || ev.Player.IsDummy || !ev.Player.IsReady) return;
    ServerSpecificSettingsSync.SendToPlayer(ev.Player.ReferenceHub, ViewFor(ev.Player));
}
```

Caveats: the union must stay under 255 entries, because the join hook, `SendToAll` and `SendToPlayer(hub)` serialise the whole union (with `SendOnJoinFilter` returning false and no whole-list senders, only subsets are packed). Staff entries visible only to staff is cosmetic: still authorise in the handler. Whether the client accepts a pack in the first frames after `Joined` is unverified; if entries do not appear for fast joiners, delay the send by a short wall-clock interval via SlClock rather than a per-join coroutine, and test with a real client.

## Per-player live text

```csharp
// textArea is an SSTextArea in DefinedSettings
textArea.SendTextUpdate("Kills: " + n, applyOverride: false, receiveFilter: h => h == target.ReferenceHub);
```

`SendTextUpdate` is an instance method on the text area, with the filter typed `Func<ReferenceHub, bool>`; confirm it on your build. The contract is what matters: an explicit filter AND `applyOverride: false`. The filter also receives `Unverified` hubs, so a bare `h => true` style filter reaches unauthenticated connections. Throttle with `IsTabOpenForUser(hub)` / `ServerOnStatusReceived` and re-push the current text when the tab opens, because skipped updates are not replayed.

## Re-verifying on a new build

- Compile the snippets against the new `Assembly-CSharp.dll`: `SendToPlayer`, `SendTextUpdate` (instance method, filter type), constructor parameter names and the `Sync*` properties are the likely drift points.
- `Player.ReferenceHub`, `Player.ReadyList`, `IsDummy`, `HasPermissions` are LabAPI members; check them against the LabAPI release notes.
- Re-test with two plugins registering through the registry on a live server and a real client: both plugins' controls visible, each handler fires only for its own ids.
