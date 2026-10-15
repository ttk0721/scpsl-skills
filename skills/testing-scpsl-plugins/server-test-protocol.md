# Server tests (L4 and L5)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of testing-scpsl-plugins.

The protocol for running a server (consent, finding the install, ports, shared plugins, EULA, privacy, launch, driving, cleanup) lives only in the running-a-test-server skill. This file does not repeat it. It adds what L4 and L5 need on top: which checks make a pass, and what the result may claim. Declined or impossible: skip L4/L5, give `manual-checklist.md`, and list what stays unverified.

## Install the test build into the test port

Put the plugin in `LabAPI/plugins/<port>/` and SlKit in `LabAPI/dependencies/<port>/`, never `global`. (A global plugin whose dependency exists only under another port fails with `ReflectionTypeLoadException` on every other port.)

## Check the boot log

`Successfully enabled '<Name>'` for your plugin, no `Couldn't enable the plugin`, no `Couldn't load the plugin`, no `Failed to load the configuration file`, no exception after it.

## Drive it

Cycle at least two round restarts and a join/leave if you can. Dummies are destroyed at every restart, so spawn fresh ones each time (commands and console rules: running-a-test-server/launch-and-console.md).

## What L4 may claim

"Loaded, enabled, survived N restarts, the dummy scenario produced the expected log lines." Not "works for players": dummies carry no voice, hints, latency or client code. Put the rest in the checklist.

Do not take performance numbers while a public server runs on the same machine: you would measure both.

## Re-verifying on a new build

This file holds no version-specific facts of its own: it points to running-a-test-server. After a game or LabAPI update, re-verify that skill's paths.md and launch-and-console.md (LocalAdmin flags, the console prefixes, log locations), then check the links here still point at the right sections.
