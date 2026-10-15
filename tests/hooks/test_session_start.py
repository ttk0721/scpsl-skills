#!/usr/bin/env python3
"""The SessionStart hook must inject the index in SCP:SL plugin projects and stay silent elsewhere.

Run: python3 -m unittest discover -s tests/hooks
"""
import json
import os
import shutil
import subprocess
import tempfile
import unittest

HOOK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "hooks", "session-start")


def run_hook(cwd, env_extra=None):
    env = dict(os.environ)
    env.pop("SCPSL_SKILLS_ALWAYS", None)
    env.update(env_extra or {})
    out = subprocess.run(["bash", HOOK], cwd=cwd, env=env, capture_output=True, text=True, timeout=30)
    return out.returncode, out.stdout


def write_csproj(directory, body, name="Plugin.csproj"):
    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, name), "w") as f:
        f.write(f"<Project><ItemGroup>{body}</ItemGroup></Project>")


class SessionStartHookTest(unittest.TestCase):
    def test_injects_index_in_a_labapi_project(self):
        with tempfile.TemporaryDirectory() as d:
            os.makedirs(os.path.join(d, "MyPlugin"))
            with open(os.path.join(d, "MyPlugin", "MyPlugin.csproj"), "w") as f:
                f.write('<Project><ItemGroup><Reference Include="LabApi" /></ItemGroup></Project>')
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            ctx = json.loads(out)["hookSpecificOutput"]["additionalContext"]
            self.assertIn("using-scpsl-skills", ctx)
            self.assertIn("Non-negotiable", ctx)

    def test_silent_in_an_unrelated_project(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "Other.csproj"), "w") as f:
                f.write('<Project><ItemGroup><PackageReference Include="Newtonsoft.Json" /></ItemGroup></Project>')
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            self.assertEqual(out.strip(), "")

    def test_ignores_build_output_directories(self):
        with tempfile.TemporaryDirectory() as d:
            os.makedirs(os.path.join(d, "obj"))
            with open(os.path.join(d, "obj", "Ghost.csproj"), "w") as f:
                f.write("LabApi")
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            self.assertEqual(out.strip(), "")

    def test_silent_for_a_bepinex_style_unity_mod(self):
        # Regression: Assembly-CSharp is referenced by EVERY Unity mod, so it used to trigger the hook.
        with tempfile.TemporaryDirectory() as d:
            write_csproj(d, '<Reference Include="Assembly-CSharp"><HintPath>Game_Data/Managed/Assembly-CSharp.dll</HintPath></Reference>'
                            '<PackageReference Include="BepInEx.Core" Version="5.4.21" />', "Mod.csproj")
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            self.assertEqual(out.strip(), "")

    def test_injects_for_each_scpsl_marker(self):
        # Every shape of a real reference. Matching is on the reference itself, not on a word anywhere
        # in the file, so each of these is an actual <Reference>/<PackageReference>/<HintPath>.
        for marker in ('<Reference Include="LabApi" />',
                       '<Reference Include="LabApi, Version=1.1.7.0, Culture=neutral" />',
                       "<Reference Include='LabApi' />",
                       '<PackageReference Include="Northwood.LabAPI" Version="1.1.7" />',
                       '<PackageReference Version="1.1.7" Include="Northwood.LabAPI" />',
                       '<PackageReference Include="EXILED" Version="9.0.0" />',
                       '<PackageReference Include="ExMod.Exiled" Version="9.0.0" />',
                       '<PackageReference Include="Exiled.API" Version="9.0.0" />',
                       '<Reference Include="PluginAPI" />',
                       '<Reference Include="PluginAPI, Version=1.0.0.0" />',
                       '<Reference Include="NWAPI" />',
                       '<Reference Include="Anything"><HintPath>../libs/SCPSL_Data/Managed/x.dll</HintPath></Reference>',
                       '<Reference Include="Anything"><HintPath>$(SL_REFERENCES)/LabApi.dll</HintPath></Reference>',
                       '<Reference Include="Anything"><HintPath>libs\\LabApi.dll</HintPath></Reference>',
                       '<Reference Include="Anything"><HintPath>libs/Exiled.API.dll</HintPath></Reference>'):
            with self.subTest(marker=marker), tempfile.TemporaryDirectory() as d:
                write_csproj(d, marker)
                code, out = run_hook(d)
                self.assertEqual(code, 0)
                self.assertIn("additionalContext", out)

    def test_silent_for_a_project_that_only_looks_like_a_marker(self):
        # Regression (second-round inspector N2): a case-insensitive substring match turned the index on
        # for a project that merely referenced Acme.PluginApi.csproj, or mentioned "labapi" in a comment.
        for name, body in (
                ("project reference to Acme.PluginApi",
                 '<ProjectReference Include="..\\Acme.PluginApi\\Acme.PluginApi.csproj" />'),
                ("package that merely contains PluginApi", '<PackageReference Include="Acme.PluginApi" Version="1.0.0" />'),
                ("lower-case pluginapi assembly", '<Reference Include="pluginapi" />'),
                ("project reference to a LabApi-named project", '<ProjectReference Include="../LabApi.Tests/LabApi.Tests.csproj" />'),
                ("word labapi in a comment", "<!-- ported from a labapi plugin -->"),
                ("real marker inside a comment", '<!-- <Reference Include="LabApi" /> -->'),
                ("multi-line comment", '<!--\n  uses LabApi and EXILED\n  <HintPath>SCPSL_Data/x.dll</HintPath>\n-->'),
                ("dll that only ends like LabApi.dll", '<Reference Include="X"><HintPath>libs/MyLabApi.dll</HintPath></Reference>'),
                ("SCP Secret Laboratory in a comment", "<!-- SCP Secret Laboratory plugin -->"),
                ("Exiled as a longer word", '<PackageReference Include="ExiledFoo" Version="1.0.0" />')):
            with self.subTest(case=name), tempfile.TemporaryDirectory() as d:
                write_csproj(d, body)
                code, out = run_hook(d)
                self.assertEqual(code, 0)
                self.assertEqual(out.strip(), "")

    def test_a_real_marker_next_to_a_comment_still_fires(self):
        with tempfile.TemporaryDirectory() as d:
            write_csproj(d, '<!-- unrelated -->\n<Reference Include="LabApi" />\n<!-- trailing -->')
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            self.assertIn("additionalContext", out)

    def test_pure_shell_fallback_matches_the_same_way(self):
        # No python3: comment stripping falls back to tr+sed and must give the same answers.
        env = {"SCPSL_SKILLS_NO_PYTHON": "1"}
        for body, fires in (('<!-- labapi -->', False),
                            ('<!--\n  <Reference Include="LabApi" />\n-->', False),
                            ('<ProjectReference Include="Acme.PluginApi.csproj" />', False),
                            ('<!-- x -->\n<Reference Include="LabApi" />', True),
                            ('<Reference Include="PluginAPI" />', True)):
            with self.subTest(body=body), tempfile.TemporaryDirectory() as d:
                write_csproj(d, body)
                code, out = run_hook(d, env)
                self.assertEqual(code, 0)
                self.assertEqual("additionalContext" in out, fires, out)

    def test_override_forces_injection(self):
        with tempfile.TemporaryDirectory() as d:
            code, out = run_hook(d, {"SCPSL_SKILLS_ALWAYS": "1"})
            self.assertEqual(code, 0)
            self.assertIn("additionalContext", out)


@unittest.skipUnless(shutil.which("git"), "git not installed")
class SessionStartHookInGitRepoTest(unittest.TestCase):
    """Inside a git work tree the hook asks git instead of walking the directory tree."""

    def init_repo(self, d):
        subprocess.run(["git", "init", "-q", d], check=True, capture_output=True)

    def test_finds_an_untracked_project_deeper_than_the_find_depth_cap(self):
        # Proves the git path is used: the bounded find fallback stops at depth 8.
        with tempfile.TemporaryDirectory() as d:
            self.init_repo(d)
            write_csproj(os.path.join(d, *"abcdefghijk"), '<Reference Include="LabApi" />')
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            self.assertIn("additionalContext", out)

    def test_ignores_git_ignored_build_output(self):
        with tempfile.TemporaryDirectory() as d:
            self.init_repo(d)
            with open(os.path.join(d, ".gitignore"), "w") as f:
                f.write("obj/\n")
            write_csproj(os.path.join(d, "obj"), '<Reference Include="LabApi" />', "Ghost.csproj")
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            self.assertEqual(out.strip(), "")

    def test_ignores_non_ignored_obj_and_node_modules(self):
        with tempfile.TemporaryDirectory() as d:
            self.init_repo(d)
            write_csproj(os.path.join(d, "obj"), '<Reference Include="LabApi" />', "Ghost.csproj")
            write_csproj(os.path.join(d, "node_modules", "pkg"), '<Reference Include="LabApi" />', "Ghost.csproj")
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            self.assertEqual(out.strip(), "")

    def test_silent_for_a_bepinex_style_project(self):
        with tempfile.TemporaryDirectory() as d:
            self.init_repo(d)
            write_csproj(d, '<Reference Include="Assembly-CSharp" />', "Mod.csproj")
            code, out = run_hook(d)
            self.assertEqual(code, 0)
            self.assertEqual(out.strip(), "")


class JsonEscapingTest(unittest.TestCase):
    """The injected index must survive any control character in the skill text."""

    # Every control char U+0001..U+001F (NUL cannot live in a bash variable), plus quote and backslash.
    NASTY = "start\n" + "".join(chr(i) for i in range(1, 32)) + '\n"quoted" back\\slash \x7f end\n'

    def run_with_content(self, content, env_extra=None):
        with tempfile.TemporaryDirectory() as root:
            os.makedirs(os.path.join(root, "hooks"))
            os.makedirs(os.path.join(root, "skills", "using-scpsl-skills"))
            shutil.copy(HOOK, os.path.join(root, "hooks", "session-start"))
            with open(os.path.join(root, "skills", "using-scpsl-skills", "SKILL.md"), "w", encoding="utf-8", newline="") as f:
                f.write(content)
            with tempfile.TemporaryDirectory() as cwd:
                env = dict(os.environ)
                env.update({"SCPSL_SKILLS_ALWAYS": "1", **(env_extra or {})})
                out = subprocess.run(["bash", os.path.join(root, "hooks", "session-start")], cwd=cwd, env=env,
                                     capture_output=True, text=True, timeout=30)
        self.assertEqual(out.returncode, 0, out.stderr)
        # json.loads is strict: a raw control character inside a string raises here.
        return json.loads(out.stdout)["hookSpecificOutput"]["additionalContext"]

    def check_roundtrip(self, env_extra):
        ctx = self.run_with_content(self.NASTY, env_extra)
        # $(cat) drops trailing newlines; everything else must come back byte for byte.
        self.assertTrue(ctx.endswith(self.NASTY.rstrip("\n")), repr(ctx[-120:]))

    def test_control_characters_are_escaped_into_valid_json(self):
        self.check_roundtrip({})

    def test_pure_bash_fallback_escapes_every_control_character(self):
        self.check_roundtrip({"SCPSL_SKILLS_JSON_FALLBACK": "1"})

    def test_non_ascii_text_survives(self):
        ctx = self.run_with_content("zażółć gęślą jaźń \u2014 \u2713\n")
        self.assertIn("zażółć gęślą jaźń \u2014 \u2713", ctx)


if __name__ == "__main__":
    unittest.main()
