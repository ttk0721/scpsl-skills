#!/usr/bin/env python3
"""scpsl-lint: a dependency-free static scanner for C# SCP:SL (LabAPI) plugin source.

It flags performance and correctness traps that were verified against LabAPI 1.1.7 / game 14.2.7
(see README.md for the fact behind every rule). It is a text scanner, not a compiler: comments and
string literals are masked first, then a small brace-structure model (types, methods, lambdas,
loops) is built so rules can ask "is this call inside an event handler / a per-frame method / a
loop over players / an off-thread lambda?". Event facts (frequency class, hot / burst /
conditionallyHot, ignored setters) are read at runtime from tools/catalog/labapi-events.json.

Usage:
    python3 scpsl_lint.py <path...> [--json] [--min-severity info|warn|error]
    exit code: 0 = no errors, 1 = at least one error-severity finding, 2 = usage / IO problem

Suppression (in the C# source):
    // scpsl-lint: ignore SL004            on the same line, or alone on the line above
    // scpsl-lint: ignore                  every rule for that line
    // scpsl-lint: ignore-file SL010       whole file
"""
from __future__ import annotations

import argparse
import bisect
import json
import os
import re
import sys
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

SEVERITY_ORDER = {"info": 0, "warn": 1, "error": 2}

# SL007 follows same-file calls from a main-thread context this many levels deep.
MAX_PROPAGATION_DEPTH = 3

# id -> (severity, short title). The README documents each one in full.
RULES: Dict[str, Tuple[str, str]] = {
    "SL001": ("warn", "subscription to a hot or conditionally hot event"),
    "SL002": ("warn", "subscription to a never-fired event"),
    "SL003": ("warn", "assignment to a setter the game ignores"),
    "SL004": ("error", "Server.Tps / tps command used as a signal"),
    "SL005": ("warn", "coroutine or delayed call per event / per player"),
    "SL006": ("error", "Timing.KillCoroutines() with no arguments"),
    "SL007": ("error", "blocking call on the main thread"),
    "SL008": ("warn", "logging in a hot path or loop"),
    "SL009": ("error", "FirstChanceException subscription"),
    "SL010": ("error", "GC.Collect()"),
    "SL011": ("warn", "player list enumerated every frame"),
    "SL012": ("warn", "hint or broadcast sent every frame"),
    "SL013": ("warn", "Assembly.Location / CodeBase of a byte-loaded plugin"),
    "SL014": ("warn", "Vector4 in a config class"),
    "SL015": ("info", "Disable() cleanup but no Shutdown subscription"),
    "SL016": ("warn", "Unity/LabAPI API touched off the main thread"),
    "SL017": ("info", "Harmony prefix returning false on a commonly patched method"),
    "SL018": ("error", "expensive per-raise work in a hot or conditionally hot event handler"),
    "SL019": ("warn", "expensive per-raise work in a burst (per-shot / per-hit) event handler"),
}

# Event frequency classes at which a per-event coroutine / debug log is a real cost.
HOTTISH_FREQUENCIES = {"per-frame-per-pair", "per-frame", "high-rate-network", "per-action"}

UPDATE_NAMES = {"Update", "LateUpdate", "FixedUpdate", "OnUpdate"}
CONTROL_KEYWORDS = {
    "if", "else", "while", "for", "foreach", "switch", "using", "lock", "catch", "fixed", "try",
    "finally", "do", "checked", "unchecked", "unsafe", "return", "new", "throw", "await", "yield",
    "case", "default", "when", "nameof", "typeof", "sizeof", "base", "this",
}


def summarize_condition(text, limit: int = 200) -> str:
    """One readable sentence out of a free-text catalog condition (hotConditions).

    The catalog keeps the long form for humans. A message needs the first sentence only, and cutting
    that at a fixed character count left half a parenthesis in it, so parenthetical asides are dropped
    first and a still-too-long sentence is cut at a word boundary."""
    out, depth = [], 0
    for ch in str(text or ""):
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth = max(0, depth - 1)
        elif depth == 0:
            out.append(ch)
    t = " ".join("".join(out).split())
    t = re.sub(r"\s+([,;:.])", r"\1", t)
    t = re.split(r"\.\s+(?=[A-Z])", t, maxsplit=1)[0].rstrip(". ")
    if len(t) > limit:
        t = t[:limit].rsplit(" ", 1)[0].rstrip(",;: ") + " ..."
    return t


# --------------------------------------------------------------------------------------
# Catalog
# --------------------------------------------------------------------------------------

def default_catalog_path() -> str:
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.normpath(os.path.join(here, "..", "catalog", "labapi-events.json"))


class Catalog:
    """Event facts keyed by 'PlayerEvents.Hurting', plus the CustomEventsHandler override names."""

    def __init__(self, data: dict):
        self.meta = data.get("meta", {})
        self.frequency_classes: Dict[str, str] = self.meta.get("frequencyClasses", {})
        self.events: Dict[str, dict] = {}
        for rec in data["events"]:
            key = rec["event"]
            if key in self.events:
                # One row per event is part of the catalog schema. Silently keeping one of two rows
                # would hide facts, so a hand-edited catalog with a repeated event fails loudly.
                raise ValueError(f"event {key} appears twice in the catalog")
            self.events[key] = dict(rec)
        self.categories = sorted({k.split(".", 1)[0] for k in self.events})
        # CustomEventsHandler virtuals are On{Category}{Event}, Category = class name minus "Events".
        self.override_map: Dict[str, str] = {}
        for key in self.events:
            cls, ev = key.split(".", 1)
            self.override_map["On" + cls[: -len("Events")] + ev] = key
        self.labapi = self.meta.get("labapi", "?")
        self.game = self.meta.get("game", "?")

    @staticmethod
    def load(path: str) -> "Catalog":
        with open(path, "r", encoding="utf-8") as fh:
            return Catalog(json.load(fh))

    def freq_text(self, rec: dict) -> str:
        freq = rec.get("frequency", "unknown")
        desc = self.frequency_classes.get(freq, "")
        detail = rec.get("frequencyDetail")
        text = freq
        if desc:
            text += ": " + desc
        if detail:
            text += " (" + str(detail) + ")"
        return text

    @staticmethod
    def is_hottish(rec: dict) -> bool:
        return bool(rec.get("hot")) or rec.get("frequency") in HOTTISH_FREQUENCIES

    @staticmethod
    def is_hot(rec: dict) -> bool:
        return bool(rec.get("hot"))

    @staticmethod
    def is_conditionally_hot(rec: dict) -> bool:
        # A catalog written before the flag existed still carries hotConditions.
        return bool(rec.get("conditionallyHot")) or bool(rec.get("hotConditions"))

    @staticmethod
    def is_burst(rec: dict) -> bool:
        return bool(rec.get("burst"))

    @staticmethod
    def always_ignored_setters(rec: dict) -> List[str]:
        """Setter names the game ignores in every situation (catalog `ignoredSetters`: names only)."""
        return [n for n in rec.get("ignoredSetters") or [] if isinstance(n, str) and n.isidentifier()]

    @staticmethod
    def conditionally_ignored_setters(rec: dict) -> List[Tuple[str, str]]:
        """(setter, when) pairs from the catalog `conditionallyIgnoredSetters`: setters the game ignores
        only in the situation `when` describes."""
        out: List[Tuple[str, str]] = []
        for entry in rec.get("conditionallyIgnoredSetters") or []:
            if isinstance(entry, dict) and str(entry.get("setter", "")).isidentifier():
                out.append((entry["setter"], " ".join(str(entry.get("when") or "").split())))
        return out

    @staticmethod
    def work_rule(rec: dict) -> Optional[str]:
        """Which rule polices per-raise work in this event's handlers: SL018 (error) for hot and
        conditionally hot events, SL019 (warn) for burst ones, None for everything else."""
        if Catalog.is_hot(rec) or Catalog.is_conditionally_hot(rec):
            return "SL018"
        if Catalog.is_burst(rec):
            return "SL019"
        return None


# --------------------------------------------------------------------------------------
# Lexical masking: comments and string literals
# --------------------------------------------------------------------------------------

_CHAR_RE = re.compile(r"'(?:\\u[0-9a-fA-F]{4}|\\x[0-9a-fA-F]{1,4}|\\.|[^\\'\n])'")


def mask_source(src: str) -> Tuple[str, str]:
    """Return (code, keep). Both have the same length and the same newlines as `src`.

    `code`: comments blanked, string/char literal contents blanked (quotes kept), interpolation
    holes kept as code (their braces blanked so they do not look like blocks).
    `keep`: only comments blanked; used where a rule needs string contents (Harmony attribute
    arguments, the "tps" command string).
    """
    n = len(src)
    code = list(src)
    keep = list(src)

    def blank(buf: List[str], a: int, b: int) -> None:
        for k in range(a, min(b, n)):
            if buf[k] != "\n" and buf[k] != "\r":
                buf[k] = " "

    def scan_string(i: int, verbatim: bool, interp: bool) -> int:
        # `i` is the opening quote; returns the index just past the closing quote.
        j = i + 1
        while j < n:
            c = src[j]
            if verbatim:
                if c == '"':
                    if j + 1 < n and src[j + 1] == '"':
                        blank(code, j, j + 2)
                        j += 2
                        continue
                    return j + 1
            else:
                if c == "\\":
                    blank(code, j, j + 2)
                    j += 2
                    continue
                if c == '"':
                    return j + 1
                if c == "\n":  # unterminated: do not swallow the rest of the file
                    return j
            if interp and c == "{":
                if j + 1 < n and src[j + 1] == "{":
                    blank(code, j, j + 2)
                    j += 2
                    continue
                blank(code, j, j + 1)
                close = mask_region(j + 1, True)
                if close < n:
                    blank(code, close, close + 1)
                j = close + 1
                continue
            if interp and c == "}" and j + 1 < n and src[j + 1] == "}":
                blank(code, j, j + 2)
                j += 2
                continue
            blank(code, j, j + 1)
            j += 1
        return n

    def scan_raw_string(k: int) -> int:
        # `k` is the first of >= 3 opening quotes.
        q = 0
        while k + q < n and src[k + q] == '"':
            q += 1
        end = src.find('"' * q, k + q)
        if end < 0:
            blank(code, k + q, n)
            return n
        blank(code, k + q, end)
        return end + q

    def mask_region(i: int, in_hole: bool) -> int:
        depth = 0
        while i < n:
            c = src[i]
            if c == "#" and not src[src.rfind("\n", 0, i) + 1:i].strip():
                # Preprocessor directive: `#region don't` must not be lexed as a char literal.
                e = src.find("\n", i)
                e = n if e < 0 else e
                blank(code, i, e)
                blank(keep, i, e)
                i = e
                continue
            if c == "/" and i + 1 < n and src[i + 1] == "/":
                e = src.find("\n", i)
                e = n if e < 0 else e
                blank(code, i, e)
                blank(keep, i, e)
                i = e
                continue
            if c == "/" and i + 1 < n and src[i + 1] == "*":
                e = src.find("*/", i + 2)
                e = n if e < 0 else e + 2
                blank(code, i, e)
                blank(keep, i, e)
                i = e
                continue
            if c == '"' or ((c == "$" or c == "@") and _string_prefix_end(src, i) is not None):
                k = i
                while k < n and src[k] in "$@":
                    k += 1
                prefix = src[i:k]
                if k < n and src[k] == '"':
                    verbatim = "@" in prefix
                    interp = "$" in prefix
                    if not verbatim and src.startswith('"""', k):
                        i = scan_raw_string(k)
                    else:
                        i = scan_string(k, verbatim, interp)
                    continue
            if c == "'":
                m = _CHAR_RE.match(src, i)
                if m:
                    blank(code, i + 1, m.end() - 1)
                    i = m.end()
                    continue
            if in_hole:
                if c == "{":
                    depth += 1
                elif c == "}":
                    if depth == 0:
                        return i
                    depth -= 1
            i += 1
        return n

    mask_region(0, False)
    return "".join(code), "".join(keep)


def _string_prefix_end(src: str, i: int) -> Optional[int]:
    k = i
    n = len(src)
    while k < n and src[k] in "$@":
        k += 1
    if k < n and src[k] == '"' and k - i <= 2:
        return k
    return None


# --------------------------------------------------------------------------------------
# Brace structure model
# --------------------------------------------------------------------------------------

@dataclass(eq=False)
class Block:
    start: int          # index of '{' (or of the char after '=>' for expression-bodied members)
    end: int            # index of the matching '}' (or of the ';' for expression-bodied members)
    kind: str           # root namespace type method lambda property accessor other
    name: str = ""
    header: str = ""    # code text since the previous boundary (strings masked)
    header_keep: str = ""   # same span with string contents preserved
    header_start: int = 0
    params: Optional[str] = None
    ret: str = ""
    name_pos: int = 0
    parent: Optional["Block"] = None
    children: List["Block"] = field(default_factory=list)


_ATTR_RE = re.compile(r"\[[^\[\]]*\]")
_WHERE_RE = re.compile(r"\s+where\s+\w+\s*:.*$", re.S)
_METHOD_RE = re.compile(r"(?P<name>~?[A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\((?P<params>.*)\)\s*$", re.S)
_TYPE_RE = re.compile(r"(?<![\w.])(class|struct|interface|enum|record)\s+(?:(?:class|struct)\s+)?([A-Za-z_]\w*)")
_EXPR_MEMBER_RE = re.compile(
    r"^\s*[\w\s<>\[\],.?*@:]*?\b(?P<name>[A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\((?P<params>[^;]*?)\)\s*=>", re.S)


def _blank_attrs(text: str) -> str:
    prev = None
    while prev != text:
        prev = text
        text = _ATTR_RE.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    return text


def split_args(text: str) -> List[Tuple[int, str]]:
    """Split `text` on top-level commas; returns (offset, piece) pairs."""
    out: List[Tuple[int, str]] = []
    depth = 0
    start = 0
    for i, c in enumerate(text):
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "," and depth == 0:
            out.append((start, text[start:i]))
            start = i + 1
    out.append((start, text[start:]))
    return out


def first_param_name(params: Optional[str]) -> Optional[str]:
    if params is None:
        return None
    parts = split_args(params)
    if not parts or not parts[0][1].strip():
        return None
    piece = parts[0][1].split("=")[0].strip()
    m = re.search(r"([A-Za-z_]\w*)\s*$", piece)
    return m.group(1) if m else None


def match_paren(code: str, i: int) -> int:
    """Index of the ')' matching the '(' at `i`, or -1."""
    depth = 0
    n = len(code)
    while i < n:
        c = code[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def statement_end(code: str, i: int) -> int:
    """Index of the ';' ending the statement that starts at `i` (or the closing bracket that ends
    the enclosing construct first, or len(code))."""
    depth = 0
    n = len(code)
    while i < n:
        c = code[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            if depth == 0:
                return i
            depth -= 1
        elif c == ";" and depth == 0:
            return i
        i += 1
    return n


def _classify(header: str, header_keep: str, header_start: int, parent: Block, start: int) -> Block:
    blanked = _blank_attrs(header)
    hs = blanked.strip()
    lead = len(blanked) - len(blanked.lstrip())  # offsets below are relative to the stripped text
    hs_nowhere = _WHERE_RE.sub("", hs)
    blk = Block(start=start, end=start, kind="other", header=header, header_keep=header_keep,
                header_start=header_start, parent=parent)

    if re.search(r"=>\s*$", hs) or re.search(r"\bdelegate\s*(\([^)]*\))?\s*$", hs):
        blk.kind = "lambda"
        return blk
    if re.match(r"^(?:namespace)\b", hs):
        blk.kind = "namespace"
        return blk
    first_word = re.match(r"^\s*([A-Za-z_]\w*)", hs)
    fw = first_word.group(1) if first_word else ""
    if parent.kind in ("root", "namespace", "type"):
        tm = _TYPE_RE.search(hs_nowhere)
        if tm and ("(" not in hs_nowhere[: tm.start()]):
            blk.kind = "type"
            blk.name = tm.group(2)
            return blk
    if parent.kind == "property":
        blk.kind = "accessor"
        return blk
    if parent.kind == "type":
        if "=" in re.sub(r"=>", "", hs_nowhere) and "(" not in hs_nowhere.split("=")[0]:
            return blk  # field/property initializer such as `= new Foo {` -> plain block
        mm = _METHOD_RE.search(hs_nowhere)
        if mm and mm.group("name") not in CONTROL_KEYWORDS:
            blk.kind = "method"
            blk.name = mm.group("name")
            blk.params = mm.group("params")
            blk.name_pos = header_start + lead + mm.start("name")
            blk.ret = hs_nowhere[: mm.start("name")].strip()
            return blk
        if "(" not in hs_nowhere and hs_nowhere:
            blk.kind = "property"
            return blk
        return blk
    # statement context: loops stay 'other' (loops are found separately); look for local functions
    if fw not in CONTROL_KEYWORDS and "=" not in hs_nowhere.split("(")[0]:
        mm = _METHOD_RE.search(hs_nowhere)
        if mm and mm.group("name") not in CONTROL_KEYWORDS and hs_nowhere[: mm.start("name")].strip():
            blk.kind = "method"
            blk.name = mm.group("name")
            blk.params = mm.group("params")
            blk.name_pos = header_start + lead + mm.start("name")
            blk.ret = hs_nowhere[: mm.start("name")].strip()
    return blk


def parse_blocks(code: str, keep: str) -> Block:
    n = len(code)
    root = Block(start=0, end=n, kind="root")
    stack = [root]
    paren_stack: List[int] = []
    paren = 0
    last = 0
    i = 0
    while i < n:
        c = code[i]
        if c == "(":
            paren += 1
        elif c == ")":
            paren = max(0, paren - 1)
        elif c == ";" and paren == 0:
            parent = stack[-1]
            if parent.kind == "type":
                em = _EXPR_MEMBER_RE.match(code[last:i])
                if em:
                    arrow = code.index("=>", last + em.start("params"))
                    blk = Block(start=arrow + 2, end=i, kind="method", name=em.group("name"),
                                params=em.group("params"), header=code[last:arrow],
                                header_keep=keep[last:arrow], header_start=last, parent=parent,
                                name_pos=last + em.start("name"))
                    blk.ret = _blank_attrs(code[last:last + em.start("name")]).strip()
                    parent.children.append(blk)
            last = i + 1
        elif c == "{":
            blk = _classify(code[last:i], keep[last:i], last, stack[-1], i)
            stack[-1].children.append(blk)
            stack.append(blk)
            paren_stack.append(paren)
            paren = 0
            last = i + 1
        elif c == "}":
            if len(stack) > 1:
                done = stack.pop()
                done.end = i
                paren = paren_stack.pop()
            last = i + 1
        i += 1
    for leftover in stack[1:]:
        leftover.end = n
    return root


def walk_blocks(root: Block) -> Iterable[Block]:
    todo = [root]
    while todo:
        b = todo.pop()
        yield b
        todo.extend(b.children)


# --------------------------------------------------------------------------------------
# Findings
# --------------------------------------------------------------------------------------

@dataclass
class Finding:
    rule: str
    severity: str
    path: str
    line: int
    column: int
    message: str

    def to_dict(self) -> dict:
        return {"rule": self.rule, "severity": self.severity, "file": self.path,
                "line": self.line, "column": self.column, "message": self.message}

    def format(self) -> str:
        return f"{self.path}:{self.line}:{self.column}: {self.severity} {self.rule}: {self.message}"


@dataclass
class Handler:
    event: str
    rec: dict
    start: int
    end: int
    param: Optional[str]
    kind: str  # "static" | "override"
    site: int


Range = Tuple[int, int]


def in_any(pos: int, ranges: Sequence[Range]) -> bool:
    for a, b in ranges:
        if a <= pos < b:
            return True
    return False


def subtract_ranges(ranges: List[Range], holes: List[Range]) -> List[Range]:
    out = list(ranges)
    for ha, hb in holes:
        nxt: List[Range] = []
        for a, b in out:
            if hb <= a or ha >= b:
                nxt.append((a, b))
                continue
            if a < ha:
                nxt.append((a, ha))
            if hb < b:
                nxt.append((hb, b))
        out = nxt
    return out


# --------------------------------------------------------------------------------------
# Per-file analysis
# --------------------------------------------------------------------------------------

_SUPPRESS_RE = re.compile(r"scpsl-lint:\s*(ignore-file|ignore)\b([^\n*]*)")

_LAMBDA_RE = re.compile(
    r"^\s*(?:async\s+)?(?:static\s+)?(?:\((?P<ps>[^()]*)\)|(?P<p1>[A-Za-z_]\w*))\s*=>")
_DELEGATE_RE = re.compile(r"^\s*delegate\s*(?:\((?P<ps>[^()]*)\))?")
_NEW_HANDLER_RE = re.compile(r"^\s*new\s*(?:[\w.]+\s*(?:<[^()]*>)?)?\(\s*(?P<n>[\w.]+)\s*\)\s*$")
_METHOD_GROUP_RE = re.compile(r"^\s*(?P<n>[\w.]+)\s*$")

_YIELD_FRAME_RE = re.compile(r"\byield\s+return\s+(?:Timing\s*\.\s*WaitForOneFrame|0(?:\.0*)?[fF]?)\s*;")
_THROTTLE_RE = re.compile(
    r"Time\s*\.\s*(?:time|unscaledTime|realtimeSinceStartup|frameCount|timeAsDouble)\b"
    r"|\bStopwatch\b|DateTime\s*\.\s*(?:UtcNow|Now)\b|Environment\s*\.\s*TickCount|%")
_PLAYER_ENUM_RE = re.compile(
    r"(?<!\w)(?:Player\s*\.\s*(?:List|ReadyList)\b|ReferenceHub\s*\.\s*AllHubs\b|Player\s*\.\s*GetAll\s*\()")
_PLAYER_LOOP_EXPR_RE = re.compile(
    r"(?<!\w)(?:Player\s*\.\s*(?:List|ReadyList)\b|ReferenceHub\s*\.\s*AllHubs\b|Player\s*\.\s*GetAll\s*\()"
    r"|\b\w*[Pp]layers\b|\b\w*[Hh]ubs\b")
_COUNT_AFTER_RE = re.compile(r"\s*\.\s*Count\b(?!\s*\()")

_LOG_RE = re.compile(r"(?<!\w)(?:Logger|Log)\s*\.\s*(Debug|Info|Raw|Warn|Error)\s*\(")
_TIMING_SPAWN_RE = re.compile(
    r"(?<!\w)Timing\s*\.\s*(?:RunCoroutine\w*|CallDelayed|CallPeriodically|CallContinuously)\s*(?:<[^()]*>)?\s*\(")
_HINT_RE = re.compile(r"\.\s*(?:SendHint|ShowHint|SendBroadcast|Broadcast|TargetAddElement|RpcAddElement)\s*\(")

_OFFTHREAD_CALL_RE = re.compile(
    r"(?<!\w)(?P<fn>Task\s*\.\s*Run|Task\s*\.\s*Factory\s*\.\s*StartNew|ThreadPool\s*\.\s*(?:Unsafe)?QueueUserWorkItem"
    r"|new\s+(?:(?:global\s*::\s*)?System\s*\.\s*Threading\s*\.\s*)?Thread"
    r"|new\s+(?:(?:global\s*::\s*)?System\s*\.\s*Threading\s*\.\s*Tasks\s*\.\s*)?Task|Background\s*\.\s*TryRun)"
    r"\s*(?:<[^()]*>)?\s*\(")
_SAFE_RETURN_CALL_RE = re.compile(
    r"(?<!\w)(?:MainThread\s*\.\s*Post|MainThreadDispatcher\s*\.\s*Dispatch|\w+\s*\.\s*Post|Dispatch)\s*\(")
_TOUCH_RES = [
    re.compile(r"(?<!\w)(?:Player|Server|Map|Round|Warhead|NetworkServer|NetworkClient|GameObject|ReferenceHub|Physics|Timing)\s*\.\s*[A-Za-z_]"),
    re.compile(r"\btransform\b|\bgameObject\b"),
    re.compile(r"(?<![\w.])(?:player|plr|ply|hub|referenceHub|ev\s*\.\s*Player)\s*\.\s*[A-Z]"),
]

_BLOCKING_RES: List[Tuple[re.Pattern, str]] = [
    (re.compile(r"\.\s*GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\("), ".GetAwaiter().GetResult()"),
    (re.compile(r"\.\s*(?:Wait|WaitOne|WaitAll|WaitAny)\s*\("), ".Wait()/WaitOne()/WaitAll()"),
    # `new WebClient()`, `new System.Net.WebClient()` and `new global::System.Net.WebClient()`.
    (re.compile(r"\bnew\s+(?:global\s*::\s*)?(?:System\s*\.\s*Net\s*\.\s*)?WebClient\s*[({]"), "WebClient"),
    (re.compile(r"\.\s*GetResponse\s*\("), "HttpWebRequest.GetResponse()"),
    (re.compile(r"\bThread\s*\.\s*Sleep\s*\("), "Thread.Sleep()"),
    (re.compile(r"\.\s*Join\s*\(\s*\)"), "Thread.Join()"),
    (re.compile(r"(?<![\w.])(?:System\s*\.\s*IO\s*\.\s*)?File\s*\.\s*(?:ReadAll|WriteAll|AppendAll)\w+(?<!Async)\s*\("),
     "synchronous File IO"),
]
_RESULT_RE = re.compile(r"(?P<recv>[\w)\]]+)\s*\.\s*Result\b(?!\s*\()")
_TASK_NAME_HINT_RE = re.compile(r"(?i)task|async|request|download|fetch|future|promise|http|client")

# Work that must not run once per raise in a handler of a hot / conditionally hot / burst event.
_WORK_STRING_RES: List[Tuple[re.Pattern, str]] = [
    (re.compile(r"(?<!\w)\$@?\"|(?<!\w)@\$\""), "string interpolation"),
    # literal + x, x + literal, x += literal (literal contents are masked, the quotes remain)
    (re.compile(r"\"\s*\+(?!\+)|(?<!\+)\+=?\s*[$@]*\""), "string concatenation"),
    (re.compile(r"(?<![\w.])(?:System\s*\.\s*)?(?:string|String)\s*\.\s*(?:Format|Concat|Join)\s*\("), "string.Format/Concat/Join"),
]
_WORK_LINQ_RE = re.compile(
    r"(?P<recv>[\w\])]*)\s*\.\s*(?P<fn>Where|Select|SelectMany|ToList|ToArray|ToDictionary|ToHashSet|Any|All|"
    r"First|FirstOrDefault|Last|LastOrDefault|Single|SingleOrDefault|Count|OrderBy|OrderByDescending|ThenBy|"
    r"GroupBy|Distinct|Skip|Take|Sum|Min|Max|Average|Cast|OfType|Union|Except|Intersect|Zip|Aggregate|"
    r"SequenceEqual)\s*(?:<[^()]*>)?\s*\(")
_LINQ_NOT_RECEIVERS = {"Math", "Mathf", "string", "String", "Interlocked", "Volatile"}
_WORK_LOCK_RE = re.compile(r"(?<![\w.])lock\s*\(|(?<![\w])Monitor\s*\.\s*(?:Enter|TryEnter|Exit)\s*\(")
_WORK_THROW_RE = re.compile(r"(?<![\w.])throw\b")
_LAZY_INIT_RE = re.compile(
    r"\s*(?:else\s+)?if\s*\(.*(?:TryGetValue|ContainsKey|==\s*null|\bis\s+null|!\s*\w+\s*\.\s*Contains)", re.S)
_WORK_NEW_RE = re.compile(r"(?<![\w.])new\b\s*")
_WORK_IO_RES: List[Tuple[re.Pattern, str]] = [
    (re.compile(r"(?<![\w.])(?:System\s*\.\s*IO\s*\.\s*)?(?:File|Directory)\s*\.\s*[A-Z]\w*\s*\("), "file IO"),
    (re.compile(r"\bnew\s+(?:[\w.]+\s*\.\s*)?(?:StreamWriter|StreamReader|FileStream|BinaryWriter|BinaryReader|"
                r"WebClient|HttpClient|TcpClient|UdpClient|Socket)\s*[({]"), "file/network IO"),
    (re.compile(r"(?<![\w.])(?:UnityWebRequest|SlHttp)\s*\.\s*\w+"), "network IO"),
    (re.compile(r"\.\s*(?:Get|Post|Put|Delete|Send)(?:String|ByteArray|Stream)?Async\s*\("), "network IO"),
]
# Value types: `new Vector3(...)` does not allocate on the heap.
_VALUE_TYPES = {
    "Vector2", "Vector3", "Vector4", "Vector2Int", "Vector3Int", "Quaternion", "Color", "Color32", "Bounds", "Rect",
    "Ray", "Matrix4x4", "Plane", "RaycastHit", "DateTime", "TimeSpan", "Guid", "KeyValuePair", "ValueTuple",
    "Nullable", "CoroutineHandle", "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte", "float",
    "double", "decimal", "bool", "char",
}

# Callbacks the SlKit scheduler / MEC / the dispatcher run on the MAIN thread. SlClock jobs are the
# dangerous ones: a Thread.Sleep or a File.AppendAllText there stalls every frame, on a timer, silently.
_MAIN_CALLBACK_CALL_RE = re.compile(
    r"(?<![\w])(?P<fn>SlClock\s*\.\s*(?:Every|EveryStaggered|After)"
    r"|MainThread\s*\.\s*Post|MainThreadDispatcher\s*\.\s*Dispatch"
    r"|Timing\s*\.\s*(?:CallDelayed|CallPeriodically|CallContinuously)"
    r"|Background\s*\.\s*TryRun)\s*(?:<[^()]*>)?\s*\(")
_COMMAND_TYPE_RE = re.compile(r"\b(?:ICommand|ParentCommand|CustomCommand|ICustomCommand|IUsageProvider|CommandHandler)\b")

_PATCHED_NAMES_RE = re.compile(r"\b(?:WriteAll|ValidateVisibility|OnValidatedVisibility|get_CurrentRole)\b")
_CONFIG_NAME_RE = re.compile(r"(?i)(?:config|configuration|settings|options|cfg)$")

_CLEANUP_RE = re.compile(
    r"-=|\.\s*Dispose\s*\(|Unregister\w*\s*\(|KillCoroutines|\.\s*Kill\w*\s*\(|\.\s*Stop\s*\(|\.\s*Cancel\s*\("
    r"|\.\s*Clear\s*\(|\.\s*Save\w*\s*\(|\.\s*Flush\s*\(|\.\s*Close\s*\(|\.\s*Write\w*\s*\(")
_SHUTDOWN_RE = re.compile(
    r"ServerEvents\s*\.\s*Shutdown\s*\+=|override\s+void\s+OnServerShutdown\b"
    r"|ProcessExit\s*\+=|Application\s*\.\s*quitting\s*\+=")


class FileAnalysis:
    def __init__(self, path: str, raw: str, catalog: Catalog):
        self.path = path
        self.raw = raw
        self.catalog = catalog
        self.code, self.keep = mask_source(raw)
        self.line_starts = [0] + [m.end() for m in re.finditer(r"\n", raw)]
        self.root = parse_blocks(self.code, self.keep)
        self.blocks = list(walk_blocks(self.root))
        self.methods = [b for b in self.blocks if b.kind == "method"]
        self.findings: List[Finding] = []
        self._seen: set = set()
        self._suppress_line: Dict[int, set] = {}
        self._suppress_file: set = set()
        self._parse_suppressions()
        # facts exposed for the cross-file rule (SL015)
        self.has_shutdown_hook = bool(_SHUTDOWN_RE.search(self.code))
        self.disable_with_cleanup: List[Block] = []
        # ranges used by several rules
        self.handlers: List[Handler] = []
        self.update_ranges: List[Range] = []
        self.coroutine_ranges: List[Range] = []
        self.loops: List[Tuple[str, str, int, int]] = []  # kind, header expr, body start, body end
        self.player_loop_ranges: List[Range] = []
        self.any_loop_ranges: List[Range] = []
        self.frame_ranges: List[Range] = []   # per-frame paths without a throttle guard
        self.offthread_ranges: List[Range] = []
        self.command_ranges: List[Range] = []         # ICommand.Execute and friends (main thread)
        self.main_callback_ranges: List[Range] = []   # SlClock / MainThread.Post / Timing.CallDelayed callbacks
        self.main_callback_labels: Dict[Range, str] = {}

    # ---- helpers -----------------------------------------------------------------

    def line_col(self, pos: int) -> Tuple[int, int]:
        idx = bisect.bisect_right(self.line_starts, pos) - 1
        return idx + 1, pos - self.line_starts[idx] + 1

    def _parse_suppressions(self) -> None:
        lines = self.raw.split("\n")
        for idx, text in enumerate(lines, start=1):
            m = _SUPPRESS_RE.search(text)
            if not m:
                continue
            ids = set(re.findall(r"SL\d{3}", m.group(2)))
            if m.group(1) == "ignore-file":
                self._suppress_file |= ids or {"*"}
                continue
            targets = {idx}
            if text.strip().startswith("//"):
                targets.add(idx + 1)
            for t in targets:
                self._suppress_line.setdefault(t, set()).update(ids or {"*"})

    def add(self, rule: str, pos: int, message: str, each_call: bool = False,
            severity: Optional[str] = None) -> None:
        """Record a finding. By default one finding per (rule, line); `each_call=True` keeps one per
        (rule, line, column) so several offending calls on one line are all reported. `severity`
        overrides the rule's default for findings that are only conditionally true (SL003)."""
        line, col = self.line_col(pos)
        key = (rule, line, col) if each_call else (rule, line)
        if key in self._seen:
            return
        sup = self._suppress_line.get(line, set())
        if rule in sup or "*" in sup or rule in self._suppress_file or "*" in self._suppress_file:
            return
        self._seen.add(key)
        self._seen.add((rule, line))   # SL018 asks "was this line already reported as SL007?"
        self.findings.append(Finding(rule, severity or RULES[rule][0], self.path, line, col, message))

    def methods_named(self, name: str) -> List[Block]:
        return [m for m in self.methods if m.name == name]

    # ---- structure discovery -----------------------------------------------------

    def analyze(self) -> None:
        self._find_loops()
        self._find_handlers()
        self._find_update_and_coroutines()
        self._find_frame_ranges()
        self._find_offthread()
        self._find_commands()
        self._find_main_callbacks()
        self._rule_events()
        self._rule_global_patterns()
        self._rule_context_patterns()
        self._rule_handler_work()
        self._rule_config_vector4()
        self._rule_disable()
        self._rule_harmony()

    def _block_range_at(self, brace_pos: int) -> Optional[Range]:
        for b in self.blocks:
            if b.start == brace_pos and b.kind != "root":
                return (b.start, b.end + 1)
        return None

    def _find_loops(self) -> None:
        code = self.code
        for m in re.finditer(r"(?<![\w.])(for|foreach|while)\s*\(", code):
            open_paren = m.end() - 1
            close = match_paren(code, open_paren)
            if close < 0:
                continue
            j = close + 1
            while j < len(code) and code[j] in " \t\r\n":
                j += 1
            if j >= len(code) or code[j] == ";":
                continue  # `} while (x);` of a do-loop, or an empty body
            if code[j] == "{":
                rng = self._block_range_at(j)
                if rng is None:
                    continue
                body = rng
            else:
                end = statement_end(code, j)
                body = (j, end + 1)
            header = code[open_paren + 1:close]
            self.loops.append((m.group(1), header, body[0], body[1]))
        for m in re.finditer(r"(?<![\w.])do\s*\{", code):
            rng = self._block_range_at(m.end() - 1)
            if rng:
                self.loops.append(("do", "", rng[0], rng[1]))
        for kind, header, a, b in self.loops:
            self.any_loop_ranges.append((a, b))
            if kind == "foreach":
                fm = re.match(r"\s*(?P<decl>(?:var|[\w.<>\[\]?]+)\s+\w+)\s+in\s+(?P<expr>.*)$", header, re.S)
                if fm:
                    expr = fm.group("expr")
                    decl = fm.group("decl")
                    if _PLAYER_LOOP_EXPR_RE.search(expr) or re.search(r"\b(?:Player|ReferenceHub)\s+\w+\s*$", decl):
                        self.player_loop_ranges.append((a, b))

    def _add_method_handlers(self, event: str, rec: dict, name: str, site: int, kind: str) -> None:
        for mb in self.methods_named(name):
            self.handlers.append(Handler(event, rec, mb.start, mb.end + 1, first_param_name(mb.params), kind, site))

    def _subscription_sites(self) -> Iterable[Tuple[str, int, int]]:
        """Yield (catalog key, start of the statement, index just past `+=`) for every event subscription.

        Three spellings reach the same event: `PlayerEvents.X += h` (also fully qualified), an
        alias (`using PE = LabApi.Events.Handlers.PlayerEvents;` then `PE.X += h`), and a static
        import (`using static LabApi.Events.Handlers.PlayerEvents;` then a bare `X += h`).
        The imports are resolved per file, because they are per-file in C# too.
        """
        cat = self.catalog
        code = self.code
        if not cat.categories:
            return
        cats = set(cat.categories)
        qualifiers: Dict[str, str] = {c: c for c in cat.categories}   # spelling -> category
        static_cats: List[str] = []
        for um in re.finditer(r"\busing\s+static\s+(?:global\s*::\s*)?(?:[A-Za-z_]\w*\s*\.\s*)*([A-Za-z_]\w*)\s*;", code):
            if um.group(1) in cats:
                static_cats.append(um.group(1))
        for um in re.finditer(r"\busing\s+([A-Za-z_]\w*)\s*=\s*(?:global\s*::\s*)?(?:[A-Za-z_]\w*\s*\.\s*)*([A-Za-z_]\w*)\s*;", code):
            if um.group(2) in cats:
                qualifiers[um.group(1)] = um.group(2)
        sub_re = re.compile(
            r"(?<![\w])(?P<q>" + "|".join(map(re.escape, sorted(qualifiers, key=len, reverse=True))) +
            r")\s*\.\s*(?P<ev>[A-Za-z_]\w*)\s*\+=\s*")
        for m in sub_re.finditer(code):
            key = qualifiers[m.group("q")] + "." + m.group("ev")
            if key in cat.events:
                yield key, m.start(), m.end()
        if static_cats:
            bare: Dict[str, str] = {}
            clash = set()
            for c in static_cats:
                for key in cat.events:
                    if key.startswith(c + "."):
                        name = key.split(".", 1)[1]
                        if name in bare and bare[name] != key:
                            clash.add(name)   # two imported classes declare it: C# would not compile a bare use
                        bare[name] = key
            for name in clash:
                bare.pop(name, None)
            if bare:
                # `(?<![\w.])`: a bare name only, so `this.Joined +=` and `other.Joined +=` are not events.
                bare_re = re.compile(r"(?<![\w.])(?P<ev>" + "|".join(map(re.escape, sorted(bare, key=len, reverse=True))) +
                                     r")\s*\+=\s*")
                for m in bare_re.finditer(code):
                    yield bare[m.group("ev")], m.start(), m.end()

    def _find_handlers(self) -> None:
        cat = self.catalog
        code = self.code
        if cat.categories:
            for key, site, rhs_start in self._subscription_sites():
                rec = cat.events[key]
                end = statement_end(code, rhs_start)
                rhs = code[rhs_start:end]
                lm = _LAMBDA_RE.match(rhs)
                dm = _DELEGATE_RE.match(rhs) if not lm else None
                if lm:
                    ps = lm.group("ps")
                    param = lm.group("p1") or first_param_name(ps)
                    self.handlers.append(Handler(key, rec, rhs_start + lm.end(), end, param, "static", site))
                elif dm:
                    self.handlers.append(Handler(key, rec, rhs_start + dm.end(), end,
                                                 first_param_name(dm.group("ps")), "static", site))
                else:
                    gm = _NEW_HANDLER_RE.match(rhs) or _METHOD_GROUP_RE.match(rhs)
                    if gm:
                        name = gm.group("n").split(".")[-1]
                        before = len(self.handlers)
                        self._add_method_handlers(key, rec, name, site, "static")
                        if len(self.handlers) == before:
                            # handler defined elsewhere: still counts as a subscription site
                            self.handlers.append(Handler(key, rec, site, site, None, "static", site))
                    else:
                        self.handlers.append(Handler(key, rec, site, site, None, "static", site))
        for mb in self.methods:
            if mb.params is None or not re.search(r"\boverride\b", mb.header):
                continue
            key = cat.override_map.get(mb.name)
            if key is None:
                continue
            self.handlers.append(Handler(key, cat.events[key], mb.start, mb.end + 1,
                                         first_param_name(mb.params), "override", mb.name_pos))

    def _find_update_and_coroutines(self) -> None:
        for mb in self.methods:
            if mb.name in UPDATE_NAMES and mb.params is not None and not mb.params.strip() \
                    and mb.ret.split()[-1:] == ["void"]:
                self.update_ranges.append((mb.start, mb.end + 1))
            if "IEnumerator" in mb.ret:
                self.coroutine_ranges.append((mb.start, mb.end + 1))

    def _find_frame_ranges(self) -> None:
        candidates: List[Range] = list(self.update_ranges)
        for kind, _h, a, b in self.loops:
            if _YIELD_FRAME_RE.search(self.code, a, b):
                candidates.append((a, b))
        for a, b in candidates:
            if not _THROTTLE_RE.search(self.code, a, b):
                self.frame_ranges.append((a, b))

    def _find_offthread(self) -> None:
        code = self.code
        raw_ranges: List[Range] = []
        for m in _OFFTHREAD_CALL_RE.finditer(code):
            open_paren = m.end() - 1
            close = match_paren(code, open_paren)
            if close < 0:
                continue
            args = split_args(code[open_paren + 1:close])
            is_try_run = m.group("fn").startswith("Background")
            for idx, (off, text) in enumerate(args):
                if is_try_run and idx > 0:
                    break  # TryRun(work, onMainThread, ...): only `work` runs on the worker
                a = open_paren + 1 + off
                b = a + len(text)
                if "=>" in text or re.match(r"\s*delegate\b", text):
                    raw_ranges.append((a, b))
                    continue
                gm = _NEW_HANDLER_RE.match(text) or _METHOD_GROUP_RE.match(text)
                if gm:
                    for mb in self.methods_named(gm.group("n").split(".")[-1]):
                        raw_ranges.append((mb.start, mb.end + 1))
        holes: List[Range] = []
        for m in _SAFE_RETURN_CALL_RE.finditer(code):
            open_paren = m.end() - 1
            close = match_paren(code, open_paren)
            if close > 0:
                holes.append((open_paren, close))
        self.offthread_ranges = subtract_ranges(raw_ranges, holes)

    def _find_commands(self) -> None:
        """Console / Remote Admin commands run on the main thread: `ICommand.Execute`,
        `ParentCommand.ExecuteParent` and any `Execute*` method of a command class."""
        for mb in self.methods:
            if not mb.name.startswith("Execute") or mb.params is None:
                continue
            owner = mb.parent
            while owner is not None and owner.kind != "type":
                owner = owner.parent
            in_command_class = owner is not None and _COMMAND_TYPE_RE.search(owner.header) is not None
            if in_command_class or "ICommandSender" in mb.params:
                self.command_ranges.append((mb.start, mb.end + 1))

    def _find_main_callbacks(self) -> None:
        """Lambdas and method groups handed to a scheduler that invokes them on the main thread."""
        code = self.code
        for m in _MAIN_CALLBACK_CALL_RE.finditer(code):
            open_paren = m.end() - 1
            close = match_paren(code, open_paren)
            if close < 0:
                continue
            fn = m.group("fn")
            fn_label = re.sub(r"\s+", "", fn)
            for idx, (off, text) in enumerate(split_args(code[open_paren + 1:close])):
                if fn.startswith("Background") and idx == 0:
                    continue  # TryRun(work, onMainThread, onError): `work` is the off-thread part
                a = open_paren + 1 + off
                if "=>" in text or re.match(r"\s*delegate\b", text):
                    self.main_callback_ranges.append((a, a + len(text)))
                    self.main_callback_labels[(a, a + len(text))] = f"a {fn_label} callback"
                    continue
                gm = _NEW_HANDLER_RE.match(text) or _METHOD_GROUP_RE.match(text)
                if gm:
                    for mb in self.methods_named(gm.group("n").split(".")[-1]):
                        self.main_callback_ranges.append((mb.start, mb.end + 1))
                        self.main_callback_labels[(mb.start, mb.end + 1)] = f"{mb.name} (a {fn_label} callback)"

    # ---- rules -------------------------------------------------------------------

    def _rule_events(self) -> None:
        cat = self.catalog
        for h in self.handlers:
            rec = h.rec
            how = "override" if h.kind == "override" else "subscription"
            if Catalog.is_hot(rec):
                self.add("SL001", h.site,
                         f"{how} to hot event {h.event} [{cat.freq_text(rec)}]: the handler runs at that rate on "
                         f"the main thread. The subscription itself measured cheap; per-raise work dominates "
                         f"(see tools/hot-bench/README.md), so keep the "
                         f"handler trivial and gated: one early-return flag test, no "
                         f"allocation or logging, unsubscribe when idle. Per-raise work is reported as SL018. "
                         f"(CustomEventsHandler overrides subscribe automatically.)")
            elif Catalog.is_conditionally_hot(rec):
                cond = summarize_condition(rec.get("hotConditions"))
                self.add("SL001", h.site,
                         f"{how} to conditionally hot event {h.event} [{cat.freq_text(rec)}]: normally cheap, "
                         f"but raised per frame in some situations. Catalog hotConditions: "
                         f"{cond or 'see catalog'}. Keep the handler trivial and gated: one early-return "
                         f"flag test, no allocation or logging. Per-raise work is reported as SL018.")
            if rec.get("frequency") == "never-fired":
                self.add("SL002", h.site,
                         f"{h.event} is never raised in game {cat.game} / LabAPI {cat.labapi} "
                         f"(no call site in the build): this handler will never run")
            if h.end > h.start:
                self._check_ignored_setters(h)

    def _check_ignored_setters(self, h: Handler) -> None:
        always = [(nm, None) for nm in Catalog.always_ignored_setters(h.rec)]
        conditional = [(nm, when) for nm, when in Catalog.conditionally_ignored_setters(h.rec)]
        if not always and not conditional:
            return
        params = [h.param] if h.param and h.param != "_" else ["ev", "e", "args", "eventArgs"]
        for nm, when in always + conditional:
            for p in params:
                rx = re.compile(r"(?<![\w.])" + re.escape(p) + r"\s*\.\s*" + re.escape(nm) +
                                r"\s*(?:=(?!=)|\+=|-=|\*=|/=|\?\?=)")
                for m in rx.finditer(self.code, h.start, h.end):
                    if when is None:
                        self.add("SL003", m.start(),
                                 f"assignment to {p}.{nm} in the {h.event} handler is ignored by the game "
                                 f"(catalog ignoredSetters): mutate the existing object in place, "
                                 f"or cancel with IsAllowed = false")
                    else:
                        # Only sometimes ignored, so this is a hint to check the condition, not a defect.
                        cond = when.rstrip(". ")
                        self.add("SL003", m.start(),
                                 f"assignment to {p}.{nm} in the {h.event} handler is ignored when {cond} "
                                 f"(catalog conditionallyIgnoredSetters): check that your handler cannot "
                                 f"hit that case, or do not rely on this assignment there",
                                 severity="info")

    def _handlers_at(self, pos: int) -> List[Handler]:
        return [h for h in self.handlers if h.start <= pos < h.end]

    @staticmethod
    def _blocks_on_purpose(h: Handler) -> bool:
        """ServerEvents.Shutdown handlers flush synchronously on purpose (SL015 asks for exactly that: the
        process is about to quit, there is no frame left to stall), so blocking IO there is not SL007."""
        return h.event == "ServerEvents.Shutdown"

    def _main_thread_ranges(self) -> List[Range]:
        rs = [(h.start, h.end) for h in self.handlers if h.end > h.start and not self._blocks_on_purpose(h)]
        rs += self.update_ranges + self.coroutine_ranges + self.command_ranges + self.main_callback_ranges
        return subtract_ranges(rs, self.offthread_ranges)

    def _rule_global_patterns(self) -> None:
        code = self.code
        for m in re.finditer(r"(?:(?<![\w.])|(?<=Wrappers\.))Server\s*\.\s*Tps\b", code):
            self.add("SL004", m.start(),
                     "Server.Tps is round(1/Time.smoothDeltaTime): smoothed, clamped and scaled by timeScale, so "
                     "it hides hitches and is meaningless in idle mode; measure frame time with a Stopwatch "
                     "PlayerLoop probe (FrameProfiler) or read NetworkServer.actualTickRate")
        for m in re.finditer(r"\b(?:ExecuteCommand|RunCommand|ProcessCommand|ExecuteConsoleCommand)\s*\(\s*\$?@?\"\s*tps\b",
                             self.keep):
            self.add("SL004", m.start(),
                     "the 'tps' command prints round(1/Time.smoothDeltaTime), the same smoothed value as "
                     "Server.Tps; do not parse it for decisions or alerts")
        for m in re.finditer(r"\bTiming\s*\.\s*(?:Instance\s*\.\s*)?KillCoroutines\s*\(\s*\)", code):
            self.add("SL006", m.start(),
                     "Timing.KillCoroutines() with no arguments kills every coroutine in the process, other "
                     "plugins' and the game's included; kill by CoroutineHandle or a plugin-unique tag")
        for m in re.finditer(r"\bFirstChanceException\s*\+=", code):
            self.add("SL009", m.start(),
                     "a FirstChanceException handler runs for every exception thrown anywhere in the process, "
                     "game-internal first-chance ones included; handle exceptions where they are thrown instead")
        for m in re.finditer(r"(?<![\w])(?:System\s*\.\s*)?GC\s*\.\s*Collect\s*\(", code):
            self.add("SL010", m.start(),
                     "GC.Collect() forces a full collection on the Boehm GC (mark cost scales with the live heap) "
                     "and stalls the main thread; reduce allocations instead")
        for m in re.finditer(r"(?:GetExecutingAssembly\s*\(\s*\)|GetType\s*\(\s*\)\s*\.\s*Assembly)\s*\.\s*(?:Location|CodeBase)\b",
                             code):
            self._sl013(m.start())
        type_names = {b.name for b in self.blocks if b.kind == "type"}
        for m in re.finditer(r"typeof\s*\(\s*(?P<t>[\w.]+)\s*\)\s*\.\s*Assembly\s*\.\s*(?:Location|CodeBase)\b", code):
            if m.group("t").split(".")[-1] in type_names:
                self._sl013(m.start())

    _IO_CALLEE_RE = re.compile(r"^(?:System\s*\.\s*IO\s*\.\s*)?(?:Path|File|Directory|FileInfo|DirectoryInfo)\b")

    def _sl013_is_handled(self, pos: int) -> bool:
        """True when this Location read is demonstrably safe or its safety is somebody else's contract.

        Reading Assembly.Location is harmless (it returns ""); what throws is feeding the empty string to
        Path.* / File.*. So a read is NOT reported when (a) it is stored in a local that the file tests with
        string.IsNullOrEmpty/IsNullOrWhiteSpace (the guarded idiom a calibrated plugin used), or (b) it is
        passed straight to a non-System.IO method, whose contract we cannot see from here.
        """
        code = self.code
        start = pos
        while start > 0 and (code[start - 1].isalnum() or code[start - 1] in "_."):
            start -= 1
        before = code[max(0, start - 240):start]
        for stop in (";", "{", "}"):
            cut = before.rfind(stop)
            if cut >= 0:
                before = before[cut + 1:]
        assign = re.search(r"(\w+)\s*=\s*$", before)
        if assign:
            name = re.escape(assign.group(1))
            return re.search(r"(?:IsNullOrEmpty|IsNullOrWhiteSpace)\s*\(\s*" + name + r"\s*\)", code) is not None
        call = re.search(r"([\w.]+)\s*\((?:[^();{}]*,)?\s*$", before)
        if call:
            return self._IO_CALLEE_RE.match(call.group(1)) is None
        return False

    def _sl013(self, pos: int) -> None:
        if self._sl013_is_handled(pos):
            return
        self.add("SL013", pos,
                 "plugin assemblies are loaded with Assembly.Load(byte[]), so Assembly.Location is empty "
                 "(Path.GetDirectoryName then throws 'Invalid path'); use Plugin.FilePath or the config "
                 "directory helpers, and read it in Enable(), not the constructor")

    def _in_frame_path(self, pos: int) -> bool:
        return in_any(pos, self.frame_ranges)

    def _rule_context_patterns(self) -> None:
        code = self.code
        # SL005
        for m in _TIMING_SPAWN_RE.finditer(code):
            pos = m.start()
            # Handlers of hot / conditionally hot / burst events get this finding as SL018 / SL019 instead.
            handlers = [h for h in self._handlers_at(pos)
                        if Catalog.is_hottish(h.rec) and Catalog.work_rule(h.rec) is None]
            if handlers:
                h = handlers[0]
                self.add("SL005", pos,
                         f"{m.group(0).split('(')[0].strip()} inside the {h.event} handler "
                         f"[{h.rec.get('frequency')}] starts one coroutine/timer per event; prefer one clock "
                         f"that walks a data structure (SlClock.Every)")
            elif in_any(pos, self.player_loop_ranges):
                self.add("SL005", pos,
                         f"{m.group(0).split('(')[0].strip()} inside a loop over players starts one "
                         f"coroutine/timer per player; prefer one clock (SlClock.EveryStaggered)")
        # SL007
        main_ranges = self._main_thread_ranges()
        helpers = self._reached_helpers()
        task_names = set(re.findall(r"\bTask(?:<[^>;()]*>)?\s+(\w+)\s*[=;]", code))
        task_names |= set(re.findall(r"\bvar\s+(\w+)\s*=\s*[^;]*Async\s*\(", code))
        for rx, label in _BLOCKING_RES:
            for m in rx.finditer(code):
                self._sl007_at(m.start(), label, main_ranges, helpers)
        for m in _RESULT_RE.finditer(code):
            recv = m.group("recv")
            if recv[-1] in ")]" or recv in task_names or _TASK_NAME_HINT_RE.search(recv):
                self._sl007_at(m.start(), ".Result", main_ranges, helpers)
        # SL008
        for m in _LOG_RE.finditer(code):
            pos = m.start()
            level = m.group(1)
            cheap = level in ("Debug", "Info", "Raw")
            reason = None
            for h in self._handlers_at(pos):
                if cheap and Catalog.is_hottish(h.rec) and Catalog.work_rule(h.rec) is None:
                    # (a hot / conditionally hot / burst handler reports logging as SL018 / SL019)
                    reason = f"inside the {h.event} handler [{h.rec.get('frequency')}]"
                    break
            if reason is None and in_any(pos, self.frame_ranges):
                reason = "in a per-frame path"
            if reason is None and in_any(pos, self.player_loop_ranges):
                reason = "inside a loop over players"
            if reason is None and cheap and in_any(pos, self.any_loop_ranges):
                reason = "inside a loop"
            if reason:
                self.add("SL008", pos,
                         f"Logger.{level} {reason}: Logger has no level gate and does a stack walk + two regex "
                         f"passes per call; guard with your own flag or log a rate-limited summary")
        # SL011
        for m in _PLAYER_ENUM_RE.finditer(code):
            pos = m.start()
            if not self._in_frame_path(pos):
                continue
            if _COUNT_AFTER_RE.match(code, m.end()):
                continue
            self.add("SL011", pos,
                     "player list walked on every frame: an O(N) pass per frame (ReadyList also allocates a LINQ "
                     "iterator per call); poll at 1-5 Hz (SlClock.EveryStaggered) or keep your own set updated "
                     "from Joined/Left")
        # SL012
        for m in _HINT_RE.finditer(code):
            if self._in_frame_path(m.start()):
                self.add("SL012", m.start(),
                         "hint/broadcast sent every frame: each call is a network message per player; make it "
                         "change-driven (send when the text changes) or use client-side hint parameters")
        # SL016
        for a, b in self.offthread_ranges:
            for rx in _TOUCH_RES:
                for m in rx.finditer(code, a, b):
                    self.add("SL016", m.start(),
                             "Unity/Mirror/LabAPI member touched inside Task.Run/Thread/ThreadPool code: none of "
                             "it is thread-safe; copy plain data on the main thread and return results with "
                             "MainThread.Post / MainThreadDispatcher.Dispatch")

    def _new_allocations(self, a: int, b: int) -> List[int]:
        """Positions of `new T(...)`, `new T[n]`, `new T { }`, `new[] { }` and `new { }` in [a, b) that
        allocate on the heap. Value-type constructors, `throw new` (reported as a throw) and target-typed
        `new()` are skipped."""
        code = self.code
        hits: List[int] = []
        for m in _WORK_NEW_RE.finditer(code, a, b):
            i = m.end()
            if i >= b:
                continue
            if re.search(r"\bthrow\s*$", code[max(a, m.start() - 12):m.start()]):
                continue
            c = code[i]
            if c in "[{":
                hits.append(m.start())      # new[] {..}, new {..} (anonymous type)
                continue
            tm = re.match(r"[A-Za-z_][\w.]*", code[i:b])
            if not tm:
                continue                    # `new()`: target-typed, type unknown
            name = tm.group(0).split(".")[-1]
            j = i + tm.end()
            if j < b and code[j] == "<":   # generic arguments: skip to the matching '>'
                depth = 0
                while j < b:
                    if code[j] == "<":
                        depth += 1
                    elif code[j] == ">":
                        depth -= 1
                        if depth == 0:
                            j += 1
                            break
                    j += 1
            while j < b and code[j] in " \t\r\n":
                j += 1
            if j >= b or code[j] not in "([{":
                continue                    # `new void Foo()` member modifier, etc.
            if code[j] != "[" and name in _VALUE_TYPES:
                continue
            hits.append(m.start())
        return hits

    def _rule_handler_work(self) -> None:
        """SL018 / SL019: per-raise work in a handler of a hot, conditionally hot or burst event.

        The hot-bench measurement (tools/hot-bench/README.md) puts the subscription itself as cheap and
        the work done on every raise as the cost: what costs is what the handler does on every raise. So the
        rule looks for the work, not for the subscription. One finding per line and rule, naming
        everything found on that line."""
        code = self.code
        # The failure path is not per-raise work: a `catch` body runs only when the handler threw (and
        # the skills ask for a rate-limited log there). A lazy `if (!map.TryGetValue(..)) map[k] = new T()`
        # allocates once per key, not once per raise.
        catch_ranges = [(b.start, b.end + 1) for b in self.blocks
                        if b.kind == "other" and re.match(r"\s*catch\b", b.header)]
        lazy_blocks = [b for b in self.blocks
                       if b.kind == "other" and _LAZY_INIT_RE.match(b.header)]
        scopes: Dict[Range, Handler] = {}
        for h in self.handlers:
            if h.end <= h.start or Catalog.work_rule(h.rec) is None:
                continue
            key = (h.start, h.end)
            prev = scopes.get(key)
            # a method subscribed to several events: the strictest class decides
            if prev is None or (Catalog.work_rule(h.rec) == "SL018" and Catalog.work_rule(prev.rec) != "SL018"):
                scopes[key] = h
        for (a, b), h in scopes.items():
            rule = Catalog.work_rule(h.rec)
            hits: Dict[int, Tuple[int, List[str]]] = {}   # line -> (first pos, labels)

            def note(pos: int, label: str) -> None:
                if in_any(pos, catch_ranges):
                    return
                if label == "allocation with new" and any(lb.start < pos < lb.end for lb in lazy_blocks):
                    return
                line = self.line_col(pos)[0]
                cur = hits.get(line)
                if cur is None:
                    hits[line] = (pos, [label])
                elif label not in cur[1]:
                    cur[1].append(label)
                    hits[line] = (min(cur[0], pos), cur[1])

            for m in _LOG_RE.finditer(code, a, b):
                note(m.start(), f"Logger.{m.group(1)}")
            for rx, label in _WORK_STRING_RES:
                for m in rx.finditer(code, a, b):
                    note(m.start(), label)
            for m in _WORK_LINQ_RE.finditer(code, a, b):
                if m.group("recv") in _LINQ_NOT_RECEIVERS:
                    continue
                note(m.start(), "LINQ ." + m.group("fn") + "()")
            for pos in self._new_allocations(a, b):
                note(pos, "allocation with new")
            for m in _WORK_LOCK_RE.finditer(code, a, b):
                note(m.start(), "lock / Monitor")
            for m in _PLAYER_ENUM_RE.finditer(code, a, b):
                text = m.group(0)
                if "ReadyList" not in text and "GetAll" not in text and _COUNT_AFTER_RE.match(code, m.end()):
                    continue   # Player.List.Count / AllHubs.Count are O(1)
                note(m.start(), "player list enumeration")
            for m in re.finditer(r"(?<![\w.])Player\s*\.\s*Count\b(?!\s*\()", code[a:b]):
                note(a + m.start(), "Player.Count (a LINQ count over ReadyList)")
            for m in _WORK_THROW_RE.finditer(code, a, b):
                note(m.start(), "throw")
            for m in _TIMING_SPAWN_RE.finditer(code, a, b):
                note(m.start(), m.group(0).split("(")[0].strip())
            for rx, label in _WORK_IO_RES:
                for m in rx.finditer(code, a, b):
                    if ("SL007", self.line_col(m.start())[0]) in self._seen:
                        continue   # already reported as a blocking call
                    note(m.start(), label)

            cls = "hot" if Catalog.is_hot(h.rec) else ("conditionally hot" if Catalog.is_conditionally_hot(h.rec) else "burst")
            for _line, (pos, labels) in sorted(hits.items()):
                what = ", ".join(labels)
                if rule == "SL018":
                    self.add("SL018", pos,
                             f"{what} in the {h.event} handler ({cls}, {h.rec.get('frequency')}): this runs on every "
                             f"raise on the main thread, so its cost is multiplied by the event rate. The "
                             f"subscription is cheap, the per-raise work is the cost: keep the handler to a flag "
                             f"test and a dictionary read, and do the work from a clock (SlClock.Every) on data "
                             f"the handler collected")
                else:
                    self.add("SL019", pos,
                             f"{what} in the {h.event} handler (burst, per shot / per hit): a firefight raises it "
                             f"many times per second, so per-raise work adds up exactly when the server is busiest; "
                             f"keep the handler allocation-free and early-out, move formatting and logging out of it")

    def _sl007_at(self, pos: int, label: str, main_ranges: List[Range],
                  helpers: List[Tuple[int, int, List[str]]]) -> None:
        """Report a blocking call at `pos` when it sits in a main-thread context, either directly or in a
        same-file helper that a main-thread context reaches (`helpers`: range + call path)."""
        if in_any(pos, main_ranges):
            self._sl007(pos, label, None)
            return
        for a, b, path in helpers:
            if a <= pos < b:
                self._sl007(pos, label, path)
                return

    def _sl007(self, pos: int, label: str, path: Optional[List[str]]) -> None:
        if path is None:
            where = "event handler, command, SlClock / MainThread.Post / Timing callback, per-frame or coroutine path"
        else:
            chain = f" ({' -> '.join(path)})" if len(path) > 2 else ""
            where = f"{path[-1]}() is reached from {path[0]}{chain}, a main-thread context"
        self.add("SL007", pos,
                 f"{label} blocks the main thread ({where}): a stalled frame stalls every player; use an "
                 f"async call with ConfigureAwait(false) or Background.TryRun and post the result back",
                 each_call=True)

    # ---- same-file call propagation for SL007 -------------------------------------

    def _type_at(self, pos: int) -> Optional[Block]:
        best: Optional[Block] = None
        for b in self.blocks:
            if b.kind == "type" and b.start <= pos <= b.end and (best is None or b.start > best.start):
                best = b
        return best

    def _main_seeds(self) -> List[Tuple[Range, str]]:
        """Main-thread contexts with a human label, the roots of the propagation."""
        seeds: List[Tuple[Range, str]] = []
        by_start = {mb.start: mb for mb in self.methods}
        for h in self.handlers:
            if h.end > h.start and not self._blocks_on_purpose(h):
                mb = by_start.get(h.start)
                seeds.append(((h.start, h.end), mb.name if mb else f"the {h.event} handler"))
        for rs in (self.update_ranges, self.coroutine_ranges, self.command_ranges):
            for a, b in rs:
                mb = by_start.get(a)
                seeds.append(((a, b), mb.name if mb else "a main-thread method"))
        for rng, label in self.main_callback_labels.items():
            seeds.append((rng, label))
        return seeds

    def _reached_helpers(self) -> List[Tuple[int, int, List[str]]]:
        """Same-file methods called from a main-thread context, directly or through up to
        MAX_PROPAGATION_DEPTH levels of same-file calls: they run on the main thread too.

        Only calls that resolve with confidence are followed, to keep false positives low: a bare
        `Name(...)` or `this.Name(...)` resolves to a method of the calling type (or an enclosing type),
        `Type.Name(...)` to a method of a type declared in this file; `other.Name(...)` is not followed.
        A call inside a Task.Run / new Thread / Background.TryRun work lambda is not followed, and a
        method that is itself handed to one of those is never a main-thread helper.
        Returns (start, end, path) with path = [seed label, helper, ...]."""
        code = self.code
        if not self.methods:
            return []
        offthread = self.offthread_ranges
        types = {t.name: t for t in self.blocks if t.kind == "type" and t.name}
        name_re = re.compile(
            r"(?:(?<![\w.])|(?<=this\.))(?P<q>(?:[A-Za-z_]\w*\s*\.\s*)?)(?P<n>[A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\(")
        names = {m.name for m in self.methods}
        found: Dict[int, Tuple[Block, List[str]]] = {}   # method start -> (method, path)
        todo: List[Tuple[Range, Optional[Block], List[str]]] = []
        for rng, label in self._main_seeds():
            todo.append((rng, self._type_at(rng[0]), [label]))
        while todo:
            (a, b), owner, path = todo.pop(0)
            if len(path) - 1 >= MAX_PROPAGATION_DEPTH:
                continue
            for m in name_re.finditer(code, a, b):
                name = m.group("n")
                if name not in names or name in CONTROL_KEYWORDS or in_any(m.start(), offthread):
                    continue
                before = code[max(a, m.start() - 24):m.start()]
                prev_word = re.search(r"([A-Za-z_]\w*)\s+$", before)
                if prev_word and prev_word.group(1) not in CONTROL_KEYWORDS:
                    continue    # `void Local(` / `int Count(`: a declaration, not a call
                qual = re.sub(r"\s+", "", m.group("q")).rstrip(".")
                if qual == "this":
                    qual = ""
                if qual and qual in types:
                    targets = [mb for mb in self.methods_named(name) if self._type_at(mb.start) is types[qual]]
                elif qual:
                    continue    # some other receiver: its type is unknown
                else:
                    targets = []
                    t = owner
                    while t is not None and not targets:
                        targets = [mb for mb in self.methods_named(name) if self._type_at(mb.start) is t]
                        t = t.parent
                        while t is not None and t.kind != "type":
                            t = t.parent
                for mb in targets:
                    if mb.start in found or (mb.start, mb.end + 1) in offthread:
                        continue
                    found[mb.start] = (mb, path + [mb.name])
                    todo.append(((mb.start, mb.end + 1), self._type_at(mb.start), path + [mb.name]))
        out: List[Tuple[int, int, List[str]]] = []
        for mb, path in found.values():
            for a, b in subtract_ranges([(mb.start, mb.end + 1)], offthread):
                out.append((a, b, path))
        return out

    def _rule_config_vector4(self) -> None:
        def config_like(t: Block) -> bool:
            if _CONFIG_NAME_RE.search(t.name):
                return True
            if re.search(r"\bIConfig\b", t.header):
                return True
            if re.search(r"\bPlugin\s*<\s*" + re.escape(t.name) + r"\s*>", self.code):
                return True
            return any(c.kind == "property" and re.search(r"\b(?:Description|YamlMember)\b", c.header)
                       for c in t.children)

        for b in self.blocks:
            if b.kind != "property" or b.parent is None:
                continue
            head = _blank_attrs(b.header)
            if not re.search(r"\bVector4\b", head) or "(" in head or "public" not in head:
                continue
            if "YamlIgnore" in b.header:
                continue
            anc = b.parent
            while anc is not None and anc.kind == "type":
                if config_like(anc):
                    self.add("SL014", b.header_start + max(0, b.header.find("Vector4")),
                             "LabAPI 1.1.7 YAML writer emits Vector4.w from z, so the value is corrupted on the "
                             "first config save; use Vector3, Color, Color32 or a DTO with four floats")
                    break
                anc = anc.parent

    def _rule_disable(self) -> None:
        for mb in self.methods:
            if mb.name in ("Disable", "OnDisabled") and re.search(r"\boverride\b", mb.header) \
                    and mb.params is not None and not mb.params.strip():
                if _CLEANUP_RE.search(self.code, mb.start, mb.end + 1):
                    self.disable_with_cleanup.append(mb)

    def _rule_harmony(self) -> None:
        def attr_text(blk: Block) -> str:
            return blk.header_keep

        for mb in self.methods:
            head = mb.header_keep
            ret_is_bool = re.search(r"\bbool\b", mb.ret) is not None
            is_prefix = "HarmonyPrefix" in head or mb.name == "Prefix" or mb.name.endswith("Prefix")
            if not (ret_is_bool and is_prefix):
                continue
            targets = attr_text(mb)
            anc = mb.parent
            while anc is not None:
                if anc.kind == "type":
                    targets += " " + attr_text(anc)
                anc = anc.parent
            hit = _PATCHED_NAMES_RE.search(targets)
            getter = re.search(r"\bCurrentRole\b", targets) and re.search(r"MethodType\s*\.\s*Getter", targets)
            if not (hit or getter):
                continue
            body = self.code[mb.start:mb.end + 1]
            rm = re.search(r"\breturn\s+false\s*;", body)
            if rm:
                name = hit.group(0) if hit else "get_CurrentRole"
                self.add("SL017", mb.start + rm.start(),
                         f"Harmony prefix returning false on {name} skips the original method, silently disabling "
                         f"the behaviour other plugins patched around it; prefer a postfix or a transpiler, or "
                         f"document that your plugin is exclusive")


# --------------------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------------------

SKIP_DIRS = {"bin", "obj", ".git", "node_modules", "__pycache__", ".vs", ".idea"}


def collect_files(paths: Sequence[str]) -> Tuple[List[str], List[str]]:
    files: List[str] = []
    missing: List[str] = []
    for p in paths:
        if os.path.isdir(p):
            for root, dirs, names in os.walk(p):
                dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS)
                for name in sorted(names):
                    if name.endswith(".cs"):
                        files.append(os.path.join(root, name))
        elif os.path.isfile(p):
            files.append(p)
        else:
            missing.append(p)
    return files, missing


def lint_texts(items: Sequence[Tuple[str, str]], catalog: Catalog,
               disabled: Optional[set] = None) -> List[Finding]:
    """Lint (path, source) pairs as one project (SL015 looks across all of them)."""
    analyses = []
    for path, raw in items:
        fa = FileAnalysis(path, raw, catalog)
        fa.analyze()
        analyses.append(fa)
    if not any(fa.has_shutdown_hook for fa in analyses):
        for fa in analyses:
            for mb in fa.disable_with_cleanup:
                fa.add("SL015", mb.name_pos,
                       f"{mb.name}() holds cleanup, but LabAPI and the game never call Plugin.Disable() and no "
                       f"ServerEvents.Shutdown subscription was found: flush/release in ServerEvents.Shutdown "
                       f"(synchronously) and on a timer")
    findings = [f for fa in analyses for f in fa.findings]
    if disabled:
        findings = [f for f in findings if f.rule not in disabled]
    findings.sort(key=lambda f: (f.path, f.line, f.column, f.rule))
    return findings


def lint_paths(paths: Sequence[str], catalog: Catalog, disabled: Optional[set] = None) -> List[Finding]:
    files, _ = collect_files(paths)
    items = []
    for f in files:
        try:
            with open(f, "r", encoding="utf-8-sig", errors="replace") as fh:
                items.append((f, fh.read()))
        except OSError as exc:
            print(f"scpsl-lint: cannot read {f}: {exc}", file=sys.stderr)
    return lint_texts(items, catalog, disabled)


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="scpsl_lint.py", description="Static scanner for SCP:SL LabAPI plugin source (C#).")
    ap.add_argument("paths", nargs="*", help="C# files or directories")
    ap.add_argument("--json", action="store_true", help="emit findings as a JSON array")
    ap.add_argument("--min-severity", choices=["info", "warn", "error"], default="info",
                    help="hide findings below this severity (default: info)")
    ap.add_argument("--catalog", default=default_catalog_path(), help="path to labapi-events.json")
    ap.add_argument("--disable", default="", help="comma-separated rule ids to skip, e.g. SL015,SL017")
    ap.add_argument("--list-rules", action="store_true", help="print the rule table and exit")
    args = ap.parse_args(argv)

    if args.list_rules:
        for rid, (sev, title) in RULES.items():
            print(f"{rid}  {sev:5}  {title}")
        return 0
    if not args.paths:
        ap.error("at least one path is required")
    try:
        catalog = Catalog.load(args.catalog)
    except (OSError, ValueError, KeyError) as exc:
        print(f"scpsl-lint: cannot load event catalog {args.catalog}: {exc}", file=sys.stderr)
        return 2
    files, missing = collect_files(args.paths)
    for p in missing:
        print(f"scpsl-lint: no such file or directory: {p}", file=sys.stderr)
    if missing and not files:
        return 2
    disabled = {t.strip().upper() for t in args.disable.split(",") if t.strip()}
    findings = lint_paths(args.paths, catalog, disabled)
    has_error = any(f.severity == "error" for f in findings)
    shown = [f for f in findings if SEVERITY_ORDER[f.severity] >= SEVERITY_ORDER[args.min_severity]]
    if args.json:
        print(json.dumps([f.to_dict() for f in shown], indent=2))
    else:
        for f in shown:
            print(f.format())
        counts = {s: sum(1 for f in shown if f.severity == s) for s in ("error", "warn", "info")}
        print(f"scpsl-lint: {len(files)} file(s), {counts['error']} error(s), {counts['warn']} warning(s), "
              f"{counts['info']} info", file=sys.stderr)
    if missing:
        return 2 if not has_error else 1
    return 1 if has_error else 0


if __name__ == "__main__":
    sys.exit(main())
