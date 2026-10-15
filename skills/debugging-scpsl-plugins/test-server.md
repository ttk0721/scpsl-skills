# Test servers while debugging

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Starting, stopping or touching a server, finding the install, choosing a port, the EULA, privacy and cleanup are all in the running-a-test-server skill. It is the only copy of that protocol, so this file does not repeat it: ask first, run nothing and copy no DLL before the owner's yes. Paths for each OS are in `running-a-test-server/paths.md`. What follows is only what debugging adds.

## Which log answers which question

Folder locations: `running-a-test-server/paths.md`.

| Question | Read |
|---|---|
| Did the plugin load and enable? Plugin `Logger.*` output, EXILED output, stack traces | LocalAdmin log (`LocalAdminLogs/<port>/`, newest file is the latest boot) |
| What did the game itself record (round events, admin commands)? | Round log (`ServerLogs/<port>/`; not plugin console output) |
| Which RA commands did an admin run? | `EXILED/Logs/<port>-RAlog.txt` and the Round log |

Reading a log is read-only and needs no yes. `Round Logs Archive <date>.zip` is the owner's data: never delete it.

Logs contain SteamIDs and IP addresses. Read them for the bug; never copy lines into a public repo, issue or PR.

## Debugging on a test instance

- A bug that only shows in a running round needs dummies, and a dummy is not a player: no `Joined` event, one shared user id (`ID_Dummy`), no sockets, auth or voice (launch-and-console.md in running-a-test-server). If the bug involves any of those, say the reproduction is incomplete instead of calling the bug gone.
- Anything timing-dependent needs a running round: an empty server idles at about 1 frame per second.
- The owner's pasted log is evidence you may use without any server. When the owner declined local tests, ask them to run one specific command and paste the output back.
- Check for a `hoster_policy.txt` before concluding a log or config folder is missing (paths.md, "What moves the config root").

## Re-verifying on a new build

This file holds no version-specific facts of its own: it points to running-a-test-server. After a game or LabAPI update, re-verify that skill's paths.md and launch-and-console.md (LocalAdmin flags, the console prefixes, log locations), then check the links here still point at the right sections.
