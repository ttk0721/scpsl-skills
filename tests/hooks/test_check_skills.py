#!/usr/bin/env python3
"""check-skills.py must reject frontmatter that YAML loaders reject.

Regression: the index skill shipped with `description: ... SCP: Secret Laboratory ...` unquoted.
A regex-based check passed it; Claude Code reported a formatting error.
Run: python3 -m unittest discover -s tests/hooks
"""
import importlib.util
import os
import re
import subprocess
import sys
import tempfile
import unittest

CHECK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools", "check-skills.py")


def load_check_module():
    spec = importlib.util.spec_from_file_location("check_skills", CHECK)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def make_skill(root, name, description, extra_words=0):
    d = os.path.join(root, name)
    os.makedirs(d)
    filler = ("word " * extra_words).strip()
    with open(os.path.join(d, "SKILL.md"), "w") as f:
        f.write(f"---\nname: {name}\ndescription: {description}\n---\n\n# X\n\nVerified against LabAPI 1.1.7.\n\n{filler}\n")


def run(root, *flags):
    out = subprocess.run([sys.executable, CHECK, root, *flags], capture_output=True, text=True, timeout=30)
    return out.returncode, out.stdout


class CheckSkillsTest(unittest.TestCase):
    def test_rejects_unquoted_colon_space(self):
        with tempfile.TemporaryDirectory() as root:
            make_skill(root, "broken-skill", "Use when writing an SCP: Secret Laboratory plugin")
            code, out = run(root)
            self.assertEqual(code, 1, out)
            self.assertIn("broken-skill", out)

    def test_accepts_quoted_description(self):
        with tempfile.TemporaryDirectory() as root:
            make_skill(root, "good-skill", '"Use when writing an SCP: Secret Laboratory plugin"')
            code, out = run(root)
            self.assertEqual(code, 0, out)

    def test_repository_skills_are_valid(self):
        code, out = run(os.path.join(os.path.dirname(CHECK), "..", "skills"))
        self.assertEqual(code, 0, out)


class SizePolicyTest(unittest.TestCase):
    """The size limits in docs/design.md used to be prose nobody enforced (second-round inspector #26)."""

    GOOD = '"Use when writing an SCP: Secret Laboratory plugin"'

    def test_rejects_a_skill_over_the_general_limit(self):
        mod = load_check_module()
        with tempfile.TemporaryDirectory() as root:
            make_skill(root, "fat-skill", self.GOOD, extra_words=mod.SKILL_MAX_WORDS + 1)
            code, out = run(root)
            self.assertEqual(code, 1, out)
            self.assertIn("fat-skill", out)
            self.assertIn("words", out)

    def test_accepts_a_skill_at_the_limit(self):
        mod = load_check_module()
        with tempfile.TemporaryDirectory() as root:
            # The body already carries a few words ("# X", the version line), so leave room for them.
            make_skill(root, "ok-skill", self.GOOD, extra_words=mod.SKILL_MAX_WORDS - 20)
            code, out = run(root)
            self.assertEqual(code, 0, out)

    def test_entry_skill_has_its_own_tighter_limit(self):
        mod = load_check_module()
        self.assertLess(mod.ENTRY_MAX_WORDS, mod.SKILL_MAX_WORDS)
        with tempfile.TemporaryDirectory() as root:
            make_skill(root, mod.ENTRY_SKILL, self.GOOD, extra_words=mod.ENTRY_MAX_WORDS + 1)
            code, out = run(root)
            self.assertEqual(code, 1, out)
            self.assertIn(mod.ENTRY_SKILL, out)

    def test_frontmatter_words_are_not_counted(self):
        mod = load_check_module()
        long_desc = '"Use when ' + "word " * 150 + 'x"'  # under the 1024-char description limit, well over 100 words
        with tempfile.TemporaryDirectory() as root:
            make_skill(root, mod.ENTRY_SKILL, long_desc, extra_words=mod.ENTRY_MAX_WORDS - 20)
            code, out = run(root)
            self.assertEqual(code, 0, out)

    def test_sizes_flag_prints_numbers_and_limits(self):
        mod = load_check_module()
        with tempfile.TemporaryDirectory() as root:
            make_skill(root, "small-skill", self.GOOD, extra_words=10)
            code, out = run(root, "--sizes")
            self.assertEqual(code, 0, out)
            self.assertIn("small-skill", out)
            self.assertIn("headroom", out)
            self.assertIn(str(mod.SKILL_MAX_WORDS), out)

    def test_design_md_quotes_the_enforced_limits(self):
        # One place defines the limits (check-skills.py); design.md may only quote them.
        mod = load_check_module()
        design = os.path.join(os.path.dirname(CHECK), "..", "docs", "design.md")
        with open(design, encoding="utf-8") as f:
            text = f.read()
        section = text[text.index("## Size policy"):text.index("## Layout")]
        for limit in (mod.ENTRY_MAX_WORDS, mod.HEAVY_MAX_WORDS, mod.SKILL_MAX_WORDS):
            self.assertIn(f"{limit:,}" if limit >= 1000 else str(limit), section)
        self.assertIn("check-skills.py --sizes", section)
        # No hand-filled "Now" column: it was the thing that went stale.
        self.assertIsNone(re.search(r"\|\s*Now\b", section))


def write(root, rel, text):
    path = os.path.join(root, *rel.split("/"))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


GOOD_DESC = '"Use when writing an SCP: Secret Laboratory plugin"'


class LinkCheckTest(unittest.TestCase):
    """C3: links were only checked in SKILL.md, only for .md targets, and never for anchors. The 100+
    reference files, docs/ and the README could rot silently."""

    def tree(self, files, root_flag=False):
        """Builds <tmp>/skills/a-skill/SKILL.md plus `files` ({path relative to <tmp>: text}) and runs the check."""
        with tempfile.TemporaryDirectory() as tmp:
            make_skill(os.path.join(tmp, "skills"), "a-skill", GOOD_DESC)
            for rel, text in files.items():
                write(tmp, rel, text)
            args = [os.path.join(tmp, "skills")] + (["--root", tmp] if root_flag else [])
            return run(*args)

    def test_a_broken_link_in_a_reference_file_is_found(self):
        code, out = self.tree({"skills/a-skill/ref.md": "# Ref\n\nline\nSee [other](missing.md).\n"})
        self.assertEqual(code, 1, out)
        self.assertIn("a-skill/ref.md:4", out)
        self.assertIn("missing.md", out)

    def test_a_link_to_a_non_markdown_file_is_checked_too(self):
        code, out = self.tree({"skills/a-skill/ref.md": "[code](../../examples/Foo.cs) and [dir](../)\n"})
        self.assertEqual(code, 1, out)
        self.assertIn("Foo.cs", out)
        self.assertNotIn("(../)", out)

    def test_existing_links_pass(self):
        code, out = self.tree({"skills/a-skill/ref.md": "[sib](other.md) [img](pic.png) [dir](.)\n",
                               "skills/a-skill/other.md": "# Other\n", "skills/a-skill/pic.png": "x"})
        self.assertEqual(code, 0, out)

    def test_links_in_code_and_external_links_are_ignored(self):
        text = ("`[x](nope.md)` and [web](https://example.com/a.md) and [mail](mailto:a@b.c)\n"
                "```\n[y](nope2.md)\n```\n~~~md\n[z](nope3.md)\n~~~\n")
        code, out = self.tree({"skills/a-skill/ref.md": text})
        self.assertEqual(code, 0, out)

    def test_same_file_anchor_must_exist(self):
        code, out = self.tree({"skills/a-skill/ref.md": "# Intro\n\n[ok](#intro) [bad](#outro)\n"})
        self.assertEqual(code, 1, out)
        self.assertIn("#outro", out)
        self.assertEqual(out.count("broken anchor"), 1, out)

    def test_anchor_into_another_file_must_exist(self):
        files = {"skills/a-skill/ref.md": "[a](other.md#real-heading) [b](other.md#gone)\n",
                 "skills/a-skill/other.md": "# Other\n\n## Real heading\n"}
        code, out = self.tree(files)
        self.assertEqual(code, 1, out)
        self.assertIn("#gone", out)
        self.assertEqual(out.count("broken anchor"), 1, out)

    def test_github_slug_rules(self):
        headings = {
            "Hello, World! (v2)": "hello-world-v2",
            "`Server.Tps` is a lie": "servertps-is-a-lie",
            "A & B": "a--b",
            "Why `_x` | and **bold**": "why-_x--and-bold",
            "Za\u017c\u00f3\u0142\u0107 g\u0119\u015bl\u0105": "za\u017c\u00f3\u0142\u0107-g\u0119\u015bl\u0105",
            "snake_case_word": "snake_case_word",
            "[Linked *text*](page.md) here": "linked-text-here",
            "1. Numbered": "1-numbered",
        }
        text = "\n".join(f"## {h}" for h in headings) + "\n\n" + " ".join(f"[l](#{a})" for a in headings.values()) + "\n"
        code, out = self.tree({"skills/a-skill/ref.md": text, "skills/a-skill/page.md": "# Page\n"})
        self.assertEqual(code, 0, out)

    def test_duplicate_headings_get_numeric_suffixes(self):
        text = "# Same\n## Same\n## Same\n[1](#same) [2](#same-1) [3](#same-2) [4](#same-3)\n"
        code, out = self.tree({"skills/a-skill/ref.md": text})
        self.assertEqual(code, 1, out)
        self.assertEqual(out.count("broken anchor"), 1, out)
        self.assertIn("#same-3", out)

    def test_headings_inside_fences_are_not_anchors(self):
        text = "```\n# Fake\n```\n[x](#fake)\n"
        code, out = self.tree({"skills/a-skill/ref.md": text})
        self.assertEqual(code, 1, out)

    def test_explicit_html_anchors_count(self):
        code, out = self.tree({"skills/a-skill/ref.md": '<a name="custom-spot"></a>\n[x](#custom-spot)\n'})
        self.assertEqual(code, 0, out)

    def test_an_upper_case_anchor_is_reported_with_the_reason(self):
        code, out = self.tree({"skills/a-skill/ref.md": "# Intro\n[x](#Intro)\n"})
        self.assertEqual(code, 1, out)
        self.assertIn("lower-case", out)

    def test_percent_encoded_paths_and_anchors_are_decoded(self):
        files = {"skills/a-skill/ref.md": "[x](my%20file.md#a-b)\n", "skills/a-skill/my file.md": "# A B\n"}
        code, out = self.tree(files)
        self.assertEqual(code, 0, out)

    def test_docs_and_readme_are_checked_with_root(self):
        files = {"docs/guide.md": "# Guide\n[x](nope.md)\n", "README.md": "# Top\n[y](#missing)\n"}
        code, out = self.tree(files, root_flag=True)
        self.assertEqual(code, 1, out)
        self.assertIn("docs/guide.md:2", out)
        self.assertIn("README.md:2", out)

    def test_docs_and_readme_are_not_checked_without_root(self):
        code, out = self.tree({"docs/guide.md": "[x](nope.md)\n", "README.md": "[y](nope.md)\n"})
        self.assertEqual(code, 0, out)

    def test_the_repository_docs_readme_and_references_are_clean(self):
        repo = os.path.join(os.path.dirname(CHECK), "..")
        code, out = run(os.path.join(repo, "skills"), "--root", repo)
        self.assertEqual(code, 0, out)


class MissingYamlTest(unittest.TestCase):
    """C3: without PyYAML the check fell back to a regex that accepts what YAML loaders reject."""

    def run_without_yaml(self, root):
        script = ("import runpy, sys; sys.modules['yaml'] = None; sys.argv = ['check-skills.py', sys.argv[1]]; "
                  "runpy.run_path(%r, run_name='__main__')" % os.path.abspath(CHECK))
        out = subprocess.run([sys.executable, "-c", script, root], capture_output=True, text=True, timeout=30)
        return out.returncode, out.stdout, out.stderr

    def test_refuses_to_run_with_a_clear_message(self):
        with tempfile.TemporaryDirectory() as root:
            make_skill(root, "good-skill", GOOD_DESC)
            code, out, err = self.run_without_yaml(root)
        self.assertEqual(code, 2, out + err)
        self.assertIn("PyYAML", err)
        self.assertIn("pip install", err)
        self.assertNotIn("0 problem", out)

    def test_it_does_not_pass_the_broken_description_silently(self):
        with tempfile.TemporaryDirectory() as root:
            make_skill(root, "broken-skill", "Use when writing an SCP: Secret Laboratory plugin")
            code, out, err = self.run_without_yaml(root)
        self.assertNotEqual(code, 0, out + err)


if __name__ == "__main__":
    unittest.main()
