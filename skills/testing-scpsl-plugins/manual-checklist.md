# What cannot be tested automatically, and the owner's checklist

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of testing-scpsl-plugins.

Silence about a gap is worse than the gap. Every report names what no layer checked and hands the owner a short list they can run on their own server.

## What no automated layer proves

| Gap | Why | Who can check |
|---|---|---|
| Client visuals: hint position, size, overlap with an existing HUD or another plugin's hints, SSS menu layout | The client renders them; dummies have no client | A person with a game client |
| Voice: audibility, mute routing, audio quality | Dummies carry no voice; the L2 fakes have none | Two or more real clients |
| Real latency and jitter: pop-in from visibility filters, rubber-banding | Dummies have no sockets or RTT | Real players, ideally with a baseline |
| Behaviour with real player counts and real play: false positives of a detector, reward abuse | Scripted dummies do not play like people | Production in shadow mode: count, do not act |
| Interaction with the owner's other plugins (custom roles, map editors, anything that refreshes everyone's hints on spawn) | They exist only on the owner's server | The owner's server |
| Data shared between several servers (an external API, a database) | One test instance has no siblings | Two instances against the same store |
| Long-run behaviour: hours of round restarts, idle mode overnight, memory growth | Smoke runs last minutes | The owner's server, a day |
| Join-time cost: authentication and handshake | Dummies skip both | Production with a baseline |

## Checklist template to hand over

Write it for someone who is not a programmer: exact steps, the expected sight or log line, and what to send back. Fill the brackets.

```
Plugin: [Name] [version]   Server: [port]   Date: [date]

Install
[ ] Copy [file] to LabAPI/plugins/[port]/ (and [dependency] to LabAPI/dependencies/[port]/). Restart the server.
[ ] In the boot log, find:  Successfully enabled '[Name]'
    If you see "Couldn't enable" or "Couldn't load the plugin", send me those lines.
[ ] Open configs/[port]/[Name]/config.yml. [Setting] must show [value].

Behaviour
[ ] [Step a real player does]. Expected: [what they see or what the log says].
[ ] Leave and rejoin. Expected: [state starts fresh / is kept].
[ ] Let the round end and restart twice. Expected: [no errors, state reset].
[ ] As a staff member: [action]. Expected: [excluded].
[ ] Set [kill switch] to false, run "labapi reload configs". Expected: [feature stops].

Look and feel (needs a person on a client)
[ ] [HUD/hint] is readable and does not cover [existing element].
[ ] Other plugins' hints and roles still behave: [list from the owner].

After an hour
[ ] Search the server log for "Exception" and "[Name]". Send anything unexpected.
```

Rules for the list: one observable result per line; no step that needs the source code; name the log string to search for; state what to send back when it differs.

## What goes in the report

1. Per layer: what ran, the command, the result (test counts, lint line, build line).
2. Not tested, and why (the table above, trimmed to what applies).
3. The checklist.
4. The assumptions you made about the owner's setup.

## Re-verifying on a new build

- Re-run the checklist's log strings (`Successfully enabled`, `Couldn't enable the plugin`, `Failed to load the configuration file`) against a real boot.
- Confirm `labapi reload configs` still exists and what it does and does not re-run.
- Revisit the gap table when a new test layer appears (a client simulator, a packet-level harness).
