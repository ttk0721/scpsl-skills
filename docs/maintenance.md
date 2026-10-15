# Maintenance: re-verifying after a game update

Everything in this repository is verified against one build: LabAPI 1.1.7 and SCP:SL 14.2.7. A
game or LabAPI update makes every claim stale until it is re-checked. This guide is the order of
work. The detailed procedure, with commands, is in
`skills/researching-scpsl-internals/reverifying-a-build.md`; this file says what to do around it.

## The private-research rule

Re-verifying means reading decompiled game code. Do that in a directory that is not in this
repository, and never copy from it:

- Keep decompiled trees and research notes outside the repository, or in the git-ignored
  directories the `.gitignore` names (it also ignores `*.dll` and `*.pdb`).
- Notes, skills and examples describe behaviour and name public types and members. The event
  catalog's `raisedFrom` column is the one place that names non-public game methods, as the
  location of a raise. Nothing quotes decompiled code, decompiled paths or line numbers (SCP:SL
  EULA section 12).
- LabAPI source is public (LGPL-3.0): read it freely and cite names from it.
- Optional: a `.private-words` file at the repository root (one case-insensitive regex per line,
  `#` comments; never committed) makes `check-publishable.py` flag words that must not appear, such
  as names and local paths.
- `check-publishable.py` is the guard, with four checks (fingerprints, artefacts, verbatim overlap
  with `--corpus`, private words). Retyping or reformatting a decompiled fragment does not get past
  it: the artefact detector flags decompiler-style locals, statement-shaped code in prose or JSON
  strings and private underscore fields, and the corpus check compares token windows, not lines.
  `tools/check-publishable.reviewed` lists the few corpus matches that were read and judged not
  copied (a standard BCL idiom, a public API signature), each with a reason; a stale entry is a
  finding, so the list cannot rot. Add to it only after reading the match, never to silence a
  result. The `check-publishable: ignore-file <reason>` marker works only for test fixtures under
  `tools/scpsl-lint/tests/fixtures`, only for the fingerprint and artefact checks, and only with a
  reason.

## Order of work

1. **Pin the new build.** Note the game version, the LabAPI version and the build id. Update the
   LabAPI source tree to the matching tag. Compare the `Managed/LabApi.dll` of the server with the
   `Northwood.LabAPI` package of the same version (they were byte-identical for 1.1.7).
2. **Mechanical diff.** Follow section 1 of `reverifying-a-build.md`: decompile into a new tree
   with the same decompiler version, `diff -rq`, then the event-focused diff with
   `skills/researching-scpsl-internals/scripts/event-sites.py`. Everything that did not change
   keeps its verdict.
3. **Re-check by risk, in the order of section 2 of that file.** Hot events, cancellable events
   and ignored setters, the `Server.Tps` formula, the tickrate override and the PlayerLoop, loader
   folders and order. The fragile facts come first because a wrong one spreads furthest.
4. **Re-read the changed call sites only.** Where a raise site moved, read the enclosing method and
   its caller again. Do not trust the old frequency class.
5. **Update the catalog** (below).
6. **Run each affected skill's own "Re-verifying on a new build" list.** Every reference file ends
   with one (`grep -rn "Re-verifying" skills` finds them). The `SKILL.md` files themselves carry
   only the version line.
7. **Re-measure what is measured.** `tools/sl-probe` for frame time, `tools/hot-bench` for the
   cost of a hot-event subscription, on a separate test instance (running-a-test-server: never the
   owner's ports, private check first). The hot-event numbers live in one place,
   `tools/hot-bench/README.md`; the README and the skills point there instead of repeating them,
   so update that file and check that no other file has grown a copy (`grep -rn "raises/s" .`).
   The frame-cost figure for dummies (`tools/sl-probe`, measured on a live test server) is quoted in a
   few places and is tied to 14.2.7. All of these numbers come from one machine with dummies and no
   real players.
8. **Stamp the version** in the "Verified against" lines and in the README only after the checks
   above were actually run. Mark anything you could not re-check as UNVERIFIED instead. Keep the
   `version` in `.claude-plugin/plugin.json` and `.claude-plugin/marketplace.json` equal and bump
   both when a release changes behaviour (`tests/hooks/test_manifests.py` fails if they differ).

## The event catalog

`tools/catalog/labapi-events.json` has one row per LabAPI event: where the game raises it, its
frequency class, cancellation, ignored setters, confidence and whether a second reader re-derived
it (`independentlyVerified`). Three flags are derived, never set by hand:

- `hot`: the frequency class is `per-frame`, `per-frame-per-pair` or `high-rate-network`.
- `burst`: raised once per shot, hit, hitmarker or damage application (15 events on 14.2.7, the rows
  marked `burst: true`; the definition is in the catalog's `meta`).
- `conditionallyHot`: the row has `hotConditions`, situations that raise it per frame.

The other fields that tools read:

- `ignoredSetters`: setters the game ignores **always**. `scpsl-lint` SL003 warns on an assignment
  to one.
- `conditionallyIgnoredSetters`: `[{setter, when}]`, setters ignored only in the situation `when`
  describes (another handler object, one channel, one call site). SL003 prints an info note for
  these, not a warning, and the maintainer must read `when` before relying on the setter. A setter
  is in one list or the other, never both, and every name in either list is also in `settable`.
  On 14.2.7: 23 events have an always-ignored setter and 11 a conditional one.
- `pair`: `null` or `{event, role}`, where `role` is `before` for the `-ing` event raised ahead of the
  action and `after` for the `-ed` event raised once it happened. It is symmetric: if A has
  `{B, before}` then B has `{A, after}`. `null` when there is no single partner (Joined/Left,
  intermediate stages).
- `frequency: never-fired`: LabAPI declares the event, but nothing calls its dispatcher: no call site
  in the game build and none in LabAPI itself, so subscribing does nothing (4 events on 14.2.7).
  A declaration that LabAPI has commented out is not an event and has no row.
- `independentlyVerified`: a second reader re-derived the row (240 of 377 on 14.2.7).

`skills/labapi-events/events-catalog.md` and `tools/SlKit/src/Runtime/HotEventAudit.HotList.cs` are
generated from the catalog. **The generator is not in this repository.** It reads the maintainer's
private research notes, which quote decompiled code, so it is not published, and nothing here can
rebuild the JSON from scratch. A fork without that input edits the JSON, then updates the markdown
rows and the embedded hot list by hand (the `jq` command in the header comment of
`HotEventAudit.HotList.cs` regenerates the hot list from the JSON). What the repository does check:
`tests/hooks/test_docs_counts.py` compares the catalog's own `meta` counts with its rows, checks the
symmetry of `pair` and that every ignored setter is in `settable`, and fails when the README quotes
a number (event count, verification counts, flag counts, lint targets, project count) that no
longer matches; the lint tests check the embedded hot list against the catalog; and
`python3 skills/researching-scpsl-internals/scripts/event-sites.py --labapi <LabApi> --game <tree>
--catalog tools/catalog/labapi-events.json` compares the catalog with LabAPI's declarations and the
game's raise sites (see `reverifying-a-build.md`). After any change, run the lint tests: the linter
reads the catalog at runtime.

## Checks to run before a release

```
python3 tools/check-skills.py
python3 tools/check-publishable.py --require-corpus --corpus <decompiled tree outside the repo>
python3 examples/build-all.py
python3 -m unittest discover -s tests/hooks
```

`check-skills.py` needs PyYAML and exits 2 without it (no weaker fallback: a regex check accepts
frontmatter that Claude Code rejects). Besides frontmatter, version line and size limits it checks
every relative link and `#anchor` in the Markdown under `skills/`, `docs/` and the README, so a
renamed heading or a moved file shows up as a broken link, not as a silent dead end.

`check-publishable.py` without `--corpus` prints "no findings, but WEAK CHECK": it then verified
only fingerprints, artefacts and private words, not verbatim copying. Use `--require-corpus` in
automation so a missing corpus is an error (exit 2). Run it against the same decompiled tree the
research used, and read any new match before touching `check-publishable.reviewed`.
`build-all.py` needs the server's `Managed/` folder (`SL_REFERENCES`, see
`deploying-scpsl-plugins/build-and-references.md`); it builds 33 projects, runs the test runners and
lints five targets (`examples/`, `tools/SlKit`, `tools/sl-probe`, `tools/hot-bench`,
`tools/tickrate-fix`). A clean lint of `examples/` means 0 errors; the 4 SL001 warnings there (and
the 3 in `tools/hot-bench`) are deliberate subscriptions, so a new warning is a change to look at,
not noise.

## What is not automated

- Whether a skill's prose is still true is a human or agent re-read; nothing flags a stale
  sentence.
- The skill-level tests (`tests/RESULTS.md`) are a method run by hand with a language model as
  agent and grader; they have not been repeated after the last patches. Re-grade a new run with
  the public keys in `tests/answer-keys.md`, and re-check its rows after a game update.

What is automated: the `SKILL.md` word limits ([design.md](design.md)) are enforced by
`tools/check-skills.py`, and `python3 tools/check-skills.py --sizes` prints the current numbers.
The plugin and marketplace versions are checked to agree by `tests/hooks/test_manifests.py`.
