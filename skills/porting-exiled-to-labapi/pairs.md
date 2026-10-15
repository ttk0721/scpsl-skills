# Porting pairs: EXILED and LabAPI side by side

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). EXILED side checked against 9.15.x.

Eight common cases, each as the EXILED code you are porting and the LabAPI code that replaces it. The
LabAPI side is a real project, `examples/porting-exiled-pairs/PortingExiledPairs.csproj`, built and linted
with the other examples. The EXILED side is shown for reading only: the repository does not reference EXILED.
Timing claims come from `mapping.md`; read it for the reasons, here are the shapes.

| # | Case | LabAPI file in the example project |
|---|---|---|
| 1 | Subscribe and unsubscribe | `PortingPairsPlugin.cs` |
| 2 | `Verified` to `Joined`, `Destroying` to `Left` | `JoinTracker.cs` |
| 3 | Broadcasts and hints | `Messaging.cs` |
| 4 | Coroutine loops and delays to SlClock | `PeriodicJobs.cs` |
| 5 | `Player.List` iteration | `PlayerScan.cs` |
| 6 | Cancellable and modifiable damage | `DamageRules.cs` |
| 7 | Config, translations, permissions | `PairsConfig.cs`, `HealCommand.cs` |
| 8 | Spawn loadout | `LoadoutRule.cs` |

Every file opens with a doc comment holding the EXILED original and the list of differences.

## 1. Event subscription and lifecycle

```csharp
// EXILED
public override void OnEnabled()  { Handlers.Player.Hurting += OnHurting; base.OnEnabled(); }
public override void OnDisabled() { Handlers.Player.Hurting -= OnHurting; base.OnDisabled(); }
```

```csharp
// LabAPI
public override void Enable()
{
    if (_enabled) return;                 // idempotent
    // fallible work first, subscriptions last
    _damage.Start();                      // PlayerEvents.Hurting += ... inside
    ServerEvents.Shutdown += OnShutdown;  // the only reliable last call
    _enabled = true;
}
public override void Disable() => Teardown();   // never called by LabAPI or the game
```

- Metadata (`Name`, `Version`, `RequiredApiVersion`) is constant; `Name` picks the config folder.
- Handlers have no priority and cannot be `async`. The loader's `Priority` byte orders `Enable()`: lower enables earlier.
- A throw after the first `+=` leaves live handlers with no rollback, hence `try/catch` around startup that calls `Teardown()`.

## 2. Verified to Joined, Destroying to Left

```csharp
// EXILED
Handlers.Player.Verified   += ev => Timing.CallDelayed(1f, () => ev.Player.Broadcast(5, "Welcome"));
Handlers.Player.Destroying += ev => Forget(ev.Player.UserId);
```

```csharp
// LabAPI
PlayerEvents.Joined += OnJoined;   // was Verified
PlayerEvents.Left   += OnLeft;     // was Destroying

private void OnJoined(PlayerJoinedEventArgs ev)
{
    string id = ev.Player.UserId;
    if (string.IsNullOrEmpty(id)) return;
    _joined.Add(id);
    _messaging.Welcome(ev.Player);   // IsReady is already true: no delay
}

private void OnLeft(PlayerLeftEventArgs ev)
{
    string id = ev.Player.UserId;
    if (id == null || !_joined.Remove(id)) return;   // unauthenticated hub, NPC, dummy, badge mismatch
}
```

- `Joined` is later than `Verified`, so the delay that waited for badges is dead code.
- `Joined` is skipped on badge UserID or nickname mismatch while the player stays connected: the set means "joined cleanly".
- `Left` fires for every non-host hub. The set filled in `Joined` is the guard; `IsReady` is not.
- `JoinTracker.Count` is O(1). `Player.Count` re-runs a LINQ filter per call and counts dummies.

## 3. Broadcasts and hints

```csharp
// EXILED
player.Broadcast(5, "text");                                  // duration first
Map.Broadcast(10, "text");
foreach (Player p in Player.List) p.ShowHint("text", 5f);     // Map.ShowHint
```

```csharp
// LabAPI
player.SendBroadcast("text", 5);                  // message first, ushort duration
Server.SendBroadcast("text", 10);
foreach (Player p in Player.ReadyList)            // excludes the host: SendHint throws there
    p.SendHint("text", 5f);
```

- The argument order is swapped. A call through variables can slip past review: grep every `Broadcast(`.
- Duration is a `ushort`: clamp config values (`Messaging.ToDuration`) instead of letting them wrap.
- Fill templates with `Replace`, not `string.Format`: a stray brace in an edited translation file throws inside a handler.
- Hints from different features compete for one client slot (newest-wins on the client is UNVERIFIED). If a HUD already owns it, route through the HUD (scpsl-hints-and-hud).

## 4. Coroutine loops to SlClock

```csharp
// EXILED (MEC)
_loop = Timing.RunCoroutine(AnnounceLoop());     // for (;;) { yield return Timing.WaitForSeconds(300f); ... }
Handlers.Server.RoundStarted += () => Timing.CallDelayed(10f, ShowTip);
OnDisabled: Timing.KillCoroutines(_loop);
```

```csharp
// LabAPI + SlKit
SlClock.Install();            // once, in Enable, main thread only; also ends ClockScope.Round jobs at RoundRestarted
_announce = SlClock.Every(TimeSpan.FromSeconds(300), Announce, "pairs.announce");
_online   = SlClock.EveryStaggered<Player>(TimeSpan.FromSeconds(5), RefillVisit, ShowOnline, "pairs.online");
_tip      = SlClock.After(TimeSpan.FromSeconds(10), ShowTip, "pairs.roundtip");   // Round scope by default
// Stop: dispose each returned IDisposable
```

- `Timing.WaitForSeconds` on the Update segment is scaled by `Time.timeScale`; in idle mode (0.01) a 300 s wait lasts hours. SlClock uses `Stopwatch` deadlines.
- A throwing coroutine dies silently. SlClock catches per job and disables a job after repeated failures.
- `Every` skips missed periods instead of replaying them after a freeze.
- Per-player work becomes `EveryStaggered`: each item once per period, spread over frames. `RefillVisit` runs once per period, so the list and any shared text are built once, not per player.
- Do not start a coroutine or `CallDelayed` per event or per player. Register one job that walks your own data (scpsl-main-thread-and-timing).
- `Disable()` is never called, so `Stop()` must also run from the `Shutdown` handler.

## 5. Player.List iteration

```csharp
// EXILED: List is a COPY of the verified players
foreach (Player p in Player.List)
    if (p.Role.Team == Team.FoundationForces) p.Heal(20f);
```

```csharp
// LabAPI: no copy; snapshot, then act
internal static int Snapshot(List<Player> into)
{
    into.Clear();
    foreach (Player p in Player.GetAll(PlayerSearchFlags.AuthenticatedPlayers)) into.Add(p);
    return into.Count;
}
// caller: PlayerScan.Snapshot(_buffer); for (int i = 0; i < _buffer.Count; i++) { if (_buffer[i].IsDestroyed) continue; ... }
```

- `Player.List` is the live view and includes the host, NPCs, dummies and unauthenticated hubs.
- `ReadyList` = authenticated players plus dummies. `GetAll(AuthenticatedPlayers)` = real players only.
- Both are lazy LINQ with no snapshot. Snapshot before heal, kick, `SetRole` or any `yield`, and re-check `IsDestroyed` per item.
- EXILED `Player.Get(Side)` has no twin. Filter by `Faction` (friendly fire uses faction) or `Team`; `Player.Faction` and `Player.IsAlive` exist.
- A reusable buffer plus a once-per-tick count keeps the steady state allocation-free (labapi-wrappers).

## 6. Cancellable and modifiable damage

```csharp
// EXILED
private void OnHurting(HurtingEventArgs ev)
{
    if (ev.DamageHandler.Type == DamageType.Falldown) { ev.IsAllowed = false; return; }
    if (ev.Attacker == null || ev.Attacker == ev.Player) return;
    if (ev.Attacker.Role.Side == ev.Player.Role.Side) ev.Amount *= 0.5f;
}
```

```csharp
// LabAPI
private void OnHurting(PlayerHurtingEventArgs ev)
{
    StandardDamageHandler handler = ev.DamageHandler as StandardDamageHandler;
    if (handler == null) return;                                   // cheap type test first
    if (cfg.BlockFallDamage && IsFall(handler)) { ev.IsAllowed = false; return; }
    if (handler.Damage <= 0f) return;                              // -1 is an instakill
    Player attacker = ev.Attacker;                                 // null for environment damage
    if (attacker == null || ReferenceEquals(attacker, ev.Player)) return;
    if (attacker.Faction == ev.Player.Faction) handler.Damage *= cfg.FriendlyDamageMultiplier;
}

private static bool IsFall(StandardDamageHandler h)
{
    UniversalDamageHandler u = h as UniversalDamageHandler;
    return u != null && u.TranslationId == DeathTranslations.Falldown.Id;
}
```

- No `DamageType` enum and no `ev.Amount`: test the handler class, and for universal handlers `TranslationId`.
- `ev.DamageHandler = x` compiles and does nothing. Mutate the same handler (scpsl-lint SL003).
- `Hurting` runs after the god-mode and spawn-protection returns: a protected victim never reaches it.
- `Damage` is raw. Hitbox, friendly-fire multiplier, effects and armour apply afterwards; read the final value in `Hurt`.
- Cancelling `Hurting` blocks the hit. Cancelling `Dying` does not: damage was applied, so restore `Health` as `DamageRules.OnDying` does, or the victim stays alive at 0 HP.
- `Hurting` is hot on some sources (MicroHID per frame, shotgun pellets): no logging, no list walking before the early-outs (scpsl-roles-damage-effects).

## 7. Config, translations and permissions

```csharp
// EXILED
public sealed class Config : IConfig { public bool IsEnabled { get; set; } = true; public bool Debug { get; set; } /* ... */ }
public sealed class Plugin : Plugin<Config, Translation> { /* Translation reloaded by EXILED */ }
if (!sender.CheckPermission("pairs.heal")) { response = "no permission"; return false; }
```

```csharp
// LabAPI
public sealed class PairsConfig { [Description("...")] public bool IsEnabled { get; set; } = true; /* no IConfig, no Debug */ }

public override void LoadConfigs()
{
    base.LoadConfigs();                                   // REPLACES Config on every reload
    Cfg.Normalize();
    PairsTranslation text;
    Text = this.TryLoadConfig("translation.yml", out text) && text != null ? text : new PairsTranslation();
}
internal PairsConfig Cfg => Config ?? (Config = new PairsConfig());   // empty config.yml gives null

if (!sender.HasPermission("pairs.heal")) { response = text.NoPermission; return false; }   // LabApi.Features.Permissions
```

- There is no translation type. A second config class loaded through `TryLoadConfig` replaces it, reloaded in `LoadConfigs`.
- Read config at use time (`_cfg()`), never cache the object: a reload replaces it.
- `HasPermission(string)` reads `permissions.yml`, with different wildcard, case and group rules than EXILED (`replacements.md`). It returns false inside `Enable()`.
- A sender that is not a player (the server console) passes the check.
- The game's `PlayerPermissions` flags are another overload: `sender.CheckPermission(PlayerPermissions.X, out response)`. A string where the flag is expected does not compile (scpsl-commands-and-permissions).

## 8. Spawn loadout

```csharp
// EXILED
private void OnChangingRole(ChangingRoleEventArgs ev)
{
    if (ev.NewRole == RoleTypeId.ClassD) ev.Items.Add(ItemType.Painkillers);
}
```

```csharp
// LabAPI
PlayerEvents.ReceivingLoadout += OnReceivingLoadout;

private void OnReceivingLoadout(PlayerReceivingLoadoutEventArgs ev)
{
    if (ev.Player.Role != RoleTypeId.ClassD) return;   // the role is already set here
    ev.AddItem(bonus);
}
```

- LabAPI `ChangingRole` has no `Items` or `Ammo`. `ReceivingLoadout` has `AddItem`, `ClearItems`, `AddAmmo`, `SetAmmo`.
- `Spawned` precedes the loadout; items added there are wiped when the change carries `AssignInventory`.
- A late joiner is not `IsReady` and has no `Nickname` yet: do not read either here.
- `ReceivingLoadout.InventoryReset` is ignored by the game.

## Using the pairs

1. Find the case in the table, open its LabAPI file, copy the shape, then re-read the matching `mapping.md` row for timing.
2. Keep each behaviour on one layer. Do not leave the EXILED handler subscribed beside the LabAPI one.
3. Build and lint your port: `scpsl-lint` flags the ignored setters (SL003), hot subscriptions (SL001), never-raised events (SL002) and per-event timers (SL005) that ports usually reintroduce.
4. Test with EXILED loaded and unloaded, and cover join, late join, leave and round restart. A live server is the owner's: ask first whether they want local tests at all, and never use their ports (running-a-test-server).

## Re-verifying on a new build

- Rebuild `examples/porting-exiled-pairs`: a renamed or removed LabAPI member fails there first.
- Re-check what compiles but changes silently: the `Broadcast`/`SendBroadcast` argument order, `Player.Get(string)`, `Mute` defaults, `Hurting` position versus spawn protection, `Dying` cancel leaving 0 HP.
- Confirm fall damage is still a `UniversalDamageHandler` with `DeathTranslations.Falldown` (the one string test in pair 6).
- On a test server (see running-a-test-server, ask first) confirm `Joined` still arrives with `IsReady` true (verifying-scpsl-work).
- Re-read EXILED's `HurtingEventArgs`, `Plugin<TConfig, TTranslation>` and `CheckPermission` after an EXILED release; the EXILED halves here are not compiled.
