#!/usr/bin/env python3
"""Regression for the SIGPIPE bug: the hook went silent in a repository with several SCP:SL projects.

The old detection was `... | grep -E MARKER | head -n 1 | grep -q .` under `set -o pipefail`. Once head
had its line it exited, grep's NEXT write hit a closed pipe, died with 141, and pipefail turned that
into "not an SCP:SL project" even though the marker had matched. Whether grep got that far depended on
timing (a fast machine passed 5/5, the inspector's saw 0/10).

These tests do not depend on timing: every project is padded so the matching output is far larger than a
pipe buffer (64 KiB on Linux). grep cannot finish its output before head has exited, so under the old
code the SIGPIPE was certain, not likely.

Run: python3 -m unittest discover -s tests/hooks
"""
import json
import os
import shutil
import subprocess
import tempfile
import unittest

HOOK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "hooks", "session-start")

# ~14 KiB of harmless PackageReferences: one stripped project is one line this long, so a handful of
# matching projects is already past any pipe buffer.
PADDING = "".join(f'<PackageReference Include="Some.Package.Number{n}" Version="1.0.{n}" />\n' for n in range(220))


def write_scpsl_project(root, index, big=True):
    folder = os.path.join(root, f"Plugin{index}")
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, f"Plugin{index}.csproj"), "w") as f:
        f.write("<Project><ItemGroup>\n" + (PADDING if big else "") +
                '<Reference Include="LabApi" /></ItemGroup></Project>')


def run_hook(cwd, env_extra=None):
    env = dict(os.environ)
    env.pop("SCPSL_SKILLS_ALWAYS", None)
    env.pop("SCPSL_SKILLS_NO_PYTHON", None)
    env.update(env_extra or {})
    out = subprocess.run(["bash", HOOK], cwd=cwd, env=env, capture_output=True, text=True, timeout=120)
    return out


class ManyProjectsTest(unittest.TestCase):
    def check(self, count, env_extra=None, git=False):
        with tempfile.TemporaryDirectory() as d:
            if git:
                subprocess.run(["git", "init", "-q", d], check=True, capture_output=True)
            for i in range(count):
                write_scpsl_project(d, i)
            out = run_hook(d, env_extra)
            self.assertEqual(out.returncode, 0, out.stderr)
            self.assertTrue(out.stdout.strip(), f"{count} matching projects, hook was silent")
            ctx = json.loads(out.stdout)["hookSpecificOutput"]["additionalContext"]
            self.assertIn("using-scpsl-skills", ctx)

    def test_without_git(self):
        for count in (1, 3, 10, 30):
            with self.subTest(count=count):
                self.check(count)

    def test_without_python(self):
        for count in (1, 3, 10, 30):
            with self.subTest(count=count):
                self.check(count, {"SCPSL_SKILLS_NO_PYTHON": "1"})

    @unittest.skipUnless(shutil.which("git"), "git not installed")
    def test_inside_a_git_work_tree(self):
        for count in (1, 3, 10, 30):
            with self.subTest(count=count):
                self.check(count, git=True)

    @unittest.skipUnless(shutil.which("git"), "git not installed")
    def test_inside_a_git_work_tree_without_python(self):
        for count in (1, 3, 10, 30):
            with self.subTest(count=count):
                self.check(count, {"SCPSL_SKILLS_NO_PYTHON": "1"}, git=True)

    def test_small_projects_too(self):
        # The cheap case must keep working as well: no padding, many matches.
        with tempfile.TemporaryDirectory() as d:
            for i in range(30):
                write_scpsl_project(d, i, big=False)
            out = run_hook(d)
            self.assertIn("additionalContext", out.stdout)

    def test_a_late_match_among_many_unrelated_projects(self):
        # The one SCP:SL project is the LAST of many unrelated ones: the scan must reach it.
        with tempfile.TemporaryDirectory() as d:
            for i in range(40):
                folder = os.path.join(d, f"Other{i:02d}")
                os.makedirs(folder)
                with open(os.path.join(folder, f"Other{i:02d}.csproj"), "w") as f:
                    f.write("<Project><ItemGroup>" + PADDING + "</ItemGroup></Project>")
            write_scpsl_project(d, 99)
            out = run_hook(d)
            self.assertIn("additionalContext", out.stdout)

    def test_no_pipefail_in_the_detection_path(self):
        # Guard against the bug coming back through a refactor: a SIGPIPE-sensitive pipeline needs
        # pipefail to bite, so the script must not switch it on.
        with open(HOOK, encoding="utf-8") as f:
            code = [line for line in f.read().splitlines() if not line.lstrip().startswith("#")]
        self.assertFalse([line for line in code if "pipefail" in line], "pipefail is back")


if __name__ == "__main__":
    unittest.main()
