#!/usr/bin/env python3
"""check-publishable.py must catch leaks of a maintainer's machine, of private research and of
decompiled game code, including fragments hidden inside long strings.

Regression: SlKit's Directory.Build.props shipped with a hardcoded /home/<user>/... default and the
event catalog pointed into the private research notes; the check only knew decompiler signatures. The
third inspector round then showed the catalog JSON carrying decompiler locals, pasted statements,
private fields, line references and research tags while the tool printed "clean": its corpus check
compared whole lines, which is useless for one 600-character JSON string.

The leak patterns below are assembled at run time (see `cat`), so this file does not itself contain
them and needs no ignore marker. Run: python3 -m unittest discover -s tests/hooks
"""
import os
import subprocess
import sys
import tempfile
import unittest

CHECK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools", "check-publishable.py")
FIXTURES = "tools/scpsl-lint/tests/fixtures"


def cat(*parts):
    return "".join(parts)


# Built from pieces so the repository scan of this very file stays quiet.
HOME_PATH = cat("/home/someone/", ".local/share/Steam/x.dll")
WIN_PATH = cat("Config in C:\\Users\\Jan Kowalski\\", "AppData\\Roaming\\SCP Secret Laboratory")
PRIVATE_REF = cat("see docs/", "badania/10-optymalizacja-serwera.md")
TAG = cat("SKEP", "TIC")
MARKER = cat("# check-publishable: ", "ignore-file")


def scan_tree(files, args=(), private_words=None, reviewed=None):
    """Scans a tree of {relative path: text}. `private_words` goes to <root>/.private-words."""
    with tempfile.TemporaryDirectory() as root:
        for rel, text in files.items():
            path = os.path.join(root, *rel.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w") as f:
                f.write(text)
        if private_words is not None:
            with open(os.path.join(root, ".private-words"), "w") as f:
                f.write(private_words)
        if reviewed is not None:
            os.makedirs(os.path.join(root, "tools"), exist_ok=True)
            with open(os.path.join(root, "tools", "check-publishable.reviewed"), "w") as f:
                f.write(reviewed)
        out = subprocess.run([sys.executable, CHECK, "--root", root, *args], capture_output=True, text=True, timeout=120)
        return out.returncode, out.stdout, out.stderr


def scan(text, name="doc.md", args=(), private_words=None):
    return scan_tree({name: text}, args, private_words)


def make_corpus(files):
    """A decompiled-corpus directory with {file name: text}."""
    d = tempfile.TemporaryDirectory()
    for name, text in files.items():
        with open(os.path.join(d.name, name), "w") as f:
            f.write(text)
    return d


class SignatureTest(unittest.TestCase):
    def test_flags_linux_home_with_real_content(self):
        code, out, _ = scan(HOME_PATH, "Directory.Build.props")
        self.assertEqual(code, 1, out)
        self.assertIn("local user path", out)

    def test_flags_windows_profile_path(self):
        code, out, _ = scan(WIN_PATH)
        self.assertEqual(code, 1, out)
        self.assertIn("Windows profile", out)

    def test_flags_private_research_reference(self):
        code, out, _ = scan(PRIVATE_REF)
        self.assertEqual(code, 1, out)
        self.assertIn("private research", out)

    def test_allows_generic_examples(self):
        code, out, _ = scan("csv_path: /home/scpsl/frames.csv and %APPDATA%\\SCP Secret Laboratory and C:\\Users\\<you>\\AppData")
        self.assertEqual(code, 0, out)


class GuaranteeLevelTest(unittest.TestCase):
    def test_without_corpus_it_says_the_check_is_weak(self):
        code, out, _ = scan("nothing suspicious here")
        self.assertEqual(code, 0, out)
        self.assertIn("WEAK CHECK (no corpus)", out)
        self.assertNotIn("clean", out)

    def test_with_corpus_it_does_not_claim_weakness(self):
        with tempfile.TemporaryDirectory() as corpus:
            code, out, _ = scan("nothing suspicious here", args=["--corpus", corpus])
        self.assertEqual(code, 0, out)
        self.assertNotIn("WEAK", out)
        self.assertIn("clean", out)

    def test_require_corpus_exits_2_when_missing(self):
        code, out, err = scan("nothing suspicious here", args=["--require-corpus"])
        self.assertEqual(code, 2, out + err)
        self.assertIn("--require-corpus", err)

    def test_require_corpus_exits_2_when_path_is_not_a_directory(self):
        code, out, err = scan("x", args=["--require-corpus", "--corpus", "/nonexistent/corpus-dir"])
        self.assertEqual(code, 2, out + err)

    def test_require_corpus_passes_with_a_corpus_directory(self):
        with tempfile.TemporaryDirectory() as corpus:
            code, out, err = scan("nothing suspicious here", args=["--require-corpus", "--corpus", corpus])
        self.assertEqual(code, 0, out + err)


class CorpusLinesTest(unittest.TestCase):
    def test_corpus_overlap_is_still_flagged(self):
        line = "var damage = player.Health * multiplier + baseValue;"
        second = "var second = player.Stamina.CurrentValue * factor;"
        third = "var third = Mathf.Clamp(second, minimumValue, maximumValue);"
        with make_corpus({"Decompiled.cs": "\n".join([line, second, third])}) as corpus:
            code, out, _ = scan("\n".join(["```", line, second, third, "```"]), args=["--corpus", corpus])
        self.assertEqual(code, 1, out)
        self.assertIn("match the decompiled corpus", out)


class PrivateWordsTest(unittest.TestCase):
    def test_private_word_is_flagged_case_insensitively(self):
        code, out, _ = scan("Thanks to Acme-Internal for the idea", private_words="# comment\n\nacme-internal\n")
        self.assertEqual(code, 1, out)
        self.assertIn("private word", out)

    def test_private_word_regex_and_file_name(self):
        code, out, _ = scan("harmless", name="widgetco-notes.md", private_words=r"widget(co|corp)\b")
        self.assertEqual(code, 1, out)
        self.assertIn("file name matches", out)

    def test_private_words_comments_and_blank_lines_match_nothing(self):
        code, out, _ = scan("some text with a hash # and nothing else", private_words="# only a comment\n\n   \n")
        self.assertEqual(code, 0, out)

    def test_private_words_file_is_optional(self):
        code, out, _ = scan("anything")
        self.assertEqual(code, 0, out)
        self.assertIn("no private words", out)

    def test_invalid_private_word_regex_is_a_setup_error_not_a_silent_pass(self):
        code, out, err = scan("anything", private_words="ok-word\n(unclosed\n")
        self.assertEqual(code, 2, out + err)
        self.assertIn("invalid regex", err)

    def test_private_words_flag_points_at_an_explicit_file(self):
        with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as pw:
            pw.write("secret-project\n")
        try:
            code, out, _ = scan("see secret-project", args=["--private-words", pw.name])
        finally:
            os.unlink(pw.name)
        self.assertEqual(code, 1, out)

    def test_private_words_flag_with_missing_file_is_a_setup_error(self):
        code, out, err = scan("anything", args=["--private-words", "/nonexistent/words.txt"])
        self.assertEqual(code, 2, out + err)

    def test_the_private_words_file_itself_is_not_scanned(self):
        code, out, _ = scan("clean text", private_words="clean-text-never-here\n")
        self.assertEqual(code, 0, out)


class ArtefactTest(unittest.TestCase):
    """C1: what the signature check cannot see, the decompiler's habits in prose and data."""

    def flagged(self, text, name="data.json"):
        code, out, _ = scan(text, name)
        return code, out

    def test_decompiler_locals_in_a_json_string(self):
        for local in ("num2", "flag2", "obj2", "e3", "hub2", "ammoPickup2"):
            code, out = self.flagged('{"notes": "reads ' + local + ' after the cast"}')
            self.assertEqual(code, 1, f"{local}: {out}")
            self.assertIn("decompiler-style local", out)

    def test_statement_shaped_code_in_a_json_string(self):
        code, out = self.flagged('{"consumption": "Foo if (!ev.IsAllowed) return; faction = ev.Faction;"}')
        self.assertEqual(code, 1, out)
        self.assertIn("code statement", out)

    def test_private_underscore_fields_in_prose(self):
        for text in ("VoiceTransceiver _packageSize = 480", "raised after _userInput was stored",
                     "_sentPackets++ < 128", "guarded by _lockdownInEffect"):
            code, out = self.flagged('{"notes": "' + text + '"}')
            self.assertEqual(code, 1, f"{text}: {out}")

    def test_decompiled_line_references(self):
        code, out = self.flagged(cat('{"notes": "IntercomMuteCommand', '/:49 issues the mute"}'))
        self.assertEqual(code, 1, out)
        self.assertIn("line reference", out)
        code, out = self.flagged(cat("see FooBarManager", ".cs:120 for details"), "doc.md")
        self.assertEqual(code, 1, out)

    def test_research_tags(self):
        for text in (cat(TAG, ": raised inside the loop"), cat(TAG, " ADDITIONS: (1) the delegate"),
                     cat("the file is (local ", "research files) only")):
            code, out = self.flagged('{"notes": "' + text + '"}')
            self.assertEqual(code, 1, f"{text}: {out}")
            self.assertIn("research tag", out)

    def test_research_tags_are_caught_in_code_files_too(self):
        code, out, _ = scan_tree({"Plugin.cs": cat("// ", TAG, ": verified against the engine\nclass A { }\n")})
        self.assertEqual(code, 1, out)
        self.assertIn("research tag", out)

    def test_the_bare_word_in_a_process_skill_is_fine(self):
        code, out = self.flagged(cat("You are an independent ", TAG, " reviewing the plan."), "prompt.md")
        self.assertEqual(code, 0, out)

    def test_legitimate_names_with_digits_pass(self):
        code, out = self.flagged("Uses Vector3, sha256, utf8, base64Encode2? no: toVector3 and md5 and x86 and e2e tests.", "doc.md")
        self.assertEqual(code, 0, out)

    def test_inline_code_spans_may_show_original_snippets(self):
        # Skills teach with `if (!ev.IsAllowed) return;` and `_cfg = cfg;` in the text; those are ours.
        code, out = self.flagged("Start with `if (!ev.IsAllowed) return;` then store `_cfg = cfg;` and call `Foo?.Bar()`.", "skill.md")
        self.assertEqual(code, 0, out)

    def test_a_code_span_wrapped_over_two_lines_is_still_a_span(self):
        code, out = self.flagged("Do this: `var x = new Foo();\n  _http = Make(Name, options);` and move on.", "skill.md")
        self.assertEqual(code, 0, out)

    def test_the_same_statement_outside_a_span_is_flagged(self):
        code, out = self.flagged("The server runs if (!ev.IsAllowed) return; before anything else.", "skill.md")
        self.assertEqual(code, 1, out)

    def test_fenced_examples_may_assign_and_use_fields(self):
        text = "Example:\n```csharp\nprivate int _count;\nvoid A() { _count++; if (_count > 3) return; var x = y; }\n```\n"
        code, out = self.flagged(text, "skill.md")
        self.assertEqual(code, 0, out)

    def test_decompiler_locals_are_flagged_inside_fences_too(self):
        code, out = self.flagged("```csharp\nint num2 = 5;\n```\n", "skill.md")
        self.assertEqual(code, 1, out)
        self.assertIn("decompiler-style local", out)

    def test_original_csharp_may_use_fields_and_assignments(self):
        code, out, _ = scan_tree({"examples/A.cs": "class A {\n  int _count;\n  void M() { _count++; var h = x?.y; return; }\n}\n"})
        self.assertEqual(code, 0, out)

    def test_counts_are_reported_per_file(self):
        code, out, _ = scan_tree({"a.json": '{"n": "num2 and flag3"}', "b.md": cat(TAG, ": one\n"), "ok.md": "fine\n"})
        self.assertEqual(code, 1, out)
        self.assertIn("findings per file", out)
        self.assertRegex(out, r"a\.json: 2 \(")
        self.assertRegex(out, r"b\.md: 1 \(")
        self.assertNotRegex(out, r"ok\.md: \d")

    def test_long_listings_are_truncated_but_counted(self):
        text = "\n".join(f'"n{i}": "num{i + 2} here",' for i in range(20))
        code, out, _ = scan_tree({"big.json": text})
        self.assertEqual(code, 1, out)
        self.assertIn("... 14 more", out)
        self.assertRegex(out, r"big\.json: 20 \(")


class NgramTest(unittest.TestCase):
    """C1(b): a fragment inside one long string, or code with renamed locals, against the corpus."""

    BODY = (
        "public void ApplyBoost(Gizmo gizmo, float amount)\n"
        "{\n"
        "\tfloat num2 = gizmo.widgetRack.GetModule<SpringPart>().CurLevel * amount;\n"
        "\tif (num2 > gizmo.widgetRack.GetModule<DamperPart>().MaxLevel)\n"
        "\t{\n"
        "\t\tgizmo.widgetRack.GetModule<DamperPart>().AddFlowEntry(num2, gizmo.tagHandle);\n"
        "\t}\n"
        "}\n"
    )

    def test_fragment_inside_a_long_json_string_is_found(self):
        long_string = ("Raised after the shield step. ApplyBoost(Gizmo gizmo, float amount) float num5 = "
                       "gizmo.widgetRack.GetModule<SpringPart>().CurLevel * amount; if (num5 > "
                       "gizmo.widgetRack.GetModule<DamperPart>().MaxLevel) { gizmo.widgetRack.GetModule<DamperPart>()"
                       ".AddFlowEntry(num5, gizmo.tagHandle); } and then nothing else happens.")
        with make_corpus({"Boost.cs": self.BODY}) as corpus:
            code, out, _ = scan('{"notes": "' + long_string + '"}', "catalog.json", args=["--corpus", corpus])
        self.assertEqual(code, 1, out)
        self.assertIn("corpus-ngram", out)
        self.assertIn("consecutive tokens match the decompiled corpus", out)

    def test_reformatted_code_with_renamed_locals_is_found(self):
        mine = ("class Mine {\n void Do(Gizmo gizmo, float amount) { float num9 = gizmo.widgetRack.GetModule<SpringPart>()"
                ".CurLevel * amount; if (num9 > gizmo.widgetRack.GetModule<DamperPart>().MaxLevel) "
                "{ gizmo.widgetRack.GetModule<DamperPart>().AddFlowEntry(num9, gizmo.tagHandle); } }\n}\n")
        with make_corpus({"Boost.cs": self.BODY}) as corpus:
            code, out, _ = scan_tree({"Mine.cs": mine}, args=["--corpus", corpus])
        self.assertEqual(code, 1, out)
        self.assertIn("Mine.cs", out)
        self.assertIn("corpus-ngram", out)

    def test_unrelated_code_is_not_flagged(self):
        other = "class Other {\n void Run(List<int> xs) { foreach (var x in xs) { Console.WriteLine(x * 2 + offset); } }\n}\n"
        with make_corpus({"Boost.cs": self.BODY}) as corpus:
            code, out, _ = scan_tree({"Other.cs": other}, args=["--corpus", corpus])
        self.assertEqual(code, 0, out)

    def test_a_framework_idiom_present_in_many_corpus_files_is_not_evidence(self):
        idiom = "void M() { total = collection.Weight * factor + bonus.Multiplier - penalty.Offset * scale.Ratio; }\n"
        corpus_files = {f"File{n}.cs": idiom for n in range(6)}
        with make_corpus(corpus_files) as corpus:
            code, out, _ = scan_tree({"Mine.cs": "class Mine {\n " + idiom + "}\n"}, args=["--corpus", corpus])
        self.assertEqual(code, 0, out)

    def test_the_same_code_in_one_corpus_file_is_evidence(self):
        idiom = "void M() { total = collection.Weight * factor + bonus.Multiplier - penalty.Offset * scale.Ratio; }\n"
        with make_corpus({"Only.cs": idiom}) as corpus:
            code, out, _ = scan_tree({"Mine.cs": "class Mine {\n " + idiom + "}\n"}, args=["--corpus", corpus])
        self.assertEqual(code, 1, out)

    def test_a_list_of_names_is_public_vocabulary(self):
        names = "enum Reason { None, RoundStart, LateJoin, Respawn, Died, Escaped, Revived, RemoteAdmin, Destroyed }\n"
        with make_corpus({"Reason.cs": names}) as corpus:
            code, out, _ = scan("Values: None, RoundStart, LateJoin, Respawn, Died, Escaped, Revived, RemoteAdmin, Destroyed", args=["--corpus", corpus])
        self.assertEqual(code, 0, out)

    def test_using_directives_do_not_count(self):
        header = "using System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing System.Text;\nnamespace A.B.C\n"
        with make_corpus({"One.cs": header}) as corpus:
            code, out, _ = scan_tree({"Mine.cs": header + "class Mine { }\n"}, args=["--corpus", corpus])
        self.assertEqual(code, 0, out)

    def test_ngram_check_is_off_without_a_corpus(self):
        code, out, _ = scan('{"notes": "' + self.BODY.replace("\n", " ").replace("\t", " ") + '"}', "catalog.json")
        self.assertNotIn("corpus-ngram", out)

    def test_reviewed_entries_silence_a_match_and_are_listed(self):
        mine = "class Mine {\n " + self.BODY.replace("num2", "num7") + "}\n"
        entry = "".join(f"Mine.cs | {snippet} | standard idiom reviewed by hand\n" for snippet in (
            "float num7 = gizmo.widgetRack.GetModule<SpringPart>().CurLevel * amount;",
            "if (num7 > gizmo.widgetRack.GetModule<DamperPart>().MaxLevel)",
            "gizmo.widgetRack.GetModule<DamperPart>().AddFlowEntry(num7, gizmo.tagHandle);"))
        with make_corpus({"Boost.cs": self.BODY}) as corpus:
            code, out, _ = scan_tree({"Mine.cs": mine}, args=["--corpus", corpus], reviewed=entry)
        self.assertEqual(code, 0, out)
        self.assertIn("reviewed: Mine.cs", out)

    def test_a_stale_reviewed_entry_is_a_finding(self):
        entry = "Gone.cs | float num7 = gizmo.widgetRack.GetModule<SpringPart>().CurLevel | file no longer exists\n"
        with make_corpus({"Boost.cs": self.BODY}) as corpus:
            code, out, _ = scan_tree({"Mine.cs": "class Mine { }\n"}, args=["--corpus", corpus], reviewed=entry)
        self.assertEqual(code, 1, out)
        self.assertIn("reviewed-list", out)

    def test_a_malformed_reviewed_file_is_a_setup_error(self):
        code, out, err = scan_tree({"Mine.cs": "class Mine { }\n"}, reviewed="Mine.cs | too short | x\n")
        self.assertEqual(code, 2, out + err)


class IgnoreMarkerTest(unittest.TestCase):
    """C2: the marker used to switch off every check for a whole file, anywhere."""

    LEAK = cat("// ", HOME_PATH, "\n")
    REASON = "fixture: deliberately contains a local path"

    def test_honoured_in_a_fixture_directory_with_a_reason_and_listed(self):
        text = f"{MARKER} {self.REASON}\n{self.LEAK}"
        code, out, _ = scan_tree({f"{FIXTURES}/leak.cs": text.replace("#", "//", 1)})
        self.assertEqual(code, 0, out)
        self.assertIn("ignored:", out)
        self.assertIn("leak.cs", out)
        self.assertIn("deliberately contains a local path", out)

    def test_not_honoured_outside_the_fixture_directories(self):
        text = f"{MARKER} {self.REASON}\n{self.LEAK}"
        code, out, _ = scan_tree({"examples/leak.cs": text.replace("#", "//", 1)})
        self.assertEqual(code, 1, out)
        self.assertIn("outside the fixture directories", out)
        self.assertIn("local user path", out)  # and the checks still ran on it

    def test_a_marker_in_the_test_tree_is_not_a_blanket_pass_either(self):
        text = f"{MARKER} {self.REASON}\n{self.LEAK}"
        code, out, _ = scan_tree({"tests/hooks/test_x.py": text})
        self.assertEqual(code, 1, out)
        self.assertIn("outside the fixture directories", out)

    def test_a_marker_without_a_reason_is_refused(self):
        for tail in ("", "  ", " ok", " (x)"):
            text = f"{MARKER}{tail}\n{self.LEAK}"
            code, out, _ = scan_tree({f"{FIXTURES}/leak.py": text})
            self.assertEqual(code, 1, f"{tail!r}: {out}")
            self.assertIn("without a reason", out)
            self.assertIn("local user path", out)

    def test_private_words_still_apply_to_an_ignored_fixture(self):
        text = f"{MARKER} {self.REASON}\nthanks to Acme-Internal\n"
        code, out, _ = scan_tree({f"{FIXTURES}/x.py": text}, private_words="acme-internal\n")
        self.assertEqual(code, 1, out)
        self.assertIn("private word", out)

    def test_corpus_checks_still_apply_to_an_ignored_fixture(self):
        body = NgramTest.BODY.replace("\n", " ").replace("\t", " ").replace("num2", "num4")
        text = f"{MARKER} {self.REASON}\nclass Mine {{\n {body} }}\n"
        with make_corpus({"Boost.cs": body}) as corpus:
            code, out, _ = scan_tree({f"{FIXTURES}/mine.py": text}, args=["--corpus", corpus])
        self.assertEqual(code, 1, out)
        self.assertIn("corpus-ngram", out)

    def test_the_marker_must_be_a_comment_in_the_first_ten_lines(self):
        filler = "\n" * 12
        text = f"{filler}{MARKER} {self.REASON}\n{self.LEAK}"
        code, out, _ = scan_tree({f"{FIXTURES}/late.cs": text})
        self.assertEqual(code, 1, out)
        # Merely documenting the marker in prose is not using it.
        code, out, _ = scan("Write cat marker text: check-publishable: ignore-file because reasons\n", "doc.md")
        self.assertEqual(code, 0, out)


if __name__ == "__main__":
    unittest.main()
