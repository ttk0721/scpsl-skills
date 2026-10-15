# Skill details moved out of SKILL.md

Full wording of material that SKILL.md only carries in short form. Read on demand.

## Full Quick Reference table
| Moment | Valid | Not yet / gone |
|---|---|---|
| `Enable` (inside server boot) | `ConfigFile`, `Server.Port`, static subscriptions | players, `Server.Host`, rooms, `Round.*`, permissions |
| `WaitingForPlayers` (every round) | new map registries, host wrapper | last round's references; items and lockers (seconds later) |
| `MapGenerated` | room objects | rooms by coordinates, doors by room |
| Lobby | `Joined`, `Left`, `ReadyList` | roles are `None` |
| `RoundStarted` | `Round.IsRoundStarted` | roles still `None` (assigned later, same frame) |
| `ChangedRole` | role, position, inventory | late joiner: fires before `Joined` |
| `RoundRestarted` | old world still alive | can fire twice; not raised by `softrestart`, which exits the process instead, so an open round is closed only by `ServerEvents.Shutdown` |
| Scene swap | `Left` per non-host hub | every cached `Player`, hub, `Server.Host` |
| `ServerEvents.Shutdown` | synchronous flush; close open sessions with a terminal record | `Disable()` is never called; no later `left` for open visits; no fire on SIGKILL or crash |

## Rule 2: identity
`UserId` is an identity only for authenticated online-mode players. Offline mode gives a non-empty per-connection `ID_Offline_<netId>_<unix>`, and dummies share `ID_Dummy`, so a null/empty check does not filter them. Key by `UserId`, and not `PlayerId` or `netId`.

## Rule 9: shutdown flush
Subscribe last in `Enable` and keep it idempotent. Process exit ends every visit and statics die, so close open sessions in the `ServerEvents.Shutdown` flush and mark them terminal (reason `shutdown`).

## Common Mistakes, full wording
| Wrong | Right |
|---|---|
| One session per `Joined`, flushed on `Left`, reported as a visit. Each restart sends `Left` for all players and resets playtime | Decide per-hub or per-visit. For visits, park on `Left`, resume on `Joined` by `UserId`, flush after a grace period |
| Keying visits by `UserId` guarded only by `IsNullOrEmpty` | Offline ids are non-empty and new per hub, so park/resume never matches. Reject `ID_*` ids, state the online-mode assumption, fall back to per-hub when offline |
| `OnShutdown` spools sessions still `online`/`parked`, next boot posts them | Nobody will ever send `left` for those visits (process exit, statics gone). Close them in the flush with a terminal record (`left`, reason `shutdown`) or the receiver keeps them online forever |
| Hard-coding `Role = "Unknown"` in `Left` | The wrapper is complete in `Left`. Read `Role`, `UserId`, `Nickname` there, or cache on `ChangedRole`. With `disconnect_drop` an alive leaver is already Spectator |
| Clearing round state only on `RoundEnded` and `WaitingForPlayers` | Also `RoundRestarted` (can fire twice) and `RoundRestart.OnRestartTriggered` (the only one `softrestart` raises) |
| Per-`PlayerId` cache reused on a new join | Ids recycle (all freed at restart). Reset on `Joined`, `Left`, round start |
| Giving items in `RoundStarted` after a guessed 1 s delay | Roles are `None` there. Use `ChangedRole` (inventory filled). A second leaves a window where a death or leave can come first |
| Cancelling `RoundStarting` to hold the lobby | It neither holds nor delays and leaves `RoundLength` at 0. Use `IsLobbyLocked` |
| `Round.End(true)` alone, or a `CanEnd` handler alone | Do both |
| Cancelling `WaveRespawning` | Token and timers are still spent. Cancel `WaveTeamSelecting` (re-fires each frame, keep it cheap) |
| Trusting `Server.Tps` or MEC waits on an empty server | Idle mode (timeScale 0.01) can start mid-round. Treat `IdleModeActive` as no data |

## Re-verifying on a new build

- Walk the Quick Reference table row by row against the raise order: `WaitingForPlayers`, `MapGenerated`, `RoundStarted`, `ChangedRole`, `RoundRestarted`, scene swap, `ServerEvents.Shutdown`. A moved raise site changes the "Valid" and "Not yet / gone" columns.
- Re-check that `RoundRestarted` can still fire twice, that `softrestart` raises only `RoundRestart.OnRestartTriggered`, and that a restart still sends `Left` for every non-host hub.
- Re-check how `UserId` is built for offline mode (`ID_Offline_<netId>_<unix>`) and for dummies (`ID_Dummy`), since rule 2 and the identity mistake rows depend on both.
- Re-check that `Disable()` still has no caller, that `ServerEvents.Shutdown` still fires on a normal exit, and that nothing fires on SIGKILL or a crash (rule 9).
- Re-check that `RoundStarting` cancellation still neither holds nor delays the round, that `WaveRespawning` cancellation still spends the token and timers, and that `WaveTeamSelecting` still re-fires each frame.
- Re-check the idle-mode trigger (`IdleModeActive`, time scale 0.01) on a live server: the mistake row about trusting `Server.Tps` on an empty server depends on it.
