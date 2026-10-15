# Test servers as evidence

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of verifying-scpsl-work.

Whether and how a server may be started (the questions, the install and port rules, the EULA, privacy, cleanup) is defined once, in the running-a-test-server skill. This file does not repeat it and adds only what a runtime claim needs from a run. No yes from the owner means no run: the claim stays unverified and the owner gets the check (unverified-claims.md).

## What counts as evidence from a run

- A boot log line you read yourself, from the log of the run you started, naming the plugin (`Successfully enabled '<Name>'`), not a build that succeeded.
- Output of the command you sent and the log lines it caused, copied with the command that produced them (`send-command.sh` prints both).
- A before/after pair for a fix: the failing behaviour on the old build, the same step passing on the new one.
- Measurements from sl-probe, never `Server.Tps` (running-a-test-server/measure-and-cleanup.md).

A run on the owner's port, or one where you could not tell which plugins were loaded (global and EXILED plugins load too), is weaker evidence: say which.

## What a local run cannot prove

Real clients, latency, voice, auth cost, other plugins on the owner's servers, a full player count, and any data shared between the owner's servers. Dummies are not players: they raise no `Joined` and share one user id. List these under "Environment gaps" in the report, together with what was not tested on Windows (the scripts are Linux-only, so a Windows run is manual and unverified).

## In the report

Say which port ran, that it was a separate instance or the owner's, which plugins were loaded beside yours, and what you removed afterwards and what you left.

## Re-verifying on a new build

This file holds no version-specific facts of its own: it points to running-a-test-server. After a game or LabAPI update, re-verify that skill's paths.md and launch-and-console.md (LocalAdmin flags, the console prefixes, log locations), then check the links here still point at the right sections.
