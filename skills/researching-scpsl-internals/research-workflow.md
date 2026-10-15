# Research workflow and prompt templates

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Part of researching-scpsl-internals. Placeholders are in `{{double braces}}`; there are no machine paths. The prompts work as scripted multi-agent workflows (one skeptic per note, per catalog chunk and per skill) or for one person with one agent at a time.

## The pipeline

| Stage | Who | Output | Model tier |
|---|---|---|---|
| 0. Split | You | Subsystems, each with a scope (files, namespaces) and a question list | none |
| 1. Analyse | Analyst (A) per subsystem | Private note, every claim cites a file and line it opened, the rest marked UNVERIFIED | cheaper by default, strongest for rule-grade topics |
| 2. Refute | Skeptic (B), independent, per note | Verdict per rule; note corrected in place | cheaper |
| 3. Catalog | Catalog builder (C) per chunk + a skeptic | Machine-readable rows | cheaper |
| 4. Baseline | Closed-book solver on realistic tasks | What an agent does without the skills | cheaper (same model as stage 7 on purpose) |
| 5. Grade | Baseline grader (F) | Failure list against the verified notes | cheaper |
| 6. Author | Skill author (D) per skill | `SKILL.md` plus reference files, claims with sources | strongest for judgment-heavy skills, else cheaper |
| 7. Verify | Skill verifier (E) per skill | Fixed skill, format and publishability checked | cheaper |
| 8. With-skill run | Same tasks as stage 4, library available | Attempt | same model as stage 4 |
| 9. Grade + patch | Grader (F) then one patcher per skill that still let a mistake through | Patched skills | cheaper |
| 10. Publish check | You | `check-publishable.py` clean, private notes outside the repo | none |

Private notes (they cite decompiled code) never enter the public repository. Skills publish descriptions only.

**Shape.** Run stages 1-2 per subsystem as a pipeline (skeptic starts when its analyst ends); use a barrier only where a consumer needs everything (stage 9 patchers need the failures from all scenarios). Stages 4-5 can run before any skill exists; do them first so each skill is written against real mistakes, test-first.

## Cost discipline

1. **Pick the model per stage and write one sentence of argument per use** next to the call: `// strongest: <why a wrong conclusion here becomes a rule>` or `// cheaper: <why this is extraction or checking>`. Strongest tier only where a wrong conclusion would become a rule other plugins inherit: threading and main-loop rules, event invocation/cancellation semantics, design-judgment skills (design, review, cross-cutting synthesis). Cheaper tier (Sonnet class, Haiku for pure listing, counting, JSON validation) for call-site extraction, catalog rows, fact-checking a claim against a cited line, format and publishability checks.
2. **Escalate on disagreement.** If a cheaper skeptic overturns or weakens a rule written by the strongest tier, spend one strongest-tier adjudication call on that rule instead of re-running everything.
3. **Re-use before re-deriving.** Tell every analyst which existing notes to grep first. Prior numbers are estimates and prior citations are leads to re-open, not facts.
4. **Hand over files, not "explore".** Give the exact directories and a question list. An analyst that has to find its own scope reads three times as much.
5. **Output to a file plus a structured summary.** The orchestrator carries the summary, not the note. Ask for JSON that parses (and validate it with one command).
6. **One plain shell command per call.** No loops, shell variables, `cd` chains or git inside agent calls; they trip sandbox guards and make failures ambiguous. Use `grep -rn`, `sed -n 'A,Bp'`, `ls`, `wc`.
7. **Grep, then read the lines you need.** `-l`/`-c` first, one directory at a time, `-m N`; whole-file reads only for the five files that matter (grep-recipes.md). Decompile one method or one type, never print a whole assembly (sources-and-decompiling.md).
8. **Do deltas, not waves.** After an update, only re-research what the mechanical diff flagged (reverifying-a-build.md).
9. **Do not pay twice for the baseline.** Keep the closed-book attempts and their grades; re-run only scenarios whose skills changed.

## Common preamble (put at the top of every agent prompt)

```
You are analysing SCP: Secret Laboratory server internals to build an accurate knowledge base
for plugin authors (LabAPI {{labapi_version}}, game {{game_version}}: the versions installed here).

SOURCES (read-only):
- LabAPI source (LGPL-3.0): {{labapi_src}}
- Decompiled game code (proprietary): {{decompiled_game}}   also {{decompiled_mirror}}
- Installed binaries for anything not decompiled: {{managed_dir}}
  Decompile one method:  ilspycmd --disable-updatecheck -m "M:Ns.Type.Method(Args)" -o {{scratch}} <dll>
  Decompile one type:    ilspycmd --disable-updatecheck -t Ns.Type -o {{scratch}} <dll>
- Existing notes on this area (grep them BEFORE re-deriving; treat numbers as estimates and
  citations as leads you re-open): {{notes_dir}}

RULES
1. Evidence or it did not happen. Every non-trivial claim cites <path>:<line> that you actually
   opened. Anything inferred but not confirmed is labelled UNVERIFIED.
2. Read the actual code. Do not rely on memory of older SCP:SL, NWAPI or EXILED versions.
3. Shell: ONE plain command per call. No loops, no variables, no "cd x && ...", no git.
   grep -rn, sed -n 'a,bp', ls, wc are fine. Count (-c, -l) before printing lines.
4. Never modify anything outside the output file(s) you are told to write.
5. English. Dense: facts, mechanisms, costs, pitfalls. No filler.
```

## A. Analyst

```
{{common_preamble}}

TOPIC: {{title}}
{{scope}}   (exact directories and files; the question list below is what the note must answer)
QUESTIONS:
{{numbered_questions}}

Write the note as Markdown to {{output_file}} with exactly these sections:
# {{title}}
> LabAPI {{labapi_version}} · SCP:SL {{game_version}} · PRIVATE research note (cites decompiled code, never publish verbatim)
## TL;DR            (max 10 bullets: what a plugin author must know)
## Mechanics        (how it works, with path:line citations)
## Plugin-facing API (what to call / subscribe / override, with signatures)
## Cost model       (main-thread work, allocations, wire traffic, how often it runs)
## Threading        (what may be touched off the main thread, with evidence)
## Pitfalls         (symptom -> cause -> fix, with citation)
## Rules for plugin authors  (numbered R1, R2...; each with its evidence citation)
## Unverified / open questions
Return JSON: {"file": str, "tldr": [str], "rules": [{"id","rule","evidence"}], "openQuestions": [str]}.
The returned rules are the numbered rules in the note, verbatim.
```

Split topics so one analyst can finish: loader and lifecycle; event system core; each wrapper family; main loop and threading; hints and settings; commands; roles and damage; networking; voice; Harmony; porting. Give the questions a precise form ("verify that X is absent", "which of these setters send RPCs").

## B. Skeptic (independent)

```
{{common_preamble}}

You are an independent SKEPTIC. Another analyst wrote {{note_file}} ("{{title}}"). Its rules will
become guidance for every plugin author who uses our skills, so a wrong rule spreads. Try to
REFUTE them.

Rules to check:
{{rules_with_evidence}}

For each rule: open the cited evidence AND the surrounding code and callers; look for a
counter-example or a misread. Verdict: "confirmed" (evidence shows it), "refuted" (wrong), or
"nuanced" (true only under conditions: state them). If the cited evidence does not support the
claim it is "refuted" even if the claim is true elsewhere, unless you find the real evidence;
then "confirmed" with the corrected citation. Also skim TL;DR and Pitfalls and check the worst 3
other claims.

Then EDIT {{note_file}} in place: correct every refuted or nuanced statement wherever it
appears (TL;DR, body, Rules) and append "## Verification (independent check)" with one line per
rule: id, verdict, one-sentence reason with citation. Do not delete correct content.
Return {"verdicts":[{"id","verdict","why"}],"otherFixes":[str]}.
```

The analyst must not see the skeptic's prompt, and the skeptic must not be the analyst's session. Ignore agreement; read the `refuted` and `nuanced` lines. This stage is not optional: skeptic checks regularly come back nuanced or refuted.

## C. Catalog builder (one chunk of events)

```
{{common_preamble}}

TASK: build an exact catalog entry for each of these LabAPI events: {{event_list}}
For EACH event:
1. Find its declaration in {{labapi_src}}/Events/Handlers/<Category>Events.EventHandlers.cs and its
   args class in {{labapi_src}}/Events/Arguments/. Note whether the args implement the cancellable
   interface and which properties have public setters.
2. Find EVERY place the game raises it: grep -rn "<Category>Events.On<Name>(" {{decompiled_game}}
   (also {{decompiled_mirror}} if nothing is found). Record the enclosing method.
3. Decide how often it fires by reading the enclosing method and its callers (Update, LateUpdate,
   FixedUpdate, MEC loop, Mirror handler, per observer-target pair, per action). Class, one of:
   per-frame-per-pair | per-frame | high-rate-network | per-action | per-lifecycle | per-round |
   once | on-request | never-fired | unknown.
4. For cancellable events and settable properties: check whether the game code AFTER the call
   reads IsAllowed or the mutated fields (cite the consuming line). A setter the game never reads
   back goes in "ignoredSetters".
5. Gotchas: Player may be null or the host; fires for dummies; fires before or after state is
   applied; fires several times; ordering against the -ing/-ed counterpart.

Write a JSON array (valid JSON, nothing else) to {{output_file}}, one object per event:
{"event":"PlayerEvents.Hurting","args":"PlayerHurtingEventArgs","cancellable":true,
 "settable":["Damage"],"ignoredSetters":[],"raisedFrom":["PlayerStats.DealDamage"],
 "frequency":"per-action","frequencyDetail":null,"hot":false,"hotConditions":null,
 "pair":"PlayerEvents.Hurt","consumption":"returns when !IsAllowed","notes":"...",
 "confidence":"high","independentlyVerified":false}
"hot" = true when frequency is per-frame, per-frame-per-pair or high-rate-network, or when a
plausible 60-player server raises it more than about 100 times per second in total.
Validate: python3 -c "import json;print(len(json.load(open('{{output_file}}'))))" and fix until it parses.
Return {"file","eventCount","hotEvents":[...],"notFired":[...],"notes"}.
```

Catalog skeptic: same shape as B, but for rows: re-derive every `hot` row's call sites and frequency independently; check at least 12 others, prioritising cancellable rows, `unknown`, `never-fired` and rows whose frequency looks too low for their call site; add missing events; set `independentlyVerified` on rows checked. In the published catalog, `raisedFrom` names methods, never files and lines.

After merging chunks run `scripts/event-sites.py` (reverifying-a-build.md): chunk boundaries are where duplicate or missing rows appear.

## D. Skill author

```
You are writing ONE skill for "{{library_name}}", an open-source library that teaches an AI agent
(and humans) to write correct, cheap SCP: Secret Laboratory plugins on LabAPI. Shape: superpowers.

SKILL: {{skill_name}}   (kind: process | technique | reference | discipline)
FOCUS: {{focus_paragraph}}
SOURCES: verified private notes {{note_files}}. Each ends with a Verification section; where it
corrects the body, the correction wins. Read the relevant parts fully before writing.

REAL MISTAKES an agent made without this library (from graded closed-book runs). Your Common
Mistakes and rules must prevent these:
{{assigned_failures}}
Other mistakes seen (another skill owns them; cross-reference, do not duplicate):
{{other_failures}}
Positive patterns the baseline got right (keep them): {{good_choices}}

FORMAT
- {{skills_dir}}/{{skill_name}}/SKILL.md with YAML frontmatter: name (letters, digits, hyphens)
  and description. The description starts with "Use when", third person, ONLY triggering
  conditions and symptoms, NEVER a summary of the steps; under 500 characters.
- Body: Overview (core principle), When to Use (symptoms; when NOT to), core content (quick
  reference table, rules, short ORIGINAL C# examples), Common Mistakes built from the real
  baseline failures (wrong move, right move), Red Flags for discipline skills.
- SKILL.md lean (about 500 words process/discipline, 700 reference); tables and long lists go in
  sibling files linked by filename. One line near the top: "Verified against LabAPI {{labapi_version}}
  / SCP:SL {{game_version}} ({{date}})." Reference files end with "## Re-verifying on a new build".
- Cross-reference other skills by name only. Mark estimates as estimates and anything the notes
  call UNVERIFIED as unverified. English, dense, no filler.

PUBLISHABILITY (the repository is public and MIT; the SCP:SL EULA forbids distributing
non-public Northwood data): never copy decompiled code; describe behaviour in your own words
and name public types and members. No decompiled file paths or line numbers, no local machine
paths, no references to the private notes. LabAPI is public: its files may be named and tiny
signatures quoted. Original example code is fine.

Return {"files":[...],"skillMdWords":n,"claims":[{"claim","source"}]}: the 15 to 30 most
important factual claims, each with its source (note and rule id), for the verifier.
```

## E. Skill verifier

```
You are an independent SKEPTIC verifying the skill {{skill_dir}} (files: {{files}}) before release.
1. FACTS. For each claim below open the cited source (verified notes; their Verification sections
   override the body) and confirm, correct or delete it. Then read the skill files fully and check
   every other doubtful statement (API names, event names, semantics, numbers). Be strict: wrong
   guidance spreads to every plugin built from the library.
   Claims: {{numbered_claims}}
2. FORMAT. {{format_rules}}
3. PUBLISHABILITY. {{publish_rules}}  Then run
   python3 {{repo}}/tools/check-publishable.py --corpus {{decompiled_game}}   and fix anything it
   reports inside this skill's directory.
Fix everything in place (edit this skill's files only).
Return {"checked":n,"wrong":[...],"fixed":[...],"formatIssues":[...],"publishIssues":[...],"skillMdWords":n}.
```

## F. Baseline run and baseline grader

Baseline solver (closed book, no library, no notes): "You are an experienced C# developer. Write {{realistic_task}} for SCP:SL with LabAPI {{labapi_version}} on .NET Framework 4.8. Deliver a short design, the complete code, your assumptions. Write one Markdown document to {{baseline_file}}." Choose tasks that touch several skills at once (a HUD tracker, an alert, a special item, an anti-cheat, an EXILED port). The with-skill run uses the identical task plus: "Start by reading {{repo}}/skills/using-scpsl-skills/SKILL.md and every skill it points to that is relevant. Read nothing else on this machine."

Grader:

```
You grade an attempt at an SCP:SL plugin against VERIFIED research notes (LabAPI {{labapi_version}}).
Attempt: {{attempt_file}}   [with-skill grading also: baseline attempt {{baseline_file}} and grade]
Verified notes (ground truth; their Verification sections override): {{notes}}
Find every concrete mistake: invented or wrong API names, wrong semantics (which event fires
when, cancellation, ignored setters), performance traps at 60 players, threading errors,
lifecycle errors (a restart reloads the scene; caching Player across rounds), networking errors,
deployment errors. For each: what it did, what is true, evidence (note + rule id), severity
(critical = breaks server or feature; major = wrong behaviour or significant cost; minor), and the
skill that should have prevented it. With-skill grading adds: did the attempt say it read that
skill, and does the skill cover it (yes-but-ignored | yes-but-unclear | no | wrong-in-skill).
List good choices to keep. Score 0 to 10 for "correct and cheap on a 60-player server".
Write a readable report to {{grade_file}}, then return the structured result.
```

Patch loop: group failures by skill across all scenarios; for each skill with a non-minor miss run one patcher ("decide why the skill failed: missing, unclear, buried or wrong; fix it; confirm every added fact against the notes; if it belongs to another skill or the notes do not support a fix, skip it and say why"). `wrong-in-skill` outranks everything.

## Verifying the method itself

- Count how many baseline mistakes each skill now prevents; a skill that prevents none is documentation, not a skill.
- Keep the grades with the repository so a later maintainer can see which rule exists because of which failure.

## Re-verifying on a new build

- Re-read each template against the current notes: if a note section was renamed or a schema field added, update template and example together.
- After an update run stage 1 only for subsystems the file diff touched (reverifying-a-build.md), stage 2 for every changed rule, stage 7 for every skill that cites a changed rule, stage 10 always.
- Re-check the model-tier choices: if a cheaper tier made a wrong call that reached a published rule, move that stage up and write down why.
- Re-run the baseline for the scenarios whose skills changed, with the same model as before, so the comparison measures the skills and not the model.
