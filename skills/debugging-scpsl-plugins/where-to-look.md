# Where to look, and how to reproduce

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Paths per OS: test-server.md.

## 1. Reading the logs, in this order

1. **The boot loader block** of the LocalAdmin log of the failing run. Every plugin must have a `Successfully enabled '<Name>'` line. A missing line is a finding on its own.
   - Linux: `grep -nE "Successfully enabled|Couldn't|Failed to load the configuration|Unable to register|Potential issue|Forcefully" "<LocalAdmin log>"`
   - Windows PowerShell: `Select-String -Path "<LocalAdmin log>" -Pattern "Successfully enabled|Couldn't|Failed to load the configuration|Unable to register|Potential issue|Forcefully"`
   - Meaning of each string: verify-and-brand.md in deploying-scpsl-plugins.
2. **The first exception after the incident, not the last.** A persistent fault repeats; the first line has the context.
3. **The stack trace, not the header.** LabAPI prefixes event exceptions with `[LabApi]`, and the header names the last-registered handler, which is not necessarily the one that threw (labapi-events).
4. **The plugin's own prefix.** `Logger.*` tags lines with the calling assembly's name, so a shared helper assembly relabels everything.
5. **The round log** for what the game recorded: round start and end, admin commands. Plugin output is not there.
6. **The version lines.** Game version (also the first lines of the round log), LabAPI version and EXILED version at boot, and the `Version:` printed by each `Successfully enabled` line (it tells you which build of your plugin actually ran). A bug that appears after an update starts here (researching-scpsl-internals).

Do not read durations from boot-output timestamps: in LocalAdmin boot output, consecutive lines can be stamped exactly 10 ms apart. The order of lines is real; the gaps are not. Measure time with `Stopwatch` in code (measuring-scpsl-performance).

When you have no access to the server, ask the owner for exactly two things: the boot block from step 1 and 30 lines around the first exception. Give them the grep command for their OS.

## 2. Reading live state: the DebugDoctor example

`examples/debug-doctor` is a compiled LabAPI plugin that answers the questions logs cannot. It subscribes to nothing at boot. Commands work in Remote Admin and the server console and need the `ServerConsoleCommands` game permission.

| Command | Answers |
|---|---|
| `sldoctor plugins` | Which plugin instances LabAPI created, which it enabled, which share a `Name`. Absent from the list = never instantiated; listed but NOT ENABLED = skipped or threw in `Enable` |
| `sldoctor subs PlayerEvents.Hurting` | Who is subscribed to an event, in the order they run, with assembly and version. Is my handler registered at all? Is it registered twice? Who runs after me? |
| `sldoctor count PlayerEvents.Joined 30` | Is the event raised at all in 30 s, how often, on which thread, and how many raises were already cancelled by an earlier handler |

The counting probe is the whole trick: a temporary subscriber that only increments counters, then removes itself and logs one line.

```csharp
public void OnRaised<T>(T args) where T : EventArgs
{
    Interlocked.Increment(ref _raised);
    if (!MainThread.IsMainThread)                    // SlKit: true only on Unity's main thread, also before install
        Interlocked.Increment(ref _offMainThread);
    ICancellableEvent c = args as ICancellableEvent;
    if (c != null && !c.IsAllowed)                   // we subscribed last, so we see earlier cancels
        Interlocked.Increment(ref _alreadyCancelled);
}
```

Hot events (catalog) are refused unless you add `force`. The probe subscribes last, so it sees cancels made by handlers registered before it and nothing about later ones. It reads event fields by reflection (the same technique as `HotEventAudit` in SlKit), so it needs no per-event code. `plugins` lists only what LabAPI itself loaded: plugins that EXILED loaded from its own folder are absent, and that proves nothing. Install: build it, copy `Examples.DebugDoctor.dll` to `plugins/<testport>/` and `SlKit.dll` to `dependencies/<testport>/` (one SlKit copy only; check the folder first). That is a change to a server: ask first (test-server.md). Whether Remote Admin truncates long responses was not investigated; read long output from the server console.

Not verified on a live server: it compiles and lints clean; the reflection pattern is the one `HotEventAudit` uses.

## 3. Reproduction ladder (cheapest first)

1. **Pure logic on plain .NET.** If the bug is in your arithmetic, state machine or parsing, move the logic away from `Player`/Unity types and test it like any other code. Most bugs in rate, queue and window logic end here.
2. **Compile and lint.** `scpsl-lint` catches the hot-handler, off-thread, `Server.Tps`, `Disable()` and ignored-setter classes (rules SL001-SL017) without a server.
3. **A test instance** (test-server.md) with dummies and RA commands. Remember what dummies are not: they raise no `Joined`, share one user id (`ID_Dummy`), and skip sockets, auth and voice. They prove round, role and per-player loop logic.
4. **Round-restart bugs need two rounds.** Reproduce across an actual restart, not within one round (scpsl-game-lifecycle). Dummies are destroyed at every restart: spawn them again, and expect a dummy spawned after round start to be a Spectator.
5. **Read-only instrumentation on the real server**, with the owner's consent: counters, a rate-limited log, a 30 s `sldoctor count` on a non-hot event.
6. **Ask the owner to trigger it** and paste the log. Better than a guess.

If you cannot reproduce: say so, add instrumentation, wait for the next occurrence. Do not fix an unreproduced bug and call it fixed.

## 4. Instrumentation rules

- Count first, log second. A counter costs nothing; every `Logger` call costs a stack walk and two regex passes plus the string you built.
- Log through a token bucket (`RateLimitedLog` in SlKit), never bare in a handler or loop. A handler exception storm of about 5000 warnings per second was measured at 26 to 155 ms of CPU per second.
- To find who calls a method (who sends a hint, who changes a role): a temporary Harmony postfix that captures one stack trace and then disarms itself, on a test server only (scpsl-harmony-patching). Never an `AppDomain.FirstChanceException` hook: it taxes every exception in the process (scpsl-lint SL009).
- Remove instrumentation when done, or leave it behind a config switch that defaults to off.

## Re-verifying on a new build

- Run `sldoctor plugins`, `subs` and `count` on a test instance with one plugin that throws in `Enable` and one with `is_enabled: false`; compare with the loader log.
- Check the event backing fields still exist in the new `LabApi.dll` (the probe reports "backing field is not readable" otherwise).
- Re-grep the loader strings against the table in verify-and-brand.md.
