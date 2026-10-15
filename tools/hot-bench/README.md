# hot-bench

A measurement plugin for test servers only. It measures what a subscription to a
per-frame-per-pair event (`PlayerEvents.ValidatedVisibility`) costs, and how that compares with
the work a handler does per raise.

Server console command `hotbench off | light | heavy | costly | stats`:

- `off`: no subscriber.
- `light`: a handler that only increments a counter. It measures the subscription itself (the
  per-raise `Delegate[]` and the call).
- `heavy`: reads both players' `Role` and the target's `Position`. These are reads only: no
  allocation, no lookup, no logging.
- `costly`: the kind of handler a careless per-pair rule ends up as. On every raise it builds an
  interpolated string, allocates a small `List<string>` and calls `Player.Get` once.
- `stats`: handler calls and rate since the previous `stats`.

## Use with sl-probe

Run on a separate test instance. The running-a-test-server skill covers it: never use the owner's
ports, and check the instance is private first. Lock the lobby (`/lobbylock`, and `/roundlock` so
the round cannot end), spawn the dummies, then `/forcestart`: dummies that join a running round
stay spectators and are not counted as targets. Repeat every arm at least twice in a mixed order,
because one window per arm cannot separate the effect from drift:

```
hotbench <mode> → slprobe reset → wait 30 s → slprobe report → hotbench stats
example mixed order: off light heavy costly costly heavy light off
```

Compare busy-time mean, p50, p95 and p99 between arms, with the spread across repetitions.

## Results on SCP:SL 14.2.7 / LabAPI 1.1.7 (one machine)

This README is the **single home of these numbers**. Other files in the repository point here
instead of repeating them, so a re-measurement changes one place.

**Setup, and what limits it.**
- Machine: one desktop PC, AMD Ryzen 9 9950X3D (16 cores, 32 threads), Linux. The server and its
  dummies ran on this one host. Absolute milliseconds will be higher on a typical rented game
  server, so read the differences between arms, not the totals.
- **Dummies only, no real players.** A dummy is a server-side player object with no network client:
  it takes part in the per-pair visibility work as receiver and target, but nothing is sent to it and
  no one moves or shoots. No real client was connected in any run.
- One server build (14.2.7), one LabAPI version (1.1.7), a separate test instance on its own port.
  EXILED's loader and some other plugins were also loaded on it, besides SlKit, sl-probe and
  hot-bench. Whether any of them did per-pair work was not checked.
- Other load on the machine was not controlled or recorded.

**How many raises to expect.** The game raises `ValidatedVisibility` once per receiver and target
on every position send (60 per second at the default tickrate). Receivers are all verified players,
spectators included; targets are only players in a first-person (FPC) role, so spectators are not
targets. With N players who are all alive that is about N × (N − 1) × 60 per second: 212,400 for
60. Dummies that join after the round started stay spectators, which is why runs 2 and 3 saw fewer
raises than their dummy count suggests (about 33 and 30 alive targets).

**Run 4.** 60 dummies, **all alive**: the lobby was locked (`/lobbylock`) before
the first dummy joined and the round was started with `/forcestart`. 60 fps, 2 windows of 30 s per
arm, mixed order. Values are means across windows; brackets give the spread.

| Arm | Handler calls/s | Busy mean | Busy p50 | Busy p95 | Busy p99 |
|---|---|---|---|---|---|
| off | 0 | 10.59 ms [10.40–10.79] | 10.43 | 12.05 | 14.06 |
| light | 208,500 | 10.75 ms [10.60–10.90] | 10.58 | 12.50 | 14.59 |
| heavy | 208,500 | 10.75 ms [10.67–10.82] | 10.55 | 12.51 | 14.71 |
| costly | 207,200 | 12.16 ms [12.04–12.28] | 11.64 | 15.24 | 20.77 |

What the result supports:
- 208,500 raises/s is 98% of the 212,400 the formula gives for 60 alive players, so the full-server
  rate is measured, not extrapolated.
- A counter-only handler cost about 0.16 ms per frame (about 46 ns per raise). Run 2's numbers
  scaled linearly to this rate predict about 0.2 ms.
- One string, one small list and one lookup per raise (`costly`) cost about 1.6 ms of mean busy
  time per frame (about 450 ns per raise); the linear prediction was 1.6 ms. **p99 rose by 6.7 ms**,
  four times the mean, which fits garbage-collection pauses from the per-raise allocations. That
  was not measured separately.

**Cost per raise at different rates.** The per-raise cost stays flat while the rate changes:

| Run | Dummies (alive targets) | Raises/s | `light` per raise | `costly` per raise |
|---|---|---|---|---|
| 2 | 60 (about 33) | 116,700 | about 50 ns | about 450 ns |
| 3 | 90 (about 30) | 160,000 | about 90 ns | about 390 ns |
| 4 | 60 (60) | 208,500 | about 46 ns | about 450 ns |

The `light` differences are within the spread of a 0.1 to 0.2 ms effect; `heavy` came out cheaper
than `light` in run 3, which shows the size of that noise.

**Run 2.** 60 dummies, round running, 60 fps, 3 windows of 30 s per arm.

| Arm | Handler calls/s | Busy mean | Busy p50 | Busy p95 | Busy p99 |
|---|---|---|---|---|---|
| off | 0 | 7.75 ms [7.70–7.79] | 7.58 | 9.20 | 11.39 |
| light | 116,700 | 7.86 ms [7.79–7.93] | 7.66 | 9.57 | 11.55 |
| heavy | 116,700 | 7.88 ms [7.84–7.90] | 7.71 | 9.35 | 11.20 |
| costly | 116,500 | 8.62 ms [8.55–8.72] | 8.34 | 10.79 | 12.88 |

At about 117,000 raises/s the counter-only handler cost about 0.1 ms per frame and `costly` about
0.9 ms, with p99 up by 1.5 ms. The spreads did not overlap with `off`.

**Run 3.** 90 dummies, most of them spectators (they joined after the round started), 160,000
raises/s, 2 windows of 30 s per arm. Busy mean: off 9.08 ms, light 9.32, heavy 9.20, costly 10.12;
p99 off 12.88, costly 16.66.

**Run 1 (weak design).** 30 dummies, one 40 s window per arm, p50 only. It found no difference
between arms, at about 41,700 raises/s. It was too weak to resolve a 0.1 ms effect, and it had no
allocating arm.

Not measured:
- Real players. Dummies are receivers too, but they have no network clients, so the figures say
  nothing about how real traffic, movement or shooting changes the per-pair work.
- More than 60 alive players, and GC over hours of uptime from the per-raise `Delegate[]` and the
  `costly` allocations. The p99 jump in run 4 is the first sign of the second, but it was not
  separated from other causes.
- Other machines.

Build: `dotnet build tools/hot-bench/src/HotBench.csproj -c Release` (same reference setup as SlKit).
