# Worked dialogue: "kill stats in the corner of the screen" (brainstorming-scpsl-features)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Compressed. Each agent turn is one message. The bracket says which design choice the answer flips.

**Owner:** I want a plugin that shows players their kill stats in a corner of the screen.

**Agent:** (empty repo, so path C) This is a new plugin, so I'll ask a few questions, propose
approaches with their server cost, and write a short spec before any code. First: what is
installed? (a) LabAPI only (b) EXILED 9 too (c) not sure: give me the server folder path and I'll
read the plugin list. *[plugin type, event layer]*

**Owner:** EXILED 9, plus UCR and UCI.

**Agent:** Does anything already put text on players' screens? (a) nothing (b) a HUD plugin: which
corner? (c) a plugin that refreshes hints on spawn or role change (d) not sure. *[own hint sender vs
shared owner vs on-demand]*

**Owner:** An activity HUD plugin sits top right. An info plugin refreshes everyone's hints on spawn.

**Agent:** Peak players? (a) under 40 (b) 40-60 (c) more. *[every cost line multiplies by it]*

**Owner:** About 60.

**Agent:** How many servers? (a) one (b) several, separate stats (c) several, one shared total.
*[server id, two sessions at once]*

**Owner:** 7777 and 7778, shared.

**Agent:** Must the totals survive server restarts, and where? (a) per round only (b) a file per
server (c) an existing panel or database: send me its API. *[IO, failure policy]*

**Owner:** Panel, `POST /api/stats`. No files.

**Agent:** Do kills by UCR custom roles count, and which never count? (a) UCR counts, teamkills and
suicides don't (b) something else. *[kill rule under custom teams]*

**Owner:** (a).

**Agent:** Staff on duty: counted like everyone, or excluded from stats? (a) excluded (b) counted.
*[business rule: the owner's call, not mine]*

**Owner:** Excluded when they are on duty, via the `staff` group.

**Agent:** Should players be able to hide it? (a) no (b) `.stats` in the player console (c) a toggle
in the Settings tab. *[SSS registry or a command]*

**Owner:** Staff asked for a toggle. Settings tab is fine.

**Agent:** Last one: do you have a dedicated server locally, and do you want me to test on it?
(a) yes (b) no, give me a checklist. *[test plan]*

**Owner:** Yes, but 7777 and 7778 are live.

**Agent:** Then I won't touch them. I'd create our own instance on 9999, kept off the public list
(no `contact_email`, and I check the boot log says so). OK, or do you prefer a different port?

**Owner:** 9999 is fine.

**Agent:** Approaches, recommended first:

1. **Own hint sender, top left, send on change plus 2 s keep-alive, staggered.** Cost:
   `Death` (per-lifecycle), 0 per-frame, one clock job, 60 / 2 s = 30 hints/s, about 3 KB/s (est., 80 B of text plus about 20 B overhead).
   Risk: the info plugin's spawn refresh will replace it (one hint slot per player, verified), so
   test on 9999 how long the HUD stays covered. Placement scales with the window height; at other aspect ratios only the area left of the container moves (scpsl-hints-and-hud).
2. **One shared HUD owner:** The activity HUD and the info plugin hand text to a compositor. No
   conflicts, but it changes plugins you may not control.
3. **On demand:** `.stats` plus a round-end summary hint. Near zero cost, no conflict, no
   always-on HUD.

Data for all three: per `UserId` (dummies and on-duty staff excluded), kills and deaths as increments, best streak as a
maximum; one batched POST every 60 s and at round end, off the main thread, server id in the body,
the same idempotency key on retries; synchronous flush on `ServerEvents.Shutdown`. Panel down: keep
pending (deferred), retry with a bound, the HUD keeps showing local numbers.

I recommend 1 with a kill switch, falling back to 3 if the conflict shows. Approve, and I'll write
the spec?

Not asked, on purpose: font, colours, exact wording, permission names. None of them changes the
design; they become config with defaults.

## Re-verifying on a new build

- The hint cost (about 20 + text bytes per recipient) and the keep-alive policy: scpsl-hints-and-hud.
- `Death` frequency class: the labapi-events catalog.
- Shutdown flush timing and the missing `Disable()` call: scpsl-game-lifecycle.
