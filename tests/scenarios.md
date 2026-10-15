# Test scenarios: prompts and hidden fact sheets

The method behind [RESULTS.md](RESULTS.md), written down so anyone can repeat it with their own
agent and grader. The original run transcripts are kept privately (they quote private research
and name people). **The prompts and keys below are written out again from the run notes, not
copied from the transcripts**, so the original wording differed in detail. The structure, the
hidden facts and the kinds of mistakes graded are the same. Scores from a repeat will not match
RESULTS.md exactly. Nothing here names a person or quotes game code. The short "Key" lists below
are the outline; [answer-keys.md](answer-keys.md) is the full key with sources.

## The method

Two arms per scenario, same model, same prompt:

- **RED**: the agent has no library. No skills, no repository on disk. Closed book.
- **GREEN**: the agent can read this repository (skills and tools) and nothing else about
  SCP:SL. For test 2 it starts from `skills/using-scpsl-skills/SKILL.md`.

One run per cell is cheap and noisy (about plus or minus 2 points, see the limits in
RESULTS.md). Run each cell several times if you want to say anything about a difference of one or
two points. Keep a fresh agent per run: no shared context between runs.

Grading: one grader, given the transcript and the grading key, never told which arm produced it
if you can avoid it. Score 0 to 10 for "would this be correct and cheap on a 60-player server",
count the mistakes, and flag **critical** mistakes (break the server or the feature). Write the
key BEFORE reading any transcript, and check every claim in it against LabAPI source and your own
server. A key that nobody else can check is the weak point of the original runs. Start from
[answer-keys.md](answer-keys.md): a public key for every scenario below, where each row names the
skill file that states the fact and says whether LabAPI's source is enough to check it.

After a round: patch the skill where a GREEN agent that had read it still slipped, say so in your
results, and re-run before you quote the new score.

## Test 1: five code tasks

The prompt is one paragraph. The agent writes the plugin as text; nothing is compiled.

### s1: activity tracker with a live HUD and HTTP export

Prompt: "Write a LabAPI plugin that tracks how long each player has been active this round, shows
it to them in a corner of the screen, and sends the totals to our web panel every minute."

Key (a correct answer does most of these):

- Time is measured on the wall clock, not on coroutine waits; an empty server drops to about one
  frame per second in idle mode.
- Players are keyed by `UserId`; dummies, staff, spectators and AFK are handled on purpose.
- State resets at `WaitingForPlayers`; no `Player` object is kept across rounds.
- Data is flushed in `ServerEvents.Shutdown` and on a timer, not in `Disable()`.
- HTTP runs off the main thread on plain snapshots; failures are retried with a bound.
- The HUD re-sends on a clock with a keep-alive, one clock for all players, no coroutine per
  player, no subscription to a per-frame event.

### s2: TPS alert to a Discord webhook

Prompt: "Write a plugin that posts to a Discord webhook when the server's TPS drops below 40 for
ten seconds."

Key:

- Does not decide on `Server.Tps` (smoothed, clamped, scaled; reads nonsense in idle mode).
  Measures frame time from the player loop instead.
- Treats idle mode as no data. Rate-limits or cools down the alert.
- Sends the webhook off the main thread, with a timeout and no `.Result` or `.Wait()`.

### s3: a "master keycard" tracked by serial

Prompt: "Write a plugin that gives one player a special keycard that opens every door, and keeps
it special if they drop it and someone else picks it up."

Key:

- Identity is the item **serial**, not a held reference to the item object.
- The tag is cleared when the round restarts.
- The permission check compiles against real LabAPI types; it does not mix LabAPI's string
  permissions with the game's `PlayerPermissions` enum.
- Subscribes only to events whose frequency class was checked in the catalog.

### s4: anti-ESP

Prompt: "Write a plugin that stops the server from sending hidden players to clients that cannot
see them, to defeat wallhacks."

Key:

- Knows that omitting a player parks its model far away on the client, which silences footsteps,
  proximity voice and gunshot audio unless the send set includes audibility range.
- Any Harmony patch targets a method and parameter that exist (grep the LabAPI source and the
  game's public members).
- The per-pair event is hot (observers times targets, every frame); answers from a per-frame
  cache; no allocation per pair.
- Fails open: on an error or a bad map graph the layer switches off for the round.

### s5: port an EXILED fragment

Prompt: "Port this EXILED plugin fragment to LabAPI." The fragment is a handler for the
`Hurting` event that cancels damage between players on the same team, calls `ShowHint` on the
attacker, and uses `Timing.CallDelayed` to re-enable something later.

Key:

- Every LabAPI name in the answer exists (grep it); none is invented.
- Cancellation uses the LabAPI event's own `IsAllowed`, and the answer says which setters the game
  ignores for that event (the catalog's `ignoredSetters`).
- The delayed call does not capture a `Player` across a round restart.
- Differences in event timing between EXILED and LabAPI are named, not hidden.

## Test 2: process layer with a role-played owner

### Setup

The agent receives only the owner's first message (the request). It may ask the owner one
question per turn, up to 8 turns, then must deliver. The owner is a second model that answers
from the fact sheet below, **reveals a fact only when asked about it, and answers only the
question asked**. Instructions to the owner model:

> You are the owner of an SCP:SL server. Answer in short, plain sentences, in your own words. Use
> only the fact sheet. If the agent asks something the sheet does not cover, say you are not sure.
> Never volunteer a fact nobody asked about. If the agent says it is ready to write code, say
> "ok, go ahead".

### Hidden fact sheet (generic)

| # | Fact |
|---|---|
| F1 | About 60 players at peak |
| F2 | Two servers on 7777 and 7778 that share totals |
| F3 | EXILED 9, with a custom-role plugin and a custom-item plugin installed |
| F4 | An info plugin re-sends everyone's hints on every spawn and role change |
| F5 | An existing activity HUD sits in the top-right corner |
| F6 | A web panel with `POST /api/stats`; no files |
| F7 | Staff groups (owner, admin, moderator) must be excluded from anything that rewards or ranks |
| F8 | A map plugin with teleporters moves players instantly |
| F9 | No automatic bans: detections are reported to staff only |
| F10 | The servers sit empty at night and rounds run about 15 minutes |
| F11 | A local dedicated server exists at the default install path; 7777 and 7778 are in use; 9999 is free |

Which facts a good agent should discover depends on the request. The grader counts the facts that
bear on the scenario (for p1, F1 to F8).

### Requests

- **p1**: "I want a plugin that shows players their kill stats in a corner of the screen."
- **p2**: "I want to reward players for being active."
- **p3**: "I need something that detects speedhackers."
- **p4**: "Log a line when a player joins." (a small change; the owner's plugin exists and can be read)

### Grading

Score 0 to 10 on:

1. **Facts discovered**, counted against the sheet.
2. **Design approved before code.** The agent shows a design and waits for a yes before it writes
   code. A shortcut the owner offers after approval ("just write it") is legitimate.
3. **Proportionality.** A real feature gets questions and a design. A small change gets neither:
   one change, in the plugin's own style, no unrequested filters, no list of variants.
4. **Engine mistakes** in what it finally writes or specifies (the s1 to s5 keys apply).

Record the number of questions, how many were irrelevant, and whether the agent asked about the
test server instead of assuming one.

### What to expect from a weak library

If the library makes the agent over-ask, p2 shows it first (14 questions in the original first
GREEN run, 3 irrelevant). If it adds ceremony to small work, p4 shows it (7 in the second GREEN run
against 9 without the library). Both are in [RESULTS.md](RESULTS.md), together with the patches
they led to.
