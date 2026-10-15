#!/usr/bin/env python3
"""Fail if this repository contains material copied from decompiled SCP:SL assemblies.

SCP:SL EULA section 12 forbids distributing non-public Northwood data, and decompiled Assembly-CSharp
is exactly that. Skills may DESCRIBE behaviour and NAME public types; they may not quote the
decompiled bodies, their local variable names, their private fields or their line numbers.

Checks, with very different strength:
  1. Signatures of decompiler output (ILSpy headers, compiler-generated identifiers), maintainer
     machine paths and references to private research. Always on. WEAK: it only catches output that
     still carries the decompiler's fingerprints.
  2. Decompiler artefacts. Always on. Catches what survives retyping and reformatting inside prose and
     data files (.md, .json, .txt, .yml): decompiler-style locals (num2, flag3, e3, ammoPickup2),
     statement-shaped code pasted into a sentence or a JSON string ("if (!e.IsAllowed) return;"),
     private underscore fields of game types, "Type/:49" style line references, and research tags
     (the sceptic-review markers, "(local research files)"). Inside fenced code blocks and in code
     files (.cs, .py, ...) only the locals check (fences) and the line-reference / research-tag checks
     run, because the repository's own examples and tools are original code that legitimately has
     assignments and underscore fields.
  3. Verbatim overlap with a local decompiled corpus. Only when --corpus is given (the corpus is never
     committed). Two detectors, both whitespace- and formatting-insensitive:
       - whole lines: >= --min-run consecutive non-trivial lines that also appear in the corpus;
       - token n-grams: every run of --ngram consecutive tokens (identifiers lower-cased, digit-suffixed
         locals and leading underscores normalised away, quotes and markdown punctuation dropped, `using`
         lines skipped) that also occurs in the corpus, so a fragment inside ONE long string or sentence
         is found, and so is code whose variables were renamed or whose layout was changed. A window only
         counts when it looks like code and not like a framework idiom: at least --min-informative
         distinct non-boilerplate identifiers, at least --min-structural operators or brackets (a list of
         enum members is public vocabulary), and a presence in at most --max-corpus-files corpus files
         (a counting loop occurs in dozens, a copied body in one or two). The few matches that survive
         and are reviewed by hand live in tools/check-publishable.reviewed, each with a reason; a stale
         entry is itself a finding.
     This is the only check that can back "nothing was copied".
  4. Private words: an optional local, untracked `.private-words` file at the repository root (or
     --private-words PATH), one case-insensitive regex per line, `#` starts a comment. Any match in a
     scanned file's text or path is a finding. For names that must never reach the public repo.

IGNORE MARKER. A test fixture that must contain leak patterns on purpose may carry, in its first 10
lines, a comment `check-publishable: ignore-file <reason>`. It is honoured ONLY for files under
FIXTURE_DIRS, ONLY for checks 1 and 2, and ONLY with a reason. It never disables private words or the
corpus checks. A marker anywhere else, or without a reason, is itself a finding, and every honoured
marker is listed in the report. This checker's own source is exempt from checks 1 and 2 (it has to
spell the patterns out) and is listed as such.

GUARANTEE LEVEL. Without --corpus a "no findings" result means only "no decompiler fingerprints, no
artefacts, no local paths, no private-research references", and the tool says so in its status line
("WEAK CHECK (no corpus)"). Use --require-corpus in release scripts: it exits 2 instead of reporting a
weak pass when the corpus is missing.

Usage:
  python3 tools/check-publishable.py [--corpus /path/to/decompiled/AssemblyCSharp] [--require-corpus]
                                     [--min-run 3] [--ngram 12] [--min-informative 5]
                                     [--min-structural 2] [--max-corpus-files 3]
                                     [--private-words FILE] [--root DIR] [--all]
Exit code 0 = no findings, 1 = findings, 2 = bad setup (corpus required but missing, or an
unreadable / invalid private-words file).
"""
import argparse
import os
import re
import sys
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# MSBuild files matter most: a build default is exactly where a maintainer's local path lands.
SCAN_EXT = {".md", ".cs", ".py", ".json", ".txt", ".yml", ".yaml", ".sh",
            ".props", ".targets", ".csproj", ".sln", ".slnx", ".xml", ".config", ".ps1", ".cmd", ".bat"}
# Prose and data: sentences, JSON strings. Everything else is code and gets the light artefact set.
PROSE_EXT = {".md", ".json", ".txt", ".yml", ".yaml"}
SKIP_DIRS = {".git", "bin", "obj", "node_modules", "__pycache__"}
# This file legitimately contains the signature and artefact patterns below.
SELF = os.path.abspath(__file__)
# The only places where the ignore marker is honoured (relative to the scanned root, "/" separated).
FIXTURE_DIRS = ("tools/scpsl-lint/tests/fixtures",)
IGNORE_MARKER = re.compile(r"^\s*(?:#|//|/\*|\*|<!--)\s*check-publishable: ignore-file\b(.*)$")
MIN_REASON_CHARS = 10
# Reviewed corpus matches: a deliberately tiny list of lines that overlap the decompiled corpus because
# they are a standard .NET idiom or a public API signature, each with the reason. Relative to the scanned
# root; the extension keeps it out of the scan (the snippets are exactly what the scan would flag).
REVIEWED_FILE = "tools/check-publishable.reviewed"
MIN_SNIPPET_CHARS = 25

SIGNATURES = [
    (re.compile(r"Decompiled with ICSharpCode|ILSpy|// Assembly location:|// MVID:"), "decompiler header"),
    (re.compile(r"<>c__DisplayClass|<>f__AnonymousType|<>c\.<|\bCS\$<>"), "compiler-generated name"),
    (re.compile(r"<PrivateImplementationDetails>|<Module>"), "compiler-generated type"),
    (re.compile(r"\[CompilerGenerated\]|\[DebuggerHidden\]"), "compiler attribute (typical of decompiled output)"),
    # A maintainer's machine leaking into the public repo: home directories with real content
    # (generic examples like /home/scpsl/frames.csv stay allowed) and Windows profile paths.
    (re.compile(r"/home/[\w.-]+/(?:Dokumenty|Documents|Desktop|Pulpit|\.config|\.local|\.claude|\.dotnet)\b"), "local user path"),
    (re.compile(r"[A-Za-z]:\\\\?Users\\\\?(?!<|%|\{)[\w .-]+\\\\?(?:AppData|Documents|Desktop)"), "local Windows profile path"),
    # The private research cites decompiled code; the public repo must not point into it.
    (re.compile(r"docs/badania|research-local|scpslEngine/"), "reference to private research"),
]

# --- Decompiler artefacts -----------------------------------------------------------------------------
# Digit-suffixed names that are legitimate everywhere (hashes, encodings, platforms, vector types).
_LEGIT_DIGITS = r"(?:Vector|Int|UInt|Base|Sha|Utf|Float|Quaternion|Matrix|Win|Net|Md|Ipv|Uint|Vec|Rgb|Mp|Crc|Rfc)"
# ILSpy names unnamed temporaries num/flag/obj/... and appends a counter to a name that is already
# taken (hub2, ammoPickup2, e3), so both shapes are decompiler fingerprints.
LOCAL_RX = re.compile(
    r"(?<![\w.])(?:(?:num|flag|obj|array|text|str|list|ptr|result|ev|args|hub|e)\d{1,2}"
    r"|(?!" + _LEGIT_DIGITS + r")[a-z]+(?:[A-Z][a-z0-9]*)+\d{1,2})(?![\w(])")
LEGIT_DIGIT_SUFFIX = re.compile(_LEGIT_DIGITS + r"\d+$")
# Statement-shaped code inside a sentence or a JSON string. Prose does not contain these.
STATEMENT_RXS = [
    (re.compile(r"\b(?:else\s+)?if\s*\(\s*!?\s*[A-Za-z_][\w.?]*(?:\s*(?:==|!=|<=|>=|<|>)\s*[\w.]+)?\s*\)"),
     "code statement (if condition)"),
    (re.compile(r"(?<![\w.=!<>])[A-Za-z_][\w.]*\s*=\s*!?[A-Za-z_][\w.?]*(?:\([^()]*\))?\s*;"),
     "code statement (assignment)"),
    (re.compile(r"\breturn\s+[\w.!?()\[\]]+\s*;"), "code statement (return)"),
    (re.compile(r"[A-Za-z_]\w*(?:\+\+|--)(?=\s*[<>=;)\],]|$)"), "code statement (increment)"),
    (re.compile(r"[A-Za-z_]\w*\)?\?\.[A-Za-z_]"), "code statement (null-conditional access)"),
]
# A private underscore field of a game type quoted in prose: camelCase after the underscore, or a bare
# lower-case name that is immediately assigned / incremented / called.
FIELD_RX = re.compile(r"(?<![\w])_[a-z][a-z0-9]*[A-Z]\w*(?![\w])|(?<![\w])_[a-z]\w{2,}(?=\s*(?:=[^=]|\+\+|--))")
# "IntercomMuteCommand/:49", "Foo.cs:120": a pointer into decompiled source.
LINEREF_RXS = [
    (re.compile(r"\b[A-Z]\w{3,}/:\d+\b"), "decompiled line reference (Type/:NN)"),
    (re.compile(r"\b[A-Z][A-Za-z0-9]{3,}\.cs:\d+\b"), "decompiled line reference (File.cs:NN)"),
]
# Notes taken during private research that leaked into a deliverable.
# A bare word like "sceptic" is fine in a process skill that tells a reviewer how to behave; the leak is
# the tag form: "SKEPTIC: ...", "SKEPTIC ADDITIONS", "(local research files)".
TAG_RXS = [
    (re.compile(r"\bSKEPTIC(?:\s+ADDITIONS?)?\s*[:(]|\bSKEPTIC\s+ADDITIONS?\b"), "research tag"),
    # Only the parenthetical provenance form: docs that merely state the policy ("never cite private
    # research notes") are not leaks.
    (re.compile(r"\((?:from )?(?:the )?(?:local|private) research (?:files?|notes?)\)", re.I), "research tag"),
]

# --- Token n-grams ------------------------------------------------------------------------------------
TOKEN_RX = re.compile(r"[A-Za-z_][A-Za-z0-9_]*|\d+(?:\.\d+)?|==|!=|<=|>=|&&|\|\||\+\+|--|=>|\?\?|\?\.|[^\sA-Za-z0-9_]")
# Dropped on both sides: quotes, backticks, table bars, backslashes and emphasis stars carry no meaning in
# code and only exist because the fragment sits inside a JSON string or a markdown sentence.
DROPPED = frozenset('`|\\"\'*#')
LOCAL_TOKEN = re.compile(r"^[a-z]+\d+$")
# Words that make every C# file look alike. A window needs real identifiers besides these.
BOILERPLATE = frozenset("""
public private protected internal static void int uint long bool string float double byte char var new this
base class struct enum interface namespace using return if else for foreach while do in is as null true false
get set value override virtual abstract readonly const sealed ref out params typeof default switch case break
continue try catch finally throw object partial async await yield delegate event lambda <l> and or not the a
""".split())


USING_DIRECTIVE = re.compile(r"^\s*(?:using\s+(?:static\s+)?[\w.=\s]+;|namespace\s+[\w.]+\s*[;{]?)\s*$")


def tokenise(text_lines):
    """Returns (tokens, line_numbers) with the normalisation described in the module docstring.
    `using X;` and `namespace X` lines are skipped: every C# file starts with the same few of them."""
    toks, lns = [], []
    for ln, line in enumerate(text_lines, 1):
        if USING_DIRECTIVE.match(line):
            continue
        for m in TOKEN_RX.finditer(line):
            t = m.group(0)
            if t in DROPPED:
                continue
            if t[0].isalpha() or t[0] == "_":
                t = t.lower()
                if LOCAL_TOKEN.match(t):
                    t = "<l>"
                else:
                    t = t.lstrip("_") or "_"
            toks.append(t)
            lns.append(ln)
    return toks, lns


# Tokens that make a window CODE rather than a list of names: a run of enum members or type names
# ("None, Radio, Intercom, ...") is a public vocabulary, not a copied body.
STRUCTURAL = frozenset("{ } ( ) [ ] = == != < > <= >= && || ; ++ -- => ?? ?. + - * / ! ? :".split()) - {":"}


def window_stats(toks, n):
    """Per window: (number of distinct identifiers that are not boilerplate and longer than 2 characters,
    number of structural tokens). Loop counters (i, j) and keywords tell nothing about origin."""
    out = []
    for i in range(len(toks) - n + 1):
        w = toks[i:i + n]
        ids = {t for t in w if t[0].isalpha() and len(t) > 2 and t not in BOILERPLATE}
        out.append((len(ids), sum(1 for t in w if t in STRUCTURAL)))
    return out


def window_hashes(toks, n):
    return [hash(tuple(toks[i:i + n])) for i in range(len(toks) - n + 1)]


def normalise(line):
    line = line.strip()
    line = re.sub(r"\s+", " ", line)
    return line


def trivial(line):
    # Braces, one-word lines, comments and very short lines appear everywhere; they are not evidence.
    if len(line) < 28:
        return True
    if line.startswith(("//", "/*", "*", "#", "using ", "namespace ")):
        return True
    return False


def iter_files(root):
    for dp, dns, fns in os.walk(root):
        dns[:] = [d for d in dns if d not in SKIP_DIRS]
        for f in fns:
            p = os.path.join(dp, f)
            if os.path.splitext(f)[1].lower() in SCAN_EXT:
                yield p


def load_corpus(corpus, ngram):
    """Returns (set of normalised lines, {window hash: number of corpus files containing it}).
    Hashes are only comparable inside this process (str hashing is salted), which is all the check needs.
    The file count is the genericity measure: an idiom such as a counting loop or a framework method
    signature occurs in dozens of corpus files and proves nothing, a copied fragment occurs in one or two."""
    lines = set()
    grams = {}
    for dp, _, fns in os.walk(corpus):
        for f in fns:
            if not f.endswith(".cs"):
                continue
            try:
                with open(os.path.join(dp, f), encoding="utf-8", errors="ignore") as fh:
                    raw_lines = fh.read().splitlines()
            except OSError:
                continue
            for raw in raw_lines:
                n = normalise(raw)
                if not trivial(n):
                    lines.add(n)
            toks, _ = tokenise(raw_lines)
            for h in set(window_hashes(toks, ngram)):
                grams[h] = grams.get(h, 0) + 1
    return lines, grams


def load_private_words(path):
    """Returns (compiled patterns, error). A missing file is fine (it is optional); a bad regex is not."""
    if not os.path.isfile(path):
        return [], None
    words = []
    try:
        with open(path, encoding="utf-8", errors="ignore") as fh:
            for n, raw in enumerate(fh, 1):
                line = raw.strip()
                if not line or line.startswith("#"):
                    continue
                try:
                    words.append(re.compile(line, re.IGNORECASE))
                except re.error as e:
                    # A silently skipped pattern would turn the safety net into a false "clean".
                    return [], f"{path}:{n}: invalid regex {line!r}: {e}"
    except OSError as e:
        return [], f"cannot read {path}: {e}"
    return words, None


def load_reviewed(path):
    """Returns (entries, error). Entry = (file, snippet, reason). Format per line: `file | snippet | reason`."""
    if not os.path.isfile(path):
        return [], None
    entries = []
    try:
        with open(path, encoding="utf-8") as fh:
            for n, raw in enumerate(fh, 1):
                line = raw.strip()
                if not line or line.startswith("#"):
                    continue
                parts = [x.strip() for x in line.split(" | ", 2)]
                if len(parts) != 3 or len(parts[1]) < MIN_SNIPPET_CHARS or len(parts[2]) < MIN_REASON_CHARS:
                    return [], (f"{path}:{n}: expected `file | snippet (>= {MIN_SNIPPET_CHARS} chars) | "
                                f"reason (>= {MIN_REASON_CHARS} chars)`")
                entries.append(tuple(parts))
    except OSError as e:
        return [], f"cannot read {path}: {e}"
    return entries, None


def ignore_marker(text):
    """Returns (line number, reason) of an ignore marker in the first 10 lines, or None."""
    for i, line in enumerate(text[:10], 1):
        m = IGNORE_MARKER.match(line)
        if m:
            reason = re.sub(r"^[\s:()\-–—]+|[\s()\-]*(?:-->|\*/)?\s*$", "", m.group(1))
            return i, reason
    return None


def under_fixture_dir(rel):
    r = rel.replace(os.sep, "/")
    return any(r == d or r.startswith(d + "/") for d in FIXTURE_DIRS)


def mask_code_spans(line, open_len):
    """Blanks out markdown inline code spans. `open_len` is the backtick run length of a span that is still
    open from an earlier line of the same paragraph (0 = none). Returns (masked line, open_len)."""
    out, i = [], 0
    while i < len(line):
        if line[i] == "`":
            j = i
            while j < len(line) and line[j] == "`":
                j += 1
            run = j - i
            if open_len == 0:
                open_len = run
            elif run == open_len:
                open_len = 0
            out.append(" " * run)
            i = j
        else:
            out.append(" " if open_len else line[i])
            i += 1
    return "".join(out), open_len


def artefact_findings(text, ext):
    """Yields (line number, kind, snippet) for decompiler artefacts. See the module docstring for scope.

    In markdown, inline `code spans` are original teaching snippets (the skills show plugin code), so the
    statement and private-field rules look at the sentence around them, not inside them. A fragment
    pasted verbatim into a span is the n-gram check's job; renamed locals are caught everywhere."""
    prose = ext in PROSE_EXT
    in_fence = False
    open_len = 0
    for i, raw in enumerate(text, 1):
        if ext == ".md":
            if re.match(r"^\s{0,3}(?:```|~~~)", raw):
                in_fence = not in_fence
                continue
            if not raw.strip():
                open_len = 0  # a blank line ends the paragraph, so an unclosed backtick cannot swallow the rest
        rules = list(LINEREF_RXS) + list(TAG_RXS)
        sentence = raw
        if prose:
            rules.append((LOCAL_RX, "decompiler-style local"))
        for rx, why in rules:
            for m in rx.finditer(raw):
                if rx is LOCAL_RX and LEGIT_DIGIT_SUFFIX.search(m.group(0)):
                    continue  # toVector3, parseUInt32: type names that merely end in a digit
                yield i, why, m.group(0)
        if prose and not in_fence:
            if ext == ".md":
                sentence, open_len = mask_code_spans(raw, open_len)
            for rx, why in STATEMENT_RXS + [(FIELD_RX, "private game field")]:
                for m in rx.finditer(sentence):
                    yield i, why, m.group(0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", help="local decompiled AssemblyCSharp directory (never committed)")
    ap.add_argument("--require-corpus", action="store_true",
                    help="exit 2 unless a usable --corpus directory is given (without it the check is weak)")
    ap.add_argument("--min-run", type=int, default=3)
    ap.add_argument("--ngram", type=int, default=12, help="token window for the n-gram overlap check")
    ap.add_argument("--min-informative", type=int, default=5,
                    help="a window needs this many non-boilerplate identifiers to count")
    ap.add_argument("--min-structural", type=int, default=2,
                    help="a window needs this many operators/brackets, so lists of names do not count")
    ap.add_argument("--max-corpus-files", type=int, default=3,
                    help="a window found in more corpus files than this is a common idiom, not evidence")
    ap.add_argument("--root", default=ROOT, help="tree to scan (default: this repository)")
    ap.add_argument("--private-words", default=None,
                    help="file of case-insensitive regexes, one per line (default: <root>/.private-words if present)")
    ap.add_argument("--all", action="store_true", help="list every finding (default: first 6 per file and kind)")
    a = ap.parse_args()

    corpus_usable = bool(a.corpus) and os.path.isdir(a.corpus)
    if a.require_corpus and not corpus_usable:
        why = "no --corpus given" if not a.corpus else f"--corpus {a.corpus!r} is not a directory"
        print(f"check-publishable: --require-corpus set but {why}; refusing to report a weak pass", file=sys.stderr)
        return 2

    if a.private_words and not os.path.isfile(a.private_words):
        print(f"check-publishable: --private-words file not found: {a.private_words}", file=sys.stderr)
        return 2
    private_words, err = load_private_words(a.private_words or os.path.join(a.root, ".private-words"))
    if err:
        print(f"check-publishable: {err}", file=sys.stderr)
        return 2

    reviewed, err = load_reviewed(os.path.join(a.root, REVIEWED_FILE))
    if err:
        print(f"check-publishable: {err}", file=sys.stderr)
        return 2
    reviewed_used = {}  # entry -> number of corpus findings it suppressed

    def corpus_finding(rel, text, first, last, kind, msg):
        """Records a corpus finding unless a reviewed entry covers one of the lines it spans."""
        span = "\n".join(text[first - 1:last])
        for entry in reviewed:
            if entry[0] == rel.replace(os.sep, "/") and entry[1] in span:
                reviewed_used[entry] = reviewed_used.get(entry, 0) + 1
                return
        findings.append((rel, first, kind, msg))

    findings = []   # (rel, line or 0, kind, message)
    ignored = []    # (rel, reason)
    corpus_lines = grams = None
    if corpus_usable:
        corpus_lines, grams = load_corpus(a.corpus, a.ngram)

    for path in sorted(iter_files(a.root)):
        rel = os.path.relpath(path, a.root)
        try:
            with open(path, encoding="utf-8", errors="ignore") as fh:
                text = fh.read().splitlines()
        except OSError:
            continue
        ext = os.path.splitext(path)[1].lower()

        # What may be skipped: only checks 1 and 2, only for the checker itself or an explicitly
        # listed fixture directory, and a fixture needs a written reason.
        skip_leak_checks = False
        marker = ignore_marker(text)
        if os.path.abspath(path) == SELF:
            skip_leak_checks = True
            ignored.append((rel, "the checker's own pattern table"))
        elif marker is not None:
            line_no, reason = marker
            if not under_fixture_dir(rel):
                findings.append((rel, line_no, "ignore-marker",
                                 "ignore-file marker outside the fixture directories "
                                 f"({', '.join(FIXTURE_DIRS)}); it is not honoured here"))
            elif len(reason) < MIN_REASON_CHARS:
                findings.append((rel, line_no, "ignore-marker",
                                 "ignore-file marker without a reason (write why the file must contain leak patterns); not honoured"))
            else:
                skip_leak_checks = True
                ignored.append((rel, reason))

        for rx in private_words:
            if rx.search(rel):
                findings.append((rel, 0, "private-word", f"file name matches /{rx.pattern}/"))
        for i, raw in enumerate(text, 1):
            if not skip_leak_checks:
                for rx, why in SIGNATURES:
                    if rx.search(raw):
                        findings.append((rel, i, "signature", f"{why}: {raw.strip()[:120]}"))
            for rx in private_words:
                if rx.search(raw):
                    findings.append((rel, i, "private-word", f"/{rx.pattern}/: {raw.strip()[:120]}"))
        if not skip_leak_checks:
            for i, why, snippet in artefact_findings(text, ext):
                findings.append((rel, i, f"artefact: {why}", snippet.strip()[:80]))

        if corpus_lines is None:
            continue
        run = []
        for i, raw in enumerate(text, 1):
            n = normalise(raw)
            if trivial(n):
                continue
            if n in corpus_lines:
                run.append((i, n))
            else:
                if len(run) >= a.min_run:
                    corpus_finding(rel, text, run[0][0], run[-1][0], "corpus-lines",
                                   f"lines {run[0][0]}-{run[-1][0]}: {len(run)} consecutive lines match the decompiled corpus")
                run = []
        if len(run) >= a.min_run:
            corpus_finding(rel, text, run[0][0], run[-1][0], "corpus-lines",
                           f"lines {run[0][0]}-{run[-1][0]}: {len(run)} consecutive lines match the decompiled corpus")

        toks, lns = tokenise(text)
        if len(toks) >= a.ngram:
            stats = window_stats(toks, a.ngram)
            hits = [i for i, h in enumerate(window_hashes(toks, a.ngram))
                    if stats[i][0] >= a.min_informative and stats[i][1] >= a.min_structural
                    and 0 < grams.get(h, 0) <= a.max_corpus_files]
            start = prev = None
            for i in hits + [None]:
                if start is not None and (i is None or i != prev + 1):
                    first, last = lns[start], lns[prev + a.ngram - 1]
                    span = prev + a.ngram - start
                    where = f"line {first}" if first == last else f"lines {first}-{last}"
                    corpus_finding(rel, text, first, last, "corpus-ngram",
                                   f"{where}: {span} consecutive tokens match the decompiled corpus "
                                   f"(window of {a.ngram}): {text[first - 1].strip()[:100]}")
                    start = None
                if i is not None:
                    if start is None:
                        start = i
                    prev = i

    if corpus_lines is not None:
        for entry in reviewed:
            if entry not in reviewed_used:
                # A stale entry would keep a hole open for whatever later lands on that line.
                findings.append((REVIEWED_FILE, 0, "reviewed-list",
                                 f"entry for {entry[0]} matches no corpus finding any more; remove it"))

    words = f"{len(private_words)} private word pattern(s)" if private_words else "no private words (.private-words absent or empty)"
    if corpus_lines is not None:
        mode = (f"signatures + artefacts + corpus overlap ({len(corpus_lines)} corpus lines, "
                f"{len(grams)} corpus {a.ngram}-token windows) + {words}")
    else:
        mode = f"WEAK CHECK (no corpus): signatures + artefacts only + {words}"

    if ignored:
        print(f"check-publishable: {len(ignored)} file(s) exempt from the signature/artefact checks "
              "(private words and corpus checks still ran on them):")
        for rel, reason in ignored:
            print(f"  ignored: {rel}: {reason}")

    if reviewed_used:
        print(f"check-publishable: {sum(reviewed_used.values())} corpus match(es) suppressed by {REVIEWED_FILE}:")
        for (rel, snippet, reason), n in sorted(reviewed_used.items()):
            print(f"  reviewed: {rel}: {snippet[:70]}: {reason} ({n}x)")

    if findings:
        print(f"check-publishable: {len(findings)} finding(s) [{mode}]")
        shown = Counter()
        hidden = defaultdict(int)
        for rel, line, kind, msg in findings:
            shown[(rel, kind)] += 1
            if a.all or shown[(rel, kind)] <= 6:
                loc = f"{rel}:{line}" if line else rel
                print(f"  {loc}: [{kind}] {msg}")
            else:
                hidden[(rel, kind)] += 1
        for (rel, kind), n in sorted(hidden.items()):
            print(f"  {rel}: [{kind}] ... {n} more (use --all)")
        per_file = defaultdict(Counter)
        for rel, _, kind, _ in findings:
            per_file[rel][kind] += 1
        print("check-publishable: findings per file")
        for rel, c in sorted(per_file.items(), key=lambda kv: (-sum(kv[1].values()), kv[0])):
            detail = ", ".join(f"{k} {v}" for k, v in sorted(c.items()))
            print(f"  {rel}: {sum(c.values())} ({detail})")
        return 1
    if corpus_lines is None:
        # Deliberately not the word "clean": that is a claim this mode cannot back.
        print(f"check-publishable: no findings, but WEAK CHECK (no corpus) [{mode}]")
        print("  Verbatim copying of decompiled code was NOT checked. Pass --corpus DIR (--require-corpus enforces it).")
    else:
        print(f"check-publishable: clean [{mode}]")
    return 0


if __name__ == "__main__":
    sys.exit(main())
