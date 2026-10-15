"""The numbers the docs quote must match the catalog and the build script.

Counts were quoted by hand in several files and went stale one by one (378 events after the catalog
had 377, 241 verified rows after 240, "four lint targets" after a fifth was added). These tests
derive each number from its source and look for it in the README, so a future change to the
catalog or to build-all.py fails here instead of leaving a wrong sentence behind.
"""
import json
import re
import unittest
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOG = json.loads((ROOT / "tools" / "catalog" / "labapi-events.json").read_text(encoding="utf-8"))
README = (ROOT / "README.md").read_text(encoding="utf-8")
EVENTS = CATALOG["events"]
HOT_FREQUENCIES = {"per-frame", "per-frame-per-pair", "high-rate-network"}


def verified(rows):
    return sum(1 for e in rows if e.get("independentlyVerified"))


class CatalogInvariantsTest(unittest.TestCase):
    def test_meta_counts_match_the_rows(self):
        counts = CATALOG["meta"]["counts"]
        self.assertEqual(CATALOG["meta"]["count"], len(EVENTS))
        self.assertEqual(counts["events"], len(EVENTS))
        # meta lists every class, including the ones with no rows ("unknown": 0).
        self.assertEqual({k: v for k, v in counts["byFrequency"].items() if v},
                         dict(Counter(e["frequency"] for e in EVENTS)))
        self.assertEqual(counts["hot"], sum(1 for e in EVENTS if e["hot"]))
        self.assertEqual(counts["burst"], sum(1 for e in EVENTS if e["burst"]))
        self.assertEqual(counts["conditionallyHot"], sum(1 for e in EVENTS if e["conditionallyHot"]))
        self.assertEqual(counts["neverFired"], sum(1 for e in EVENTS if e["frequency"] == "never-fired"))
        self.assertEqual(counts["independentlyVerified"], verified(EVENTS))
        self.assertEqual(counts["independentlyVerifiedPercent"], round(100 * verified(EVENTS) / len(EVENTS)))
        self.assertEqual(counts["withAlwaysIgnoredSetter"], sum(1 for e in EVENTS if e["ignoredSetters"]))
        self.assertEqual(counts["withConditionallyIgnoredSetter"],
                         sum(1 for e in EVENTS if e["conditionallyIgnoredSetters"]))

    def test_hot_is_derived_from_the_frequency(self):
        for e in EVENTS:
            with self.subTest(event=e["event"]):
                self.assertEqual(e["hot"], e["frequency"] in HOT_FREQUENCIES)

    def test_ignored_setters_are_settable_and_never_in_both_lists(self):
        for e in EVENTS:
            with self.subTest(event=e["event"]):
                always = set(e["ignoredSetters"])
                conditional = {c["setter"] for c in e["conditionallyIgnoredSetters"]}
                self.assertFalse(always & conditional)
                self.assertLessEqual(always | conditional, set(e["settable"]))

    def test_pair_is_symmetric(self):
        by_name = {e["event"]: e for e in EVENTS}
        flip = {"before": "after", "after": "before"}
        for e in EVENTS:
            pair = e.get("pair")
            if pair is None:
                continue
            with self.subTest(event=e["event"]):
                other = by_name[pair["event"]].get("pair")
                self.assertIsNotNone(other)
                self.assertEqual(other["event"], e["event"])
                self.assertEqual(other["role"], flip[pair["role"]])


class ReadmeNumbersTest(unittest.TestCase):
    def test_event_count(self):
        n = len(EVENTS)
        self.assertIn(f"catalog of all {n} events", README)
        self.assertIn(f"Event catalog, {n} events", README)

    def test_verification_numbers(self):
        n, v = len(EVENTS), verified(EVENTS)
        self.assertIn(f"**{v} of {n} rows ({round(100 * v / n)}%)**", README)
        self.assertIn(f"the other **{n - v} ({round(100 * (n - v) / n)}%) were checked by one reader only**", README)
        for label, rows in (("hot", [e for e in EVENTS if e["hot"]]),
                            ("burst", [e for e in EVENTS if e["burst"]]),
                            ("cancellable", [e for e in EVENTS if e["cancellable"]])):
            with self.subTest(group=label):
                self.assertIn(f"{verified(rows)} of the {len(rows)} {label}"
                              if label != "hot" else f"all {len(rows)} hot events", " ".join(README.split()))
        always = [e for e in EVENTS if e["ignoredSetters"]]
        cond = [e for e in EVENTS if e["conditionallyIgnoredSetters"]]
        flat = " ".join(README.split())
        self.assertIn(f"{verified(always)} of the {len(always)} events with a setter the game always ignores", flat)
        self.assertIn(f"{verified(cond)} of the {len(cond)} with a setter it ignores only in some situation", flat)
        conf = Counter(e["confidence"] for e in EVENTS)
        self.assertIn(f"`high` for {conf['high']} rows, `medium` for {conf['medium']} and `low` for {conf['low']}", flat)

    def test_flag_counts_in_the_tools_section(self):
        flat = " ".join(README.split())
        self.assertIn(f"`hot` ({sum(1 for e in EVENTS if e['hot'])} events", flat)
        self.assertIn(f"`burst` ({sum(1 for e in EVENTS if e['burst'])} events", flat)
        self.assertIn(f"`conditionallyHot` ({sum(1 for e in EVENTS if e['conditionallyHot'])} events", flat)

    def test_hot_list_in_slkit_has_the_catalog_count(self):
        text = (ROOT / "tools" / "SlKit" / "src" / "Runtime" / "HotEventAudit.HotList.cs").read_text(encoding="utf-8")
        hot = sum(1 for e in EVENTS if e["hot"])
        self.assertIn(f"{hot} of {len(EVENTS)} events", text)

    def test_lint_targets_and_project_count(self):
        build_all = (ROOT / "examples" / "build-all.py").read_text(encoding="utf-8")
        targets = re.search(r"LINT_TARGETS = \[(.*?)\]", build_all).group(1).count('"') // 2
        words = {4: "four", 5: "five", 6: "six"}
        self.assertIn(f"lints {words[targets]} targets separately", " ".join(README.split()))
        projects = [p for top in ("examples", "tools") for p in (ROOT / top).rglob("*.csproj")
                    if not {"bin", "obj"} & set(p.parts)]
        self.assertIn(f"({len(projects)} on this tree", README)


class NoStaleNumbersTest(unittest.TestCase):
    STALE = [r"\b378 events\b", r"\b241 of 378\b", r"\b241 of\b", r"\bthe 31 events with a setter\b",
             r"\bfour targets\b", r"\blints four\b", r"\b16 events\b", r"\(16 on 14\.2\.7\)"]

    def test_old_numbers_are_gone(self):
        files = [ROOT / "README.md", *(ROOT / "docs").glob("*.md"), *(ROOT / "skills").rglob("*.md")]
        for path in files:
            text = path.read_text(encoding="utf-8")
            for rx in self.STALE:
                with self.subTest(file=str(path.relative_to(ROOT)), pattern=rx):
                    self.assertIsNone(re.search(rx, text))


if __name__ == "__main__":
    unittest.main()
