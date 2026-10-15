# Coexisting with other role plugins (adding-custom-roles)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Compiled example: `examples/adding-custom-roles/`. Behaviour of third-party plugins is described as a class ("a plugin that rewrites `NewRole`"), not from their source.

## The rule of thumb

Everyone else sees your custom role as its BASE role. `Player.Role`, `Player.Team`, faction, `IsHuman`, friendly-fire rules, wave priority and round-end team counts all read the live game role. That is the point of the layered design, and it fixes the limits:

| You want | Possible by layering | Instead |
|---|---|---|
| Different loadout, health, effects, abilities, label | yes | the recipe |
| Different team or faction (a "spy" the enemy treats as a friend) | NO: a different base role changes the loadout and team rules, and faking the team breaks round-end and friendly fire | Pick the base role whose team you want. A cross-team role is a design of its own: cancel or scale `Hurting` between the two sides yourself and say so. |
| Another plugin treats your holders specially | yes, via `TryGetRoleId` | publish a small read-only API (below) |
| Your holders skip another plugin's rules | only if that plugin offers a hook | ask the owner; do not patch around it |

## Who gets the last word

Handlers run in subscription order, which follows plugin `Priority` at `Enable()` time (lower byte runs earlier), and the LAST subscriber wins a mutable argument such as `NewRole`, `ChangeReason` or `IsAllowed`. There is no per-handler priority. Consequences:

- If another plugin redirects `NewRole` after you decided, the role that reaches `ChangedRole` is not your base role. Compare it with the base role you decided for and drop the decision (the example does).
- If another plugin cancels the change, your decision is stale one frame later. Store the frame with the decision (spawn-timeline.md).
- Do not use `Priority` to "win". A second role plugin doing the same fights you for ever, and the loser's players get a half-applied role. Agree one owner per base role, or make the definitions mutually exclusive (distinct base roles, or a shared registry).
- Never call `SetRole` on the same player from a role or spawn handler to take over: nested role changes are unsafe (scpsl-roles-damage-effects).

## Shared state you must not trample

| State | Rule |
|---|---|
| Custom info and info-area flags | Record the previous values, write yours, restore only if the current values are still yours. |
| Health, items, effects | Written in `ChangedRole` after the game's reset. A plugin that also writes them there is order-dependent: yours should only touch what the definition names. |
| Hints | One owner per player. If a HUD exists, send the "you are X" line through it (adding-player-hud) and not with a raw `SendHint`: a hint replaces the previous one on the client. |
| Badges, permissions | Not part of a role. Do not couple them to it. |
| Movement speed | A `MovementBoost` effect is visible to every plugin that reads effects. An anti-cheat that keys its speed limit on the role name will flag your holders. Document the effect, or expose it through `TryGetRoleId`. |

## Telling holders apart without caching

Expose one static, read-only query that verifies the live role instance and returns plain data:

```csharp
public static bool TryGetRoleId(Player player, out string roleId)   // in CustomRolesPlugin
```

Callers ask each time and never store the answer: it is checked against `Player.LifeId`, which changes on every role instance, so it can never be stale. Do not make other plugins parse custom info text. Do not hand out the registry or a `Player` list: both invite the caching this recipe exists to prevent. A static C# event for "assigned" and "released" is fine if a consumer needs push, but pass plain data (user id, role id) and raise it from the main thread.

## Third-party custom-role plugins on the same server

- Two systems assigning from `ChangingRole` for the same base role is a conflict by construction. Tell the owner before shipping, and ship a kill switch that stops yours (`IsEnabled`).
- A holder of the other plugin's role never reaches your registry unless you assigned it. Your `ChangedRole` still runs for them: it releases nothing it does not own.
- A plugin that rewrites `NewRole` to its own base role for round start will shift which players reach your roll. That is fine: roll on the role that arrives, not on the one you expect.
- Counts and caps (`MaxAlive`) are yours only. They do not see the other plugin's holders.

## Mass events

Round start and waves run `ChangingRole` and `ChangedRole` once per player in one frame. Your handlers stay O(1): an int compare when nobody holds a role, a dictionary lookup for the few who do. Anything heavier (a hint per holder, a `Player.List` scan, a log line) moves to the 1 Hz tick or does not happen. See scpsl-roles-damage-effects for the engine side of this cost.

## Re-verifying on a new build

- Re-read the event manager's invocation path: still subscription order, still no per-handler priority.
- Re-read plugin load ordering and the `Priority` values.
- Confirm `Player.LifeId`, `Player.Team` and `Player.Role` still read the live role.
- On a server with a second role plugin: spawn a round, check which plugin's loadout each player got, and that no label or effect leaked after death.
