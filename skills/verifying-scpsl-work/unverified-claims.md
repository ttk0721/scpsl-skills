# Stating what was not verified

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of verifying-scpsl-work.

A hand-off lists what you proved, what you did not, and how the owner closes each gap. Hiding a gap is worse than the gap: the owner deploys believing it is covered.

## Template

```
VERIFIED (command -> result)
- Build: dotnet build -c Release against Managed -> 0 Error(s), 0 Warning(s)
- Lint: scpsl_lint.py src/ -> exit 0
- Unit tests (rate-limit logic): 12 passed, 0 failed

NOT VERIFIED (why -> how you check)
- Loads on your server: not run (no local test server / you chose no local tests)
  -> after copying the DLL to plugins/<port> and restarting, grep the boot log for
     "Successfully enabled 'MyPlugin'".
- Handler fires: needs a real join.
  -> join once; expect "first join this round handled" in the console.
- Cost on 60 players: not measured. Estimate: one dictionary lookup per join (ESTIMATE).
  -> install sl-probe, `slprobe reset` after round start, `slprobe report` before round end,
     compare against a round without the plugin.
- Round restart: not run -> after a round ends, expect "round reset: previous round ..." once.

ENVIRONMENT GAPS
- Tested with 3 dummies, not clients: no sockets, auth, voice or latency, and no `Joined` event (dummies never raise it).
- Other plugins touching the same state were not present.
```

## Rules

1. **Every "not verified" line has an owner check**: where to look, what string or number to expect. "Please test" is not a check.
2. **One status per claim**: VERIFIED, ESTIMATE (reasoned, with the assumption) or NOT VERIFIED. Never a fourth word such as "should".
3. **Say what was skipped on purpose** ("you asked for no local tests"), so the gap reads as a decision, not an oversight.
4. **Small changes get the short form**: "Builds (0 errors), lint clean. Not run on a server: join once and look for `<line>`."
5. **State what you left on disk**: a test instance, a copied DLL, an extra config folder, and what you removed.
6. If a check failed, say so first, with the output.

## Re-verifying on a new build

- Update the example strings when log wording changes (the enabled line, the private-server line).
- Check that every command named in the owner checks still exists in the current sl-probe.
