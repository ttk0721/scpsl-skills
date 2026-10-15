---
name: running-a-test-server
description: "Use when about to start, restart or script a local SCP:SL dedicated server to test a plugin, when looking for the server install, config or log folders on Linux or Windows, when sending console or Remote Admin commands, spawning dummies, or cleaning up after a test run."
---

# Running a test server

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A local server belongs to its owner and may hold their ports, logs and live data. Ask before you run anything, look paths up instead of searching, give the test its own port, delete only what you created.

**This skill is the one copy of the test-server protocol.** The test-server notes in debugging-scpsl-plugins, verifying-scpsl-work and testing-scpsl-plugins only point here.

**The scripts are Linux-only.** Git Bash, MSYS and Cygwin are unsupported: every script exits 69 there. Windows is a manual, unverified procedure in launch-and-console.md.

## When to Use

- A test needs a real server process: plugin loading, events, dummy load, frame measurement.
- NOT when build plus lint answer the question: offer a run in one line, never push it.
- NOT for which log strings mean what (deploying-scpsl-plugins) or judging frame numbers (measuring-scpsl-performance).

## Process

1. **Consent first.** Ask once: "Do you have a dedicated server installed, and do you want local tests?" No, unsure or unwanted: stop, deliver with build and lint evidence, say nothing ran on a server. "No" is final for the session. Do not ask again.
2. **Find the install.** `bash scripts/find-install.sh`: default paths and Steam `libraryfolders.vdf` only. Never `find`, `locate`, `grep -r` or any walk over the home folder or disk (paths.md). Not found, different or several: ask, reuse the answer.
3. **Inventory ports.** `bash scripts/list-ports.sh`. Every port with traces is the owner's.
4. **Ask which port.** Recommend a separate instance on a free port (`list-ports.sh --suggest`, e.g. 9999): "one of your ports, or our own instance?" Their port only on an explicit yes, never one that is running or has players; then back up what you replace and never restart it unasked.
5. **Check shared plugins.** `plugins/global` and EXILED plugins also load on the test port. Show the list, ask whether they may run (launch-and-console.md). Before the yes, say what a start costs: a second process on a machine that may host live players, and old round logs get archived.
6. **EULA.** `--acceptEULA` accepts the game's licence for the owner. `start-server.sh` never passes it on its own, and refuses (exit 7) without a stored acceptance or `--accept-eula`. Ask the owner to accept it themselves or to say they accept it, and only then add `--accept-eula`. Never add it on your own.
7. **Check privacy.** `bash scripts/check-private.sh <port>` before launch (`contact_email` unset) and after boot (the "won't be visible on the public server list" line). Email set: stop and ask. Private means unlisted, not unreachable.
8. **Launch.** `bash scripts/start-server.sh <port>`. Test DLLs go in `plugins/<port>` and `dependencies/<port>`, never `global`. Confirm `Successfully enabled '<Name>'`.
9. **Drive it.** `bash scripts/send-command.sh <port> <command>`: plugin commands plain, Remote Admin commands with a leading `/`. Never run `/forcestart`, dummy spawns or round restarts where real players are.
10. **Measure with sl-probe**, never `Server.Tps` (measure-and-cleanup.md).
11. **Stop and clean up.** `stop-server.sh`, then `list-created.sh`; remove only `OURS` paths, after showing the user the list. Never remove `Round Logs Archive` zips.
12. **Report** what ran, on which port, and what was not tested (dummies are not players; Windows unverified).

## Common Mistakes

- Starting on the owner's port because it "is empty". Empty means idle, not unused.
- `--accept-eula` before the owner said so, or `--acceptEULA` typed by hand.
- `pkill LocalAdmin` to stop: it also kills the owner's servers. Use `stop-server.sh`.
- Signalling a pid straight from a pid file: pids are reused, and `kill -- -<pid>` takes a whole process group.
- Working around the Git Bash refusal.
- Test DLLs in `global`: the owner's other ports load them.
- Cleaning by modification time (configs are rewritten on every load) or deleting the old-log zips.
- `dummy spawn` without `/`: "Command dummy does not exist!".
- A global plugin needing a per-port dependency fails on other ports.

## Red Flags

| Thought | Reality |
|---|---|
| "They surely have it installed, I'll start it" | Ask. Some have no server or want no local tests |
| "Just a minute on 7777" | Ports are never borrowed without an explicit yes |
| "I'll `find` it, it is quicker than asking" | Default paths and `libraryfolders.vdf` only. Then ask |
| "The EULA flag is just a formality" | It is the owner's acceptance of a licence. Theirs to give |
| "Everything new is mine" | LabAPI and EXILED touch files on every load; use birth time and the port name |
| "TPS reads 60" | Not a measurement |
| "The zip is junk" | It is the owner's only copy of old logs |

Facts and recipes: paths.md, launch-and-console.md, measure-and-cleanup.md. Script tests: `python3 -m unittest discover -s tests/hooks` from the repository root.
