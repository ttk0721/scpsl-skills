# TickrateFix

A tiny LabAPI plugin for **LabAPI 1.1.7 / game 14.2.7** that makes the `server_tickrate` config
value actually take effect. It has no dependencies beyond the game (it does not need SlKit).

**Version 0.1.0, off by default.** The problem and the fix were measured on one 14.2.7 test
server (see [Measured](#measured-on-1427)). Installed as shipped, it changes nothing: it only logs
the frame rate target at every `WaitingForPlayers`, so you can see whether your server has the
problem before you turn the fix on.

## The problem

`server_tickrate` sets how many frames per second the server runs. Reading the game's, Mirror's and
LabAPI's code shows this chain: the game applies the value when the match is created, but right
after that Mirror's `StartHost` calls `ConfigureHeadlessFrameRate`, which sets
`Application.targetFrameRate` to Mirror's `sendRate` (60 by default). So a server configured for
20 TPS would keep running its frame loop at 60, and burn the CPU of 60, until the first time it
leaves idle mode or its config is reloaded.

LabAPI's `Server.MaxTps` getter returns the configured value, and its setter writes
`Application.targetFrameRate` again on a dedicated server. Assigning the property to itself at
`WaitingForPlayers` (after the host has started) therefore re-applies the config. That is all this
plugin does once enabled.

## Measured on 14.2.7

Measured with no players and no dummies (LabAPI 1.1.7, Linux, one run per row):
a dummy joining ends idle mode, which re-applies the tickrate by itself and would hide the problem.
Real frame rate from `tools/sl-probe` over the first 15 s after `WaitingForPlayers`.

| `server_tickrate` | Plugin | Measurement line | Real frame rate | Main-thread load |
|---|---|---|---|---|
| 60 (default) | off | `before=60 after=60 reason=disabled-observe-only` | 59.9 fps | 14.6% |
| 20 | off | `before=60 after=60 reason=disabled-observe-only` | **59.9 fps** | 14.4% |
| 20 | on | `before=60 after=20 reason=configured assigned=20` | **20.0 fps** | 7.5% |

With 20 configured, the game printed "Server tickrate set to: 20" and still ran at 60 until the
plugin re-applied the value. Not measured: a server with players, other machines, and what happens
later in the session (leaving and re-entering idle mode, config reloads).

The log line below is how to check it on your build.

## The measurement line

At every `WaitingForPlayers` the plugin logs one line, enabled or not:

```
[TickrateFix] targetFrameRate before=60 after=20 reason=configured server_tickrate=20 cap=60
```

LabAPI adds `[INFO]` in front. Find the lines with `grep "\[TickrateFix\] targetFrameRate before="`.
`before` and `after` are `Application.targetFrameRate` before and after the plugin acted; `-1`
means Unity's "no target" or that it could not be read. The numbers are formatted with invariant
culture, so scripts can parse them on any server locale.

| `reason` | Meaning |
|---|---|
| `disabled-observe-only` | `enabled: false`. Nothing was assigned; `before` is what the server runs at. If it differs from `server_tickrate` (and that is not 60), Mirror's override happened on your build. |
| `configured` | `server_tickrate` was between 1 and `cap` and was assigned as configured. |
| `unlimited-replaced-by-cap` | `server_tickrate` was `-1` or `0` (unlimited), so `cap` was assigned instead. |
| `above-cap-kept` | `server_tickrate` was above `cap`. It was assigned as configured: the plugin never lowers your setting. One warning per process says so. |
| `idle-mode-skipped` | Idle mode was active, so nothing was assigned (see below). |
| `failed` | An exception was caught; the line ends with `error=...` and the server keeps its frame rate. |

When the plugin assigns a value, the line also ends with `assigned=N`, and the game prints its own
"Server tickrate set to ..." line.

## Why it is a separate plugin

The frame rate is one value for the whole server, and two plugins could fight over the cap, so the fix is a standalone plugin and not a library call. The decision belongs to the person who installs it and turns it on: the server owner.

Install only one plugin that sets `Server.MaxTps`. If another plugin also assigns it, the last
assignment wins.

## Install

```
dotnet build tools/tickrate-fix/src/TickrateFix.csproj -c Release
dotnet build tools/tickrate-fix/src/TickrateFix.csproj -c Release -p:SL_REFERENCES="/path/to/SCPSL_Data/Managed"
```

Copy `TickrateFix.dll` from `src/bin/Release/net48/` to

```
~/.config/SCP Secret Laboratory/LabAPI/plugins/global/    (or plugins/<port>/)
```

and restart the server. The build compiles against the game's own `SCPSL_Data/Managed` assemblies
(see `tools/SlKit/Directory.Build.props`), never against NuGet reference packs.

Tests of the rule and the config defaults run on plain .NET, without the game:

```
dotnet run --project tools/tickrate-fix/tests/TickrateFix.Tests/TickrateFix.Tests.csproj
```

## Configuration

`LabAPI/configs/<port>/TickrateFix/config.yml`, created on first start:

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `false` | `true` re-applies `server_tickrate` at every `WaitingForPlayers`. `false` only logs the measurement line and changes nothing. |
| `cap` | `60` | Frame rate used when `server_tickrate` is `-1` or `0` (unlimited), 1 to 32767 (anything else means 60). A `server_tickrate` above it is **not** lowered. |

Both are read at every `WaitingForPlayers`, so after `labapi reload configs` the new values apply
from the next round's lobby.

Suggested order: install it, leave it off for a round, read the line. If `before` shows Mirror's 60
while `server_tickrate` says otherwise, set `enabled: true` and check that the next line shows
`after` equal to your `server_tickrate`.

## What it does, exactly

At every `ServerEvents.WaitingForPlayers`, on the main thread:

1. If `enabled` is false, it reads `Server.MaxTps` and `Application.targetFrameRate`, logs the line
   with `reason=disabled-observe-only`, and stops.
2. If `Server.IdleModeActive`, it does nothing and logs `reason=idle-mode-skipped`. Assigning the
   tickrate there would replace the idle frame rate (1 FPS by default) while `Time.timeScale` is
   still 0.01, so the server would spin at full speed on a clock running 100 times slow. Leaving
   idle mode re-applies the stored tickrate by itself.
3. Otherwise it reads `Server.MaxTps` (the configured `server_tickrate`). `-1` or `0` becomes `cap`;
   any other value, including one above `cap`, is kept as configured. It assigns that value and
   logs the line.

It never throws. A failure is logged as a warning and the server keeps its current frame rate.
It runs once per round, so its cost is nil. It patches nothing and starts no timer.

The plugin does not restore anything when disabled. There is nothing correct to restore: Mirror's
value was the bug.

## Why the cap is only for "unlimited"

`-1` and `0` mean "no frame limit", and a frame loop without a limit spins as fast as the CPU
allows. The cap gives them a bounded target instead. 60 is the default because it is the rate
Mirror already sets, and the game's player-position distributor derives its send rate from the
configured tickrate clamped to 10..60, so a tickrate above 60 does not send player positions more
often. A configured value above the cap is still the owner's explicit choice, so it is applied as
written; the plugin only warns once.
