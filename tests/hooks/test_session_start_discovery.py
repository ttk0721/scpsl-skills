#!/usr/bin/env python3
"""Which files the SessionStart hook reads, how deep it looks without git, and what it tells the agent.

Covers the third-round findings: references kept in Directory.Build.props/.targets (H2), the bounded
search outside git (H3), the documented matcher (H4) and the paths in the injected text (H5). The SIGPIPE
regression lives in test_session_start_many_projects.py.

Run: python3 -m unittest discover -s tests/hooks
"""
import json
import os
import shutil
import subprocess
import tempfile
import time
import unittest

HOOK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "hooks", "session-start")
LABAPI_REF = '<Reference Include="LabApi"><HintPath>$(SL_REFERENCES)/LabApi.dll</HintPath></Reference>'
CLEAN_CSPROJ = ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework>'
                '</PropertyGroup></Project>')


def run_hook(cwd, env_extra=None):
    env = dict(os.environ)
    env.pop("SCPSL_SKILLS_ALWAYS", None)
    env.pop("CLAUDE_PLUGIN_ROOT", None)
    env.update(env_extra or {})
    out = subprocess.run(["bash", HOOK], cwd=cwd, env=env, capture_output=True, text=True, timeout=60)
    return out.returncode, out.stdout


def write_file(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)


def props(body):
    return f"<Project><ItemGroup>{body}</ItemGroup></Project>"


def init_repo(directory):
    subprocess.run(["git", "init", "-q", directory], check=True, capture_output=True)


class SharedBuildFilesTest(unittest.TestCase):
    """H2: references kept in Directory.Build.props/.targets or an imported *.props still count."""

    def check(self, layout, fires, env_extra=None):
        modes = [("no git", False)] + ([("git", True)] if shutil.which("git") else [])
        for label, use_git in modes:
            with self.subTest(mode=label), tempfile.TemporaryDirectory() as d:
                if use_git:
                    init_repo(d)
                for rel, text in layout.items():
                    write_file(os.path.join(d, *rel.split("/")), text)
                code, out = run_hook(d, env_extra)
                self.assertEqual(code, 0)
                self.assertEqual("additionalContext" in out, fires, out)

    def test_reference_only_in_directory_build_props(self):
        # The layout of every example in this repository.
        self.check({"Directory.Build.props": props(LABAPI_REF), "Plugin/Plugin.csproj": CLEAN_CSPROJ}, True)

    def test_reference_only_in_directory_build_targets(self):
        self.check({"Directory.Build.targets": props(LABAPI_REF), "Plugin/Plugin.csproj": CLEAN_CSPROJ}, True)

    def test_reference_in_a_props_file_next_to_the_csproj(self):
        self.check({"Plugin/Common.props": props(LABAPI_REF), "Plugin/Plugin.csproj": CLEAN_CSPROJ}, True)

    def test_reference_in_a_shared_targets_file(self):
        self.check({"build/Shared.targets": props(LABAPI_REF), "Plugin/Plugin.csproj": CLEAN_CSPROJ}, True)

    def test_same_exact_markers_in_props(self):
        # Comment stripping and exact names apply to props exactly as to a csproj.
        self.check({"Directory.Build.props": props("<!-- " + LABAPI_REF + " -->")}, False)
        self.check({"Directory.Build.props": props('<PackageReference Include="Acme.PluginApi" />')}, False)
        self.check({"Directory.Build.props": props('<Reference Include="Assembly-CSharp" />')}, False)

    def test_same_exact_markers_in_props_without_python(self):
        env = {"SCPSL_SKILLS_NO_PYTHON": "1"}
        self.check({"Directory.Build.props": props("<!--\n" + LABAPI_REF + "\n-->")}, False, env)
        self.check({"Directory.Build.props": props(LABAPI_REF)}, True, env)

    def test_props_in_build_output_are_ignored(self):
        # obj/ holds generated *.nuget.g.props; a marker there is residue, not a project.
        self.check({"obj/Generated.props": props(LABAPI_REF)}, False)

    def test_a_clean_solution_stays_silent(self):
        self.check({"Directory.Build.props": props('<PackageReference Include="Newtonsoft.Json" />'),
                    "Plugin/Plugin.csproj": CLEAN_CSPROJ}, False)


class AncestorBuildFilesTest(unittest.TestCase):
    """The session may start inside a project folder; MSBuild reads Directory.Build.props from above it."""

    def test_directory_build_props_above_the_working_directory(self):
        modes = [("no git", False)] + ([("git", True)] if shutil.which("git") else [])
        for label, use_git in modes:
            with self.subTest(mode=label), tempfile.TemporaryDirectory() as d:
                if use_git:
                    init_repo(d)
                write_file(os.path.join(d, "examples", "Directory.Build.props"), props(LABAPI_REF))
                write_file(os.path.join(d, "examples", "minimal", "Minimal.csproj"), CLEAN_CSPROJ)
                code, out = run_hook(os.path.join(d, "examples", "minimal"))
                self.assertEqual(code, 0)
                self.assertIn("additionalContext", out)

    @unittest.skipUnless(shutil.which("git"), "git not installed")
    def test_a_git_work_tree_does_not_look_above_its_top_level(self):
        with tempfile.TemporaryDirectory() as outer:
            write_file(os.path.join(outer, "Directory.Build.props"), props(LABAPI_REF))
            repo = os.path.join(outer, "repo")
            os.makedirs(repo)
            init_repo(repo)
            write_file(os.path.join(repo, "App", "App.csproj"), CLEAN_CSPROJ)
            code, out = run_hook(os.path.join(repo, "App"))
            self.assertEqual(out.strip(), "")


class NoGitSearchTest(unittest.TestCase):
    """H3: the search outside git is deeper than before, bounded, pruned, and stops at the first hit."""

    def check(self, relpath, fires, env_extra=None):
        with tempfile.TemporaryDirectory() as d:
            write_file(os.path.join(d, *relpath.split("/")), props('<Reference Include="LabApi" />'))
            code, out = run_hook(d, env_extra)
            self.assertEqual(code, 0)
            self.assertEqual("additionalContext" in out, fires, f"{relpath}: {out[:80]}")

    def test_finds_a_project_the_old_depth_cap_missed(self):
        # tools/SlKit/examples/QuickStart/QuickStart.csproj is five levels down: invisible at depth 4.
        self.check("tools/SlKit/examples/QuickStart/QuickStart.csproj", True)

    def test_finds_projects_down_to_the_depth_cap(self):
        self.check("a/b/c/d/e/f/g/P.csproj", True)  # file at depth 8
        self.check("a/b/c/d/e/f/g/h/P.csproj", False)  # file at depth 9: beyond the cap, documented

    def test_does_not_descend_into_pruned_directories(self):
        for pruned in ("node_modules", "bin", "obj", "packages", ".vs", ".idea", ".hidden", ".git", "Library", "Temp"):
            with self.subTest(pruned=pruned):
                self.check(f"src/{pruned}/Ghost/Ghost.csproj", False)

    def test_a_pruned_name_only_prunes_directories_with_that_exact_name(self):
        self.check("src/binaries/Real/Real.csproj", True)
        self.check("src/objects/Real/Real.csproj", True)

    def test_the_search_stops_at_the_first_hit(self):
        # A fake `find` that prints one matching project and then hangs. A hook that waited for find to
        # finish would sit here until the timeout; one that stops at the first hit returns at once.
        # The project lives OUTSIDE the working directory, so only the fake find can produce the hit and
        # the test cannot pass by accident through the real one.
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryDirectory() as fakebin, \
                tempfile.TemporaryDirectory() as elsewhere:
            project = os.path.join(elsewhere, "Plugin.csproj")
            write_file(project, props('<Reference Include="LabApi" />'))
            fake = os.path.join(fakebin, "find")
            write_file(fake, f"#!/bin/sh\nprintf '%s\\0' '{project}'\nsleep 20\n")
            os.chmod(fake, 0o755)
            started = time.monotonic()
            code, out = run_hook(d, {"PATH": fakebin + os.pathsep + os.environ["PATH"]})
            elapsed = time.monotonic() - started
            self.assertEqual(code, 0)
            self.assertIn("additionalContext", out)
            self.assertLess(elapsed, 4, f"hook waited {elapsed:.1f}s for find")


@unittest.skipUnless(shutil.which("git"), "git not installed")
class FileCapTest(unittest.TestCase):
    """H3: at most SCPSL_SKILLS_MAX_FILES files are read; shared build files are listed first."""

    def make_repo(self, d, marker_in_props):
        init_repo(d)
        for i in range(10):
            write_file(os.path.join(d, f"a{i}", "Other.csproj"), props('<PackageReference Include="Other" />'))
        if marker_in_props:
            write_file(os.path.join(d, "Directory.Build.props"), props(LABAPI_REF))
        else:
            write_file(os.path.join(d, "z", "Last.csproj"), props('<Reference Include="LabApi" />'))

    def test_a_project_past_the_cap_is_not_read(self):
        with tempfile.TemporaryDirectory() as d:
            self.make_repo(d, marker_in_props=False)
            code, out = run_hook(d, {"SCPSL_SKILLS_MAX_FILES": "5"})
            self.assertEqual(out.strip(), "")
            code, out = run_hook(d)
            self.assertIn("additionalContext", out)

    def test_directory_build_props_is_read_before_the_cap_bites(self):
        with tempfile.TemporaryDirectory() as d:
            self.make_repo(d, marker_in_props=True)
            code, out = run_hook(d, {"SCPSL_SKILLS_MAX_FILES": "2"})
            self.assertIn("additionalContext", out)


class InjectedPathsTest(unittest.TestCase):
    """H5: the injected text says where the plugin lives, so the agent can open the files it names."""

    def context(self, env_extra=None):
        with tempfile.TemporaryDirectory() as d:
            code, out = run_hook(d, {"SCPSL_SKILLS_ALWAYS": "1", **(env_extra or {})})
            self.assertEqual(code, 0)
            return json.loads(out)["hookSpecificOutput"]["additionalContext"]

    def test_names_the_plugin_root_the_skills_directory_and_the_catalog(self):
        root = "/opt/plugins/scpsl-skills"
        ctx = self.context({"CLAUDE_PLUGIN_ROOT": root})
        self.assertIn(f"Plugin root: {root}\n", ctx)
        self.assertIn(f"Skills directory: {root}/skills", ctx)
        self.assertIn(f"{root}/skills/labapi-events/events-catalog.md", ctx)

    def test_a_trailing_slash_does_not_double_up(self):
        ctx = self.context({"CLAUDE_PLUGIN_ROOT": "/opt/p/"})
        self.assertIn("Skills directory: /opt/p/skills", ctx)

    def test_paths_with_quotes_backslashes_and_spaces_survive_json(self):
        root = 'C:\\Users\\Some "One"\\scpsl skills'
        self.assertIn(f"Plugin root: {root}\n", self.context({"CLAUDE_PLUGIN_ROOT": root}))

    def test_paths_survive_without_python_too(self):
        root = 'C:\\Users\\Some "One"\\scpsl skills'
        ctx = self.context({"CLAUDE_PLUGIN_ROOT": root, "SCPSL_SKILLS_NO_PYTHON": "1"})
        self.assertIn(f"Plugin root: {root}\n", ctx)

    def test_falls_back_to_the_script_location(self):
        ctx = self.context()
        plugin_root = os.path.realpath(os.path.join(os.path.dirname(HOOK), ".."))
        self.assertIn(f"Plugin root: {plugin_root}\n", ctx)
        self.assertTrue(os.path.isfile(os.path.join(plugin_root, "skills", "labapi-events", "events-catalog.md")))

    def test_the_catalog_line_is_left_out_when_the_file_is_missing(self):
        # A path that does not exist is worse than none.
        with tempfile.TemporaryDirectory() as root:
            os.makedirs(os.path.join(root, "hooks"))
            shutil.copy(HOOK, os.path.join(root, "hooks", "session-start"))
            write_file(os.path.join(root, "skills", "using-scpsl-skills", "SKILL.md"), "index")
            with tempfile.TemporaryDirectory() as cwd:
                env = dict(os.environ, SCPSL_SKILLS_ALWAYS="1")
                env.pop("CLAUDE_PLUGIN_ROOT", None)
                out = subprocess.run(["bash", os.path.join(root, "hooks", "session-start")], cwd=cwd, env=env,
                                     capture_output=True, text=True, timeout=30)
        ctx = json.loads(out.stdout)["hookSpecificOutput"]["additionalContext"]
        self.assertIn("Skills directory:", ctx)
        self.assertNotIn("events-catalog", ctx)


class MatcherDocumentedTest(unittest.TestCase):
    """H4: hooks.json has no resume in its matcher, and the script header says why."""

    def test_matcher_has_no_resume_and_the_reason_is_written_down(self):
        with open(os.path.join(os.path.dirname(HOOK), "hooks.json"), encoding="utf-8") as f:
            matcher = json.load(f)["hooks"]["SessionStart"][0]["matcher"]
        self.assertEqual(sorted(matcher.split("|")), ["clear", "compact", "startup"])
        with open(HOOK, encoding="utf-8") as f:
            header = f.read().split("set -u", 1)[0]
        self.assertIn("resume", header)
        self.assertIn("transcript", header)


if __name__ == "__main__":
    unittest.main()
