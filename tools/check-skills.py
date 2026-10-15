#!/usr/bin/env python3
"""Validates every skill's frontmatter, links and size.

Checks per skills/<dir>/SKILL.md:
  - frontmatter present and valid YAML (parsed with PyYAML; the check REFUSES TO RUN without it, because
    a hand-rolled fallback silently accepts what Claude Code rejects); `name` equals the directory name
    and uses only [a-z0-9-];
  - `description` starts with "Use when" and is under 1024 characters;
  - the version line is present;
  - the body (frontmatter excluded) is within its word limit, see the constants below.
Checks per markdown file (every .md under skills/, docs/ and the root README.md):
  - every relative link and image target exists (files and directories; http(s)/mailto are skipped, so
    are links inside code fences and inline code);
  - every #anchor, same-file or into another .md, matches a heading of that file under GitHub's slug
    rules (lower-case, punctuation dropped, spaces to hyphens, -1/-2 for duplicates) or an explicit
    <a name=...>/id=... anchor.
Exit code 0 = all good, 1 = problems, 2 = the check cannot run (PyYAML missing).

Usage: check-skills.py [skills_dir] [--root REPO_ROOT] [--sizes]
  --root   also check REPO_ROOT/docs and REPO_ROOT/README.md (default when no skills_dir is given: the
           repository this script lives in)
  --sizes  also print the current word counts against the limits, so docs quote a command instead
           of a hand-maintained table (docs/design.md, "Size policy").

The size limits live HERE and nowhere else; docs/design.md quotes them and a test checks that the
quote and these constants agree. They are set with headroom over the current skills, so adding a
paragraph does not trip them but letting a SKILL.md grow into a reference file does. When one
trips, move the detail into a sibling .md and keep the rule, gate or red flag in SKILL.md.
"""
import argparse
import os
import re
import statistics
import sys
import unicodedata
from urllib.parse import unquote

try:
    import yaml  # PyYAML: required, see YAML_MISSING
except ImportError:
    yaml = None

YAML_MISSING = ("check-skills needs PyYAML to validate frontmatter the way Claude Code does, and it is not "
                "installed. Install it (pip install pyyaml, or your distribution's python-yaml package) and run "
                "again. There is deliberately no weaker fallback: a regex check accepts descriptions that every "
                "YAML loader rejects.")

# --- Size policy (words in the SKILL.md body, frontmatter excluded) ---------------------------------
ENTRY_SKILL = "using-scpsl-skills"
ENTRY_MAX_WORDS = 700          # the entry skill is injected into every SCP:SL session by the hook
HEAVY_SKILL = "brainstorming-scpsl-features"
HEAVY_MAX_WORDS = 900          # the heaviest process skill: gates, question list and red flags
SKILL_MAX_WORDS = 1000         # every other SKILL.md
# Sibling reference files have no limit: they load only when a skill points at them.


def word_limit(skill):
    if skill == ENTRY_SKILL:
        return ENTRY_MAX_WORDS
    if skill == HEAVY_SKILL:
        return HEAVY_MAX_WORDS
    return SKILL_MAX_WORDS


FRONTMATTER = re.compile(r"^---\n(.*?)\n---\n", re.S)


def body_words(text):
    m = FRONTMATTER.match(text)
    return len((text[m.end():] if m else text).split())


def check(skills_dir, repo_root=None):
    """Returns (problems, sizes) where sizes is a list of (skill, words, limit). Raises RuntimeError when
    PyYAML is missing. `repo_root` adds its docs/ and README.md to the link check."""
    if yaml is None:
        raise RuntimeError(YAML_MISSING)
    problems = []
    sizes = []
    for d in sorted(os.listdir(skills_dir)):
        path = os.path.join(skills_dir, d, "SKILL.md")
        if not os.path.isfile(path):
            continue
        with open(path, encoding="utf-8") as f:
            text = f.read()
        words, limit = body_words(text), word_limit(d)
        sizes.append((d, words, limit))
        if words > limit:
            problems.append(f"{d}: SKILL.md body is {words} words (limit {limit}); "
                            f"move detail into a sibling .md")
        m = FRONTMATTER.match(text)
        if not m:
            problems.append(f"{d}: missing frontmatter")
            continue
        fm = m.group(1)
        # Parse with a real YAML parser: a regex happily accepts `description: ... SCP: Secret ...`,
        # which every YAML loader (and Claude Code) rejects as "mapping values are not allowed here".
        try:
            meta = yaml.safe_load(fm)
        except yaml.YAMLError as e:
            problems.append(f"{d}: frontmatter is not valid YAML: {str(e).splitlines()[0]}")
            continue
        if not isinstance(meta, dict):
            problems.append(f"{d}: frontmatter is not a mapping")
            continue
        name_v = str(meta.get("name", "")).strip()
        desc_v = meta.get("description")
        if name_v != d:
            problems.append(f"{d}: name does not match directory")
        elif not re.fullmatch(r"[a-z0-9-]+", name_v):
            problems.append(f"{d}: name has characters outside [a-z0-9-]")
        if not desc_v:
            problems.append(f"{d}: missing description")
        else:
            dv = str(desc_v).strip()
            if not dv.startswith("Use when"):
                problems.append(f"{d}: description does not start with 'Use when'")
            if len(dv) >= 1024:
                problems.append(f"{d}: description is {len(dv)} chars (limit 1024)")
        if d != ENTRY_SKILL and "Verified against LabAPI" not in text:
            problems.append(f"{d}: missing 'Verified against LabAPI ...' line")
    problems.extend(check_links(markdown_files(skills_dir, repo_root), repo_root or skills_dir))
    return problems, sizes


# --- Links and anchors --------------------------------------------------------------------------------
FENCE = re.compile(r"^\s{0,3}(`{3,}|~{3,})")
INLINE_CODE = re.compile(r"(`+)(?:(?!\1).)+?\1")
LINK = re.compile(r"!?\[(?:[^\[\]]|\[[^\]]*\])*\]\(\s*(<[^>]*>|[^)\s]+)(?:\s+(?:\"[^\"]*\"|'[^']*'))?\s*\)")
REF_DEF = re.compile(r"^\s{0,3}\[[^\]]+\]:\s*(<[^>]*>|\S+)")
EXPLICIT_ANCHOR = re.compile(r"""<a\s[^>]*?\b(?:name|id)\s*=\s*["']([^"']+)["']|\bid\s*=\s*["']([^"']+)["']""", re.I)
ATX = re.compile(r"^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$")
SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")


def markdown_files(skills_dir, repo_root):
    found = []
    roots = [skills_dir]
    if repo_root:
        roots.append(os.path.join(repo_root, "docs"))
    for r in roots:
        for dp, dns, fns in os.walk(r):
            dns[:] = sorted(x for x in dns if x not in {".git", "node_modules", "__pycache__", "bin", "obj"})
            found.extend(os.path.join(dp, f) for f in sorted(fns) if f.endswith(".md"))
    if repo_root:
        readme = os.path.join(repo_root, "README.md")
        if os.path.isfile(readme):
            found.append(readme)
    return found


def slug(heading):
    """GitHub's heading anchor: render the inline markdown to text, lower-case it, drop everything that is
    not a letter, mark, number, connector punctuation, hyphen or space, then turn each space into a hyphen."""
    t = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", heading)          # links and images keep their text
    t = re.sub(r"<[^>]+>", "", t)                                      # inline html
    t = re.sub(r"(\*{1,3}|(?<![\w])_{1,3})(.+?)\1(?![\w])", r"\2", t)  # emphasis, not snake_case
    t = t.replace("`", "").strip().lower()
    keep = []
    for c in t:
        cat = unicodedata.category(c)
        if c in "-_ " or cat[0] in "LMN" or cat == "Pc":
            keep.append("-" if c == " " else c)
    return "".join(keep)


def toggle_fence(state, line):
    """Tracks an open code fence: returns the fence string while inside one, else None."""
    m = FENCE.match(line)
    if not m:
        return state
    if state is None:
        return m.group(1)
    return None if line.strip().startswith(state[0] * len(state)) else state


def anchors_of(text):
    """The set of valid #fragment targets of a markdown document."""
    anchors, seen = set(), {}
    fence = None
    prev = ""
    for line in text.splitlines():
        was_fenced = fence is not None
        fence = toggle_fence(fence, line)
        if was_fenced or fence is not None:
            prev = ""
            continue
        heading = None
        h = ATX.match(line)
        if h:
            heading = h.group(2)
        elif re.match(r"^\s{0,3}(?:=+|-{2,})\s*$", line) and prev.strip() and not prev.lstrip().startswith(("|", "-", "*", ">", "#", "+")):
            heading = prev.strip()
        if heading is not None:
            base = slug(heading)
            n = seen.get(base, 0)
            seen[base] = n + 1
            anchors.add(base if n == 0 else f"{base}-{n}")
        for a in EXPLICIT_ANCHOR.finditer(line):
            anchors.add(a.group(1) or a.group(2))
        prev = line
    return anchors


def link_targets(text):
    """Yields (line number, raw target) for every link, image and reference definition outside code."""
    fence = None
    for n, line in enumerate(text.splitlines(), 1):
        was_fenced = fence is not None
        fence = toggle_fence(fence, line)
        if was_fenced or fence is not None:
            continue
        plain = INLINE_CODE.sub("", line)
        for lm in LINK.finditer(plain):
            yield n, lm.group(1)
        rd = REF_DEF.match(plain)
        if rd:
            yield n, rd.group(1)


def check_links(files, base):
    """Returns problems for broken relative links and anchors in the given markdown files."""
    problems = []
    cache = {}

    def anchors_for(path):
        if path not in cache:
            with open(path, encoding="utf-8", errors="replace") as f:
                cache[path] = anchors_of(f.read())
        return cache[path]

    for path in files:
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read()
        shown = os.path.relpath(path, base)
        for line, raw in link_targets(text):
            target = raw[1:-1] if raw.startswith("<") and raw.endswith(">") else raw
            if not target or SCHEME.match(target) or target.startswith("//"):
                continue
            file_part, _, frag = target.partition("#")
            file_part, frag = unquote(file_part), unquote(frag)
            dest = path if not file_part else os.path.normpath(os.path.join(os.path.dirname(path), file_part))
            if not os.path.exists(dest):
                problems.append(f"{shown}:{line}: broken link -> {target} (no such file)")
                continue
            if not frag or not dest.endswith(".md") or not os.path.isfile(dest):
                continue
            valid = anchors_for(dest)
            if frag not in valid:
                hint = " (GitHub anchors are lower-case)" if frag.lower() in valid else ""
                where = "this file" if dest == path else os.path.relpath(dest, os.path.dirname(path))
                problems.append(f"{shown}:{line}: broken anchor -> {target} (no heading #{frag} in {where}){hint}")
    return problems


def sibling_word_counts(skills_dir):
    counts = []
    for dp, _, fns in os.walk(skills_dir):
        for fn in fns:
            if fn.endswith(".md") and fn != "SKILL.md":
                with open(os.path.join(dp, fn), encoding="utf-8", errors="replace") as f:
                    counts.append(len(f.read().split()))
    return counts


def print_sizes(skills_dir, sizes):
    print(f"Size policy: entry skill {ENTRY_SKILL} <= {ENTRY_MAX_WORDS} words, {HEAVY_SKILL} <= "
          f"{HEAVY_MAX_WORDS}, every other SKILL.md <= {SKILL_MAX_WORDS} (body, frontmatter excluded).")
    width = max((len(s) for s, _, _ in sizes), default=5)
    print(f"{'skill':<{width}}  {'words':>5}  {'limit':>5}  {'headroom':>8}")
    for skill, words, limit in sorted(sizes, key=lambda t: -t[1]):
        print(f"{skill:<{width}}  {words:>5}  {limit:>5}  {limit - words:>8}")
    if sizes:
        w = [words for _, words, _ in sizes]
        print(f"{len(w)} skills: median {statistics.median(w):.0f}, mean {statistics.mean(w):.0f}, largest {max(w)}")
    sib = sibling_word_counts(skills_dir)
    if sib:
        print(f"{len(sib)} sibling reference files (no limit): median {statistics.median(sib):.0f}, largest {max(sib)}")


def main(argv=None):
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ap = argparse.ArgumentParser(description="Validate skill frontmatter, links and size.")
    ap.add_argument("skills_dir", nargs="?", default=None)
    ap.add_argument("--root", default=None, help="repository root whose docs/ and README.md are link-checked too")
    ap.add_argument("--sizes", action="store_true", help="print current word counts against the limits")
    args = ap.parse_args(argv)
    if args.skills_dir is None:
        args.skills_dir = os.path.join(root, "skills")
        args.root = args.root or root

    try:
        problems, sizes = check(args.skills_dir, args.root)
    except RuntimeError as e:
        print(f"check-skills: {e}", file=sys.stderr)
        return 2
    print(f"check-skills: {len(sizes)} skills, {len(problems)} problem(s)")
    for p in problems:
        print("  " + p)
    if args.sizes:
        print()
        print_sizes(args.skills_dir, sizes)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
