#!/usr/bin/env python3
"""Cross-check LabAPI event declarations, the event catalog and the game's raise sites.

Stdlib only. Read-only. Run it against a LOCAL decompile of the game (never committed).

  event-sites.py --labapi <LabApi source dir> --game <decompiled Assembly-CSharp dir> \
                 [--catalog tools/catalog/labapi-events.json] [--dump]

Default output: drift report.
  - duplicate catalog rows
  - declared in LabAPI but missing from the catalog, and the reverse
  - events with no raise site in the game tree that the catalog does not call never-fired
  - events the catalog calls never-fired that now have a raise site
  - events whose number of raise sites differs from len(raisedFrom) in the catalog
With --old <older decompiled tree> (and --game as the NEWER tree): compare two builds. Prints
events whose raise-site files appeared or disappeared, and events raised from a file whose
content changed (those are the call sites to re-read first). Use the same ilspycmd version for
both decompiles, otherwise decompiler noise swamps the signal.
With --dump: one "Category.Event<TAB>relative/file.cs" line per raise site (sorted, no line
numbers), so two builds can be compared with a plain `diff`.

A raise site is a call "<Category>Events.On<Name>(" anywhere in the game tree. Events that
LabAPI raises itself (loader, plugin lifecycle) have no game site and show up as "no site";
check those by hand.
"""
import argparse
import collections
import filecmp
import json
import os
import re
import sys

DECL = re.compile(r"public\s+static\s+event\s+LabEventHandler(?:<\w+>)?\??\s+(\w+)\s*;")
CALL = re.compile(r"\b(\w+Events)\.On(\w+)\(")


def declared(labapi):
    out = set()
    root = os.path.join(labapi, "Events", "Handlers") if os.path.isdir(os.path.join(labapi, "Events")) else labapi
    for dp, _, fns in os.walk(root):
        for f in fns:
            if not f.endswith(".EventHandlers.cs"):
                continue
            cat = f[: -len(".EventHandlers.cs")]
            with open(os.path.join(dp, f), errors="ignore") as fh:
                for line in fh:
                    # LabAPI keeps retired events as commented-out declarations
                    # (14.2.7: PlayerEvents.AimingWeapon). They are not events: nothing can
                    # subscribe, so counting them reported a phantom "declared, NOT in catalog".
                    if line.lstrip().startswith("//"):
                        continue
                    m = DECL.search(line)
                    if m:
                        out.add(f"{cat}.{m.group(1)}")
    return out


def sites(game):
    out = collections.defaultdict(list)
    for dp, _, fns in os.walk(game):
        for f in fns:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(dp, f)
            rel = os.path.relpath(p, game)
            with open(p, errors="ignore") as fh:
                for i, line in enumerate(fh, 1):
                    for m in CALL.finditer(line):
                        out[f"{m.group(1)}.{m.group(2)}"].append((rel, i))
    return out


def compare(old, new, st_old, st_new):
    files = collections.defaultdict(set)
    for ev, lst in st_new.items():
        for rel, _ in lst:
            files[rel].add(ev)
    for ev, lst in st_old.items():
        for rel, _ in lst:
            files[rel].add(ev)
    f_old = {ev: {r for r, _ in lst} for ev, lst in st_old.items()}
    f_new = {ev: {r for r, _ in lst} for ev, lst in st_new.items()}
    print("## raise sites that appeared or disappeared")
    n = 0
    for ev in sorted(set(f_old) | set(f_new)):
        a, b = f_old.get(ev, set()), f_new.get(ev, set())
        for r in sorted(b - a):
            print(f"  + {ev}  {r}")
            n += 1
        for r in sorted(a - b):
            print(f"  - {ev}  {r}")
            n += 1
    if not n:
        print("  (none)")
    print()
    print("## files that raise events and whose content changed")
    n = 0
    for rel in sorted(files):
        po, pn = os.path.join(old, rel), os.path.join(new, rel)
        if os.path.exists(po) and os.path.exists(pn) and not filecmp.cmp(po, pn, shallow=False):
            print(f"  {rel}: {', '.join(sorted(files[rel]))}")
            n += 1
    if not n:
        print("  (none)")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--labapi", required=True)
    ap.add_argument("--game", required=True)
    ap.add_argument("--catalog")
    ap.add_argument("--dump", action="store_true")
    ap.add_argument("--old", help="older decompiled tree; compare it with --game")
    a = ap.parse_args()

    dec = declared(a.labapi)
    st = sites(a.game)

    if a.old:
        return compare(a.old, a.game, sites(a.old), st)

    if a.dump:
        for ev in sorted(dec):
            for rel in sorted({r for r, _ in st.get(ev, [])}):
                print(f"{ev}\t{rel}")
        return 0

    print(f"declared in LabAPI: {len(dec)}")
    print(f"events with a raise site in the game tree: {sum(1 for e in dec if st.get(e))}")
    if not a.catalog:
        return 0

    rows = json.load(open(a.catalog))["events"]
    cat = {e["event"]: e for e in rows}
    print(f"in catalog: {len(cat)} distinct, {len(rows)} rows")
    for label, items in (
        ("duplicate catalog rows",
         sorted(k for k, v in collections.Counter(e["event"] for e in rows).items() if v > 1)),
        ("declared, NOT in catalog", sorted(dec - set(cat))),
        ("in catalog, NOT declared", sorted(set(cat) - dec)),
        ("no site, catalog frequency is not never-fired",
         sorted(e for e in dec if e in cat and not st.get(e) and cat[e]["frequency"] != "never-fired")),
        ("catalog says never-fired, but a site exists",
         sorted(e for e in dec if e in cat and st.get(e) and cat[e]["frequency"] == "never-fired")),
        ("site count differs from len(raisedFrom)",
         sorted(f"{e} sites={len(st[e])} catalog={len(cat[e]['raisedFrom'])}"
                for e in dec if e in cat and st.get(e) and len(st[e]) != len(cat[e]["raisedFrom"]))),
    ):
        print(f"\n## {label} ({len(items)})")
        for i in items:
            print("  " + i)
    return 0


if __name__ == "__main__":
    sys.exit(main())
