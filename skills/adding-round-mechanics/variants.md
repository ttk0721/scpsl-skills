# Round mechanics: variants and reset table

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of adding-round-mechanics. Facts and evidence live in scpsl-game-lifecycle (round-flow.md); this file only adds recipe-level choices. Items marked **(derived)** come from reading code, not from a run.

## What resets at a new round, and who resets it

| State | Reset by | You must |
|---|---|---|
| `Player`, hubs, `Server.Host`, `CustomDataStore`, doors, rooms, pickups | Scene reload | Re-resolve; never cache across a restart |
| Respawn tokens, previous team, late-join set, wave config refresh | Vanilla, on `OnClientReady` | Nothing. A token you granted is gone next round |
| Wave timers | Vanilla, at map generation (values only; the objects persist) | Nothing; re-apply `TimeLeft`, pause or `IsForcefullyPaused` edits each round |
| `Round.IsLobbyLocked`, `Round.IsLocked`, `Round.KeepRoundOnOne` | Nobody (statics; `KeepRoundOnOne` is re-read from config each round **(derived)**) | Release what you took, re-apply what you want |
| Your fields, static event subscriptions, `SlClock` `Server`-scope jobs | Nobody | Reset at `WaitingForPlayers` and `RoundRestarted` |
| `SlClock` `Round`-scope jobs | `SlClock.Install` wires it, at `RoundRestarted` | Dispose at `RoundEnded` yourself if they must not run in the summary window |
| `Server.PauseIdleMode` | Vanilla un-pauses it every round | Re-apply at `WaitingForPlayers` |

## Variants

**End on an objective.** Flag, `Round.End(true)`, `CanEnd = true` in the check, `LeadingTeam` in `RoundEnding`. `RoundVariants.CompleteObjective`, `OnConditionsCheck`, `OnEnding`. The leading team is a label for the summary; it does not change what the game counted. `RoundSummary.LeadingTeam` has `FacilityForces`, `ChaosInsurgency`, `Anomalies`, `Flamingos`, `Draw`.

**Keep a round open.** `CanEnd = false` blocks one verdict and vanilla resumes the moment you stop writing it. `Round.IsLocked = true` stops the whole check, so `RoundEndingConditionsCheck` is never raised and your own forced end needs `Round.End(true)` to be reachable at all **(derived)**. `RoundVariants.HoldRoundOpen` tracks ownership. Load tests and event rounds want the lock; "no end before minute 2" wants `CanEnd = false`.

**Round time limit.** `SlClock.After(limit, OnLimitReached, name, ClockScope.Round)` at `RoundStarted`. Warnings are more `After` jobs at `limit - warn`, kept in a list so `RoundEnded` can dispose them. Do not poll `Round.Duration` every tick: it is a wall-clock stopwatch reset at scene load, and it stays 0 for the whole round if any plugin cancelled `RoundStarting` **(derived)**.

**Wave control.** Primary waves start near 330 s and mini waves near 150 s, so "no waves in the first minute" does nothing on defaults. Rules that matter are late-round ones (final minutes, after an objective), per-faction ones (`RespawnWave.Faction`, `wave is MiniRespawnWave`), or token-based ones.
- Cancel at `WaveTeamSelecting`, then `wave.TimeLeft = n`. `TimeLeft` sets `TimePassed`, which un-readies the wave **(derived)**. `IsForcefullyPaused` is not enough: it stops the timer counting but the manager still sees a ready wave and re-raises the event every frame.
- `RespawnWaves.Get(ev.Wave)` turns the raw `ev.Wave` into the wrapper. It returns null for unknown wave types: pass through.
- `RespawnTokens` is get/set on limited waves only; `RoundVariants.GrantMtfToken`.
- `MaxWaveSize` counts hubs including host, dummies and unauthenticated ones, so a wave on a test server with dummies is larger than players suggest.

**Lobby hold.** The countdown only runs with more than one counted player and an unlocked lobby, and while locked the loop resets it, so releasing gives a full `lobby_waiting_time`. Count real players yourself. Without a max-hold valve one missing player holds the lobby forever. While locked even a full server does not start: the reserved-slots shortcut sits inside the unlocked branch of the loop.

**Announcements.** Broadcasts are for rare lines. `Announcer.Message(text, subtitles)` queues CASSIE; unknown words are skipped silently, so a typo is a quieter announcement, not an error, and there is no hook to intercept custom lines (`CassieAnnouncing` is never raised). The queue is cleared at restart.

## Probes worth running on a test server

Ask first whether a test server is wanted and whether to use an existing port or our own instance; check whether the server is public; never touch the owner's ports unasked (verifying-scpsl-work).

1. Lock the lobby, join one player, confirm no countdown, release, confirm a fresh countdown.
2. Set a 2-minute limit with two teams alive, confirm the round ends as `Draw` within one 2.5 s tick.
3. Cancel a wave and log how often `WaveTeamSelecting` fires with and without the `TimeLeft` push.
4. Force a restart mid-round and confirm no warning or limit fires in the next round.

## Re-verifying on a new build

- Re-read the lobby coroutine for the lock branch and the force-start routine for what a cancel leaves behind.
- Re-read the round-summary coroutine: gate condition, where the check event is raised, whether the forced flag is still overwritten.
- Re-read the wave manager update and the wave timer: what `IsReadyToSpawn` and `IsPaused` consult, and whether the selecting event is still raised per frame for a ready wave.
- Re-run the four probes above; any change in event frequency or ordering voids the matching row in the Decisions table.
