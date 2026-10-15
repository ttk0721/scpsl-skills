# Where the test cases come from

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of testing-scpsl-plugins.

The tests that matter most are business rules the owner never mentions unless asked: who is excluded, what must never happen, who else already does this job. Skipping these questions produces code that looks correct but implements the wrong rules. Ask them once, in one message, before writing a medium or large feature's tests. For a small change, ask nothing.

If the design is not approved yet, that is designing-scpsl-plugins first, not tests.

## Questions and the tests they become

| Ask | Becomes |
|---|---|
| Who must be excluded (staff, Overwatch, dummies and NPCs, spectators, a named list)? | One exclusion test per category, each with an included player in the same run as a control |
| What are the numbers (threshold, cap, cooldown, window)? | Boundary tests just below, at and above each number |
| What must never happen (an automatic ban, a wrong payout)? What does a false positive cost? | A test that one signal alone cannot reach the harmful action; a shadow mode that only counts |
| What exists already that does the same job (an external API that grants ranks, an HUD in a corner, hints refreshed on every spawn)? | A test that you do the minimum (act on change, not per tick); the overlap itself goes to the manual checklist |
| Which plugins touch the same things (custom roles, map editors, EXILED or LabAPI)? | Nothing at L1/L2: checklist items, and a lint/review pass |
| How many players, and how often do rounds restart? Is there an idle mode overnight? | A reset test at `WaitingForPlayers`; a big-gap tick test; an allocation test sized to the player count |
| Do several servers share the data? | Idempotency: replaying the same event or flush changes nothing; keys are `UserId` plus period |
| Where does the effect go (HTTP, database, file) and what can fail? | A fake sink that returns each status class: success, transient, permanent, timeout. Assert the terminal state (delivered once after recovery), the bound (an outage owes one item, not a queue), and decide whether a counter means items or attempts and name it so |
| What does a player leaving mid-action mean? | `Left` removes state; a rejoin starts from zero; an unresolvable id does not throw |
| Config edited by hand, or reloaded mid-round? | Null config, bad values clamped, rules read through a `Func` |

## Rules for writing the test

- One behaviour per test, named after the rule ("StaffNeverEarn"), not the method.
- Every "nothing happens" test gets a positive control in the same run, otherwise an empty stub passes it.
- Name the production change that would make the test fail before you write it. Then do it once (mutation) and see only that test fail.
- A failing test must fail for the missing behaviour, not for a typo or an exception in the fake.
- Prefer the real Core class over a mock of it. Fakes only at the seams (world, effect, time).

## Re-verifying on a new build

- Check that the owner-facing questions still cover the rules a medium or large feature depends on.
- Re-check which exclusions the game exposes (`IsHost`, `IsNpc`, `IsDummy`, `RemoteAdminAccess`, `IsOverwatchEnabled`) on `Player`.
