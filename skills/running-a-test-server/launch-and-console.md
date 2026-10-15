# Launch, console, dummies

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Linux recipe, with EXILED 9.14.2 loaded. The scripts here are Linux-only; the Windows section at the end is a manual procedure and unverified.

## Launch command

Run from the install folder (`start-server.sh` adds `--acceptEULA` only when given `--accept-eula`, see the table):

```
./LocalAdmin <port> [--acceptEULA] --useDefault --noSetCursor --noTerminalTitle --disableTrueColor
```

| Flag | Why |
|---|---|
| `--acceptEULA` | No interactive prompt. It accepts the game's EULA FOR the owner, so it is an explicit opt-in. `start-server.sh` never passes it by itself: only `--accept-eula`, given after the owner said they accept, does. If LocalAdmin already stored an acceptance (`EulaAccepted` in `config/localadmin_internal_data.json`, read-only) it does not prompt, so the script starts without the flag; that record is machine-wide, not per install, and decides nothing for the owner. No record and no `--accept-eula`: the script refuses (exit 7), because LocalAdmin would block on its prompt. Never decide this yourself |
| `--useDefault` | Accept default config instead of asking (creates `config/<port>/` for a new port) |
| `--noSetCursor`, `--noTerminalTitle`, `--disableTrueColor` | No terminal control codes in a redirected log |

Other flags present in the binary (read from its strings, not all tried): `--printStd`, `--noLogs`, `--config`, `--gameLogs`, `--noAlign`, `--restartsLimit`, `--restartsTimeWindow`, `--logLengthLimit`, `--logEntriesLimit`, `--noAutoFlush`, `--printControl`, `--reconfigure`, `--skipHomeCheck`, `--dismissPluginManagerSecurityWarning`.

Boot to `Waiting for players...` takes about 8 s with EXILED loaded. Wait for that line, never for a fixed sleep.

## Console without a terminal (Linux)

An agent has no TTY. Give the server a FIFO as stdin and keep one end open, so a single `echo "cmd" > fifo` never sends EOF:

```
mkfifo console.fifo
exec 3<>console.fifo
./LocalAdmin 9999 <flags> <&3 3<&- >>server.log 2>&1 &
echo "slprobe status" > console.fifo
```

`scripts/start-server.sh <port>` does exactly this, refuses a port that has traces, refuses a port something is listening on right now (checked just before the launch, also for a port it started earlier; `ss`, else `/proc/net`; if neither can tell it refuses too), records the install it used in the state, refuses without a stored EULA acceptance or `--accept-eula` (see the flag table), waits for `Waiting for players`, and keeps state (fifo, log, pid, creation time) in a state folder. Every script validates its arguments (a flag missing its value is exit 64, never a wait) and checks the pid file against `/proc` before writing to the console or signalling: a pid that is not our LocalAdmin for that port is refused. Then:

```
bash scripts/start-server.sh 9999              # add --accept-eula only after the owner said so
bash scripts/send-command.sh 9999 slprobe status
bash scripts/send-command.sh 9999 /dummy spawn Bot1
bash scripts/stop-server.sh 9999
```

## Console rules

| Input | Result |
|---|---|
| `slprobe status` (a plugin's console command) | works with no slash |
| `/dummy spawn Bot1` (Remote Admin command) | works; the leading slash is required |
| `dummy spawn Bot1` | `Command dummy does not exist!` |
| `/forcestart` | only in the lobby: `This command can only be ran while in the lobby.` (also registered for the console, so it works without the slash too) |
| `exit` | graceful: `Server shutdown initiated`, players notified, connections delayed 5 s, exit code 0 within seconds |
| A plugin's Remote Admin command | needs the leading slash like any RA command; without it `Command <name> does not exist!` |
| `/roundlock` | a **toggle**: the answer says `Round lock enabled!` or `Round lock disabled!`. Read it; a second call turns the lock off |
| `/list` | the console log shows only the header `List of players (N):`, not the players. Get ids from a command that prints them |
| anything typed during a round restart | queued: it runs after the scene load, after `Waiting for players` |

Player ids are not stable across rounds: after a round end or restart a reconnecting client gets a new id, so look the id up again before each `forceclass`.

The source also has `/dummy destroy all` (and per-name destroy; `dummy` is an alias of `dummies`) and a console-only `idle` command: `idle` shows the state, `idle enable` refuses while real players are connected, `idle force` does not. Dummy commands need the facility-management permission; the server console has all permissions.

## Dummies

- The first dummy ends idle mode at once (`Server has exited the idle mode`). With no players the server idles at about 1 frame per second (target frame rate 1 and a time scale of 0.01), so every number taken then describes a sleeping server.
- A dummy created after the round started takes the late-join path and becomes a spectator; assign roles explicitly. See measuring-scpsl-performance (protocol.md) for what dummies do not exercise: sockets, auth, voice, client commands.
- Dummies are destroyed at every round restart, so a restart test needs fresh ones. They never raise `Joined` and all share the user id `ID_Dummy`: they prove round, role and per-player loop logic, not join logic, networking or voice.
- A round made only of dummies can end within about 15 to 30 s (exact figure unverified). `Round.IsLocked = true` stops that; the lock survives restarts, so clear it afterwards.
- Never spawn dummies, `/forcestart` or restart a round on a server with real players.
- Destroy dummies when done (`/dummy destroy all`); a leftover keeps the server out of idle mode.
- Spawn them gradually when measuring: each spawn sends a burst to its connection.

## Is the instance private?

`bash scripts/check-private.sh <port>`. Two independent signals:

1. `contact_email` in `config/<port>/config_gameplay.txt` is `default` or empty. A new port gets the template, where it is `default`, which the game reads as empty. If it holds an address, the owner may be publicly listed on that port: stop.
2. After boot the game logs `Your server won't be visible on the public server list - (<public IP>)` and explains that `contact_email` (plus being reachable) is needed for a listing. Seeing that line is the confirmation.

Private only means "not on the list". The UDP port still accepts anyone who types the address, so do not forward the port on the router, and tell the user the instance is local-only.

## Shared plugins run on the test port too

LabAPI defaults to `plugin_paths: [global, $port]`: everything in `plugins/global` also loads on the test instance, and EXILED's `Plugins` folder is shared by every port. If those plugins talk to Discord, a database, a stats panel or a ranks API, your test round writes to production.

- Before the first start, list `plugins/global` and the EXILED plugins and show the user what will load on the test port.
- Ask whether to leave them on or isolate the test instance. Isolating means writing `<root>/LabAPI/LabApi-<port>.yml` before the first start with only `plugin_paths: ["$port"]` (key name from the loader's default config; leave `dependency_paths` alone, libraries are harmless and your plugin may need `global` ones). The loader source reads an existing file and fills missing keys with defaults, so a partial file should be honoured; that is code-derived and NOT run, so check the boot log for which plugins enabled. Isolating also drops EXILED when its loader sits in `plugins/global`, so a plugin that needs EXILED cannot be tested this way.
- EXILED has a self-updater that may rewrite its own files while a server runs (what it touches was not read). Another reason to ask before starting a test instance next to a live EXILED install, and to not let it start on a port whose EXILED copy you do not own.
- Plugins can also write beside the game, for example into a data folder in the install directory. `list-created.sh` scans the install folder for that.
- Put test DLLs only in `plugins/<port>` and `dependencies/<port>`, never in `global`: `global` would load them on the owner's other ports.

## Windows (unverified, manual)

The scripts do not run on Windows, Git Bash included: they need `mkfifo`, `setsid` and `/proc`, and each one exits 69 there. Nothing below was tried on a Windows machine; it is a procedure to follow by hand with the owner, in the same order as the Linux protocol (SKILL.md). Say "Windows launch unverified" in the report.

1. Consent and port choice exactly as in SKILL.md steps 1 to 5. Find the install from the Windows table in paths.md by looking at those folders, and use `dir "%APPDATA%\SCP Secret Laboratory"` to see the owner's ports. `netstat -ano` shows listeners. A port with any trace is the owner's.
2. EULA: the owner accepts it in their own window on first run. Do not pass `--acceptEULA` for them.
3. Launch: ask the owner to run `LocalAdmin.exe <port> --useDefault` in their own terminal window, from the install folder (the executable name is assumed from the Linux layout). Console commands are typed there by them, or pasted from you; ask them to paste the log back. Alternative, not tried: PowerShell `System.Diagnostics.Process` with `RedirectStandardInput = true` kept open and output redirected to a file.
4. Checks: read `config\<port>\config_gameplay.txt` for `contact_email` and the LocalAdmin log for the "won't be visible" line, as in "Is the instance private?" above.
5. Clean up by hand: show the owner the paths you think the test created, using creation time (`(Get-Item <path>).CreationTime` in PowerShell, unverified), and delete nothing without their yes. Never delete `Round Logs Archive` zips.

If a step turns out to work or fail, record it here with the date and drop the "unverified" mark.

## Re-verifying on a new build

- Run the launch command on a free port and confirm the boot lines (`Waiting for players...`, the private-list line) and the flags still exist (`LocalAdmin --help` if available, or its binary strings).
- Check that `dummy spawn`/`destroy`, `forcestart` and `idle` still exist and keep their lobby and permission conditions (public command classes in the game).
- Re-test `exit` for exit code and that no `SCPSL` process remains.
- Re-check the shared-plugin defaults (`plugin_paths`, `dependency_paths`) in a freshly written `LabApi-<port>.yml`.
