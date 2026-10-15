#!/usr/bin/env python3
"""build-all.py must lint tools/ as well as examples/ (second-round inspector #5).

Run: python3 -m unittest discover -s tests/hooks
"""
import contextlib
import importlib.util
import io
import os
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
BUILD_ALL = os.path.join(ROOT, "examples", "build-all.py")


def load_build_all():
    # The file name has a hyphen, so import by path. Importing runs only definitions; main() is guarded.
    spec = importlib.util.spec_from_file_location("build_all", BUILD_ALL)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def has_csharp(directory):
    for dp, dns, fns in os.walk(directory):
        dns[:] = [d for d in dns if d not in ("bin", "obj")]
        if any(f.endswith(".cs") for f in fns):
            return True
    return False


class LintTargetsTest(unittest.TestCase):
    def test_targets_exist(self):
        mod = load_build_all()
        for target in mod.LINT_TARGETS:
            self.assertTrue(os.path.isdir(os.path.join(ROOT, target)), target)

    def test_examples_and_every_plugin_shaped_tool_is_linted(self):
        # Every directory under tools/ that holds C# is a lint target, except the linter itself: its
        # test fixtures are deliberately bad code. A new tool added later fails this test until it is
        # listed, instead of silently skipping the lint.
        mod = load_build_all()
        targets = set(mod.LINT_TARGETS)
        self.assertIn("examples", targets)
        tools = os.path.join(ROOT, "tools")
        for name in sorted(os.listdir(tools)):
            path = os.path.join(tools, name)
            if os.path.isdir(path) and name != "scpsl-lint" and has_csharp(path):
                self.assertIn(f"tools/{name}", targets, f"tools/{name} holds C# but is not linted")

    def test_the_linter_own_fixtures_are_not_linted(self):
        mod = load_build_all()
        self.assertNotIn("tools/scpsl-lint", mod.LINT_TARGETS)
        self.assertNotIn("tools", mod.LINT_TARGETS)

    def test_summary_pattern_matches_the_linter_stderr_line(self):
        # The linter prints its summary on stderr, after any findings on stdout.
        mod = load_build_all()
        out = "x.cs:1:1: warn SL001: ...\nscpsl-lint: 39 file(s), 0 error(s), 0 warning(s), 0 info\n"
        self.assertEqual(mod.LINT_SUMMARY.search(out).group(0), "scpsl-lint: 39 file(s), 0 error(s), 0 warning(s), 0 info")


class TableTest(unittest.TestCase):
    """Third-round inspector: table() raised TypeError when no row had been recorded yet."""

    def render(self, rows):
        mod = load_build_all()
        mod.rows[:] = rows
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            mod.table()
        return buf.getvalue().splitlines()

    def test_empty_row_list_prints_just_the_header(self):
        lines = self.render([])
        self.assertEqual(len(lines), 2)
        self.assertTrue(lines[0].startswith("KIND"))
        self.assertIn("DETAIL", lines[0])
        self.assertTrue(set(lines[1].replace(" ", "")) <= {"-"})

    def test_rows_widen_the_columns_and_are_labelled(self):
        lines = self.render([("build", True, "examples/Very/Long/Target.csproj", ""), ("lint", False, "examples/", "2 error(s)")])
        self.assertEqual(len(lines), 4)
        self.assertIn("OK", lines[2])
        self.assertIn("FAIL", lines[3])
        # Columns line up: the DETAIL column starts at the same offset on every row.
        self.assertEqual(lines[3].index("2 error(s)"), lines[0].index("DETAIL"))

    def test_a_single_row_works(self):
        # The early-exit path calls table() right after the first (SlKit) build, so the list can be short.
        lines = self.render([("build", False, "tools/SlKit/src/SlKit.csproj", "boom")])
        self.assertEqual(len(lines), 3)
        self.assertIn("FAIL", lines[2])


if __name__ == "__main__":
    unittest.main()
