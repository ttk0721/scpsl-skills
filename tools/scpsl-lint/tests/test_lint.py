"""Tests for scpsl-lint. Plain unittest; run with:

    python3 -m unittest discover -s tools/scpsl-lint/tests

Every fixture in tests/fixtures/*.cs is linted on its own and compared against its inline
`// expect: SL001 SL008` markers (exact match on (line, rule): no missing and no extra findings).
Fixtures without markers must produce no findings at all, which is what guards against
false positives.
"""
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL_DIR = os.path.dirname(HERE)
FIXTURES = os.path.join(HERE, "fixtures")
CLI = os.path.join(TOOL_DIR, "scpsl_lint.py")
sys.path.insert(0, TOOL_DIR)

import scpsl_lint  # noqa: E402

EXPECT_RE = re.compile(r"//\s*expect:\s*((?:SL\d{3}[\s,]*)+)")


def fixture_paths():
    return sorted(os.path.join(FIXTURES, f) for f in os.listdir(FIXTURES) if f.endswith(".cs"))


def expected_for(path):
    out = set()
    with open(path, encoding="utf-8") as fh:
        for lineno, text in enumerate(fh, start=1):
            m = EXPECT_RE.search(text)
            if m:
                for rule in re.findall(r"SL\d{3}", m.group(1)):
                    out.add((lineno, rule))
    return out


def run_cli(*args):
    return subprocess.run([sys.executable, CLI, *args], capture_output=True, text=True)


CATALOG = scpsl_lint.Catalog.load(scpsl_lint.default_catalog_path())


def lint(src, name="snippet.cs"):
    return scpsl_lint.lint_texts([(name, src)], CATALOG)


def rules_of(findings):
    return sorted(f.rule for f in findings)


class FixtureTest(unittest.TestCase):
    def test_every_fixture_matches_its_markers(self):
        paths = fixture_paths()
        self.assertGreaterEqual(len(paths), 15)
        for path in paths:
            with self.subTest(fixture=os.path.basename(path)):
                with open(path, encoding="utf-8") as fh:
                    findings = scpsl_lint.lint_texts([(path, fh.read())], CATALOG)
                got = {(f.line, f.rule) for f in findings}
                want = expected_for(path)
                self.assertEqual(sorted(want - got), [], "expected but not reported")
                self.assertEqual(sorted(got - want), [], "reported but not expected: "
                                 + "; ".join(f.format() for f in findings if (f.line, f.rule) not in want))

    def test_every_rule_is_exercised_by_a_fixture(self):
        seen = set()
        for path in fixture_paths():
            seen |= {rule for _line, rule in expected_for(path)}
        self.assertEqual(sorted(set(scpsl_lint.RULES) - seen), [])

    def test_clean_plugin_has_no_findings(self):
        path = os.path.join(FIXTURES, "clean_plugin.cs")
        with open(path, encoding="utf-8") as fh:
            self.assertEqual(lint(fh.read()), [])


class RuleTableTest(unittest.TestCase):
    def test_severities_match_the_spec(self):
        want = {
            "SL001": "warn", "SL002": "warn", "SL003": "warn", "SL004": "error", "SL005": "warn",
            "SL006": "error", "SL007": "error", "SL008": "warn", "SL009": "error", "SL010": "error",
            "SL011": "warn", "SL012": "warn", "SL013": "warn", "SL014": "warn", "SL015": "info",
            "SL016": "warn", "SL017": "info", "SL018": "error", "SL019": "warn",
        }
        self.assertEqual({k: v[0] for k, v in scpsl_lint.RULES.items()}, want)

    def test_readme_documents_every_rule(self):
        with open(os.path.join(TOOL_DIR, "README.md"), encoding="utf-8") as fh:
            readme = fh.read()
        for rule in scpsl_lint.RULES:
            with self.subTest(rule=rule):
                self.assertIn("### " + rule, readme)


class CatalogTest(unittest.TestCase):
    def test_loaded_from_the_public_catalog(self):
        self.assertTrue(CATALOG.events["PlayerEvents.ValidatedVisibility"]["hot"])
        self.assertEqual(CATALOG.events["PlayerEvents.SearchedAmmo"]["frequency"], "never-fired")
        self.assertNotIn("PlayerEvents.AimingWeapon", CATALOG.events)  # not a LabAPI event, so not in the catalog
        self.assertEqual(CATALOG.override_map["OnPlayerHurting"], "PlayerEvents.Hurting")
        self.assertEqual(CATALOG.override_map["OnServerWaitingForPlayers"], "ServerEvents.WaitingForPlayers")
        self.assertEqual(CATALOG.override_map["OnScpHumeShieldBroken"], "ScpEvents.HumeShieldBroken")

    def test_a_repeated_event_row_is_rejected(self):
        # The schema has one row per event; keeping either of two rows silently would hide facts.
        row = {"event": "PlayerEvents.Joined", "frequency": "per-action"}
        with self.assertRaises(ValueError) as ctx:
            scpsl_lint.Catalog({"events": [row, dict(row)]})
        self.assertIn("PlayerEvents.Joined", str(ctx.exception))
        with tempfile.TemporaryDirectory() as tmp:
            cat = os.path.join(tmp, "events.json")
            with open(cat, "w", encoding="utf-8") as fh:
                json.dump({"events": [row, dict(row)]}, fh)
            res = run_cli(os.path.join(FIXTURES, "clean_plugin.cs"), "--catalog", cat)
        self.assertEqual(res.returncode, 2)

    def test_setter_facts_have_the_documented_shape(self):
        # ignoredSetters: names only. conditionallyIgnoredSetters: [{setter, when}]. pair: null or {event, role}.
        for key, rec in CATALOG.events.items():
            with self.subTest(event=key):
                for name in rec.get("ignoredSetters", []):
                    self.assertTrue(isinstance(name, str) and name.isidentifier(), name)
                for entry in rec.get("conditionallyIgnoredSetters", []):
                    self.assertEqual(sorted(entry), ["setter", "when"])
                    self.assertTrue(entry["when"].strip())
                    self.assertNotIn(entry["setter"], rec.get("ignoredSetters", []))
                pair = rec.get("pair")
                if pair is not None:
                    self.assertEqual(sorted(pair), ["event", "role"])
                    self.assertIn(pair["event"], CATALOG.events)

    def test_accessors_split_always_from_conditional(self):
        rec = CATALOG.events["PlayerEvents.Muting"]
        self.assertEqual(scpsl_lint.Catalog.always_ignored_setters(rec), ["IsIntercom"])
        self.assertEqual(scpsl_lint.Catalog.conditionally_ignored_setters(rec), [])
        rec = CATALOG.events["PlayerEvents.UpdatingEffect"]
        self.assertEqual(scpsl_lint.Catalog.always_ignored_setters(rec), [])
        (setter, when), = scpsl_lint.Catalog.conditionally_ignored_setters(rec)
        self.assertEqual(setter, "Duration")
        self.assertIn("ServerSetState", when)

    def test_catalog_is_read_at_runtime(self):
        # Make a normally harmless event hot in a custom catalog: the linter must follow the file.
        with open(scpsl_lint.default_catalog_path(), encoding="utf-8") as fh:
            data = json.load(fh)
        for rec in data["events"]:
            if rec["event"] == "PlayerEvents.Joined":
                rec["hot"] = True
                rec["frequency"] = "per-frame"
        with tempfile.TemporaryDirectory() as tmp:
            cat = os.path.join(tmp, "events.json")
            with open(cat, "w", encoding="utf-8") as fh:
                json.dump(data, fh)
            src = os.path.join(tmp, "a.cs")
            with open(src, "w", encoding="utf-8") as fh:
                fh.write("class A { void E() { PlayerEvents.Joined += OnJ; }\n"
                         " void OnJ(object ev) { Logger.Debug(\"joined\"); } }\n")
            normal = run_cli(src)
            custom = run_cli(src, "--catalog", cat)
        self.assertEqual(normal.returncode, 0, normal.stdout)
        self.assertNotIn("SL001", normal.stdout)
        # SL001 is a warning and the per-raise work inside the now-hot handler is an error (SL018).
        self.assertEqual(custom.returncode, 1)
        self.assertIn("warn SL001", custom.stdout)
        self.assertIn("error SL018", custom.stdout)

    def test_missing_catalog_is_a_usage_error(self):
        res = run_cli(os.path.join(FIXTURES, "clean_plugin.cs"), "--catalog", "/nonexistent/events.json")
        self.assertEqual(res.returncode, 2)


class MessageTest(unittest.TestCase):
    def test_sl001_names_the_frequency_class(self):
        f = lint("class A { void E() { PlayerEvents.ReceivingVoiceMessage += OnV; } void OnV(object e) {} }")
        self.assertEqual(rules_of(f), ["SL001"])
        self.assertIn("high-rate-network", f[0].message)
        self.assertIn("PlayerEvents.ReceivingVoiceMessage", f[0].message)
        self.assertNotIn("\n", f[0].message)

    def test_sl001_override_names_the_event(self):
        f = lint("class H : CustomEventsHandler {\n"
                 "  public override void OnPlayerValidatedVisibility(PlayerValidatedVisibilityEventArgs ev) { }\n}\n")
        self.assertEqual(rules_of(f), ["SL001"])
        self.assertEqual(f[0].line, 2)
        self.assertIn("per-frame-per-pair", f[0].message)

    def test_unknown_events_are_ignored(self):
        self.assertEqual(lint("class A { void E() { PlayerEvents.NoSuchEvent += OnX; } }"), [])

    def test_format_is_file_line_col(self):
        f = lint("class A {\n void E() { GC.Collect(); }\n}\n", name="x/y.cs")
        self.assertEqual(len(f), 1)
        self.assertTrue(f[0].format().startswith("x/y.cs:2:"))
        self.assertIn("SL010", f[0].format())


class HandlerDetectionTest(unittest.TestCase):
    def test_expression_bodied_handler_method(self):
        src = ("class A {\n"
               " void E() { PlayerEvents.Hurting += OnH; }\n"
               " void OnH(PlayerHurtingEventArgs ev) => ev.DamageHandler = null;\n"
               "}\n")
        self.assertEqual(rules_of(lint(src)), ["SL001", "SL003"])  # Hurting is conditionally hot

    def test_handler_defined_in_another_file_still_counts_as_subscription(self):
        self.assertEqual(rules_of(lint("class A { void E() { PlayerEvents.TriggeringTesla += Elsewhere.OnTesla; } }")),
                         ["SL001"])

    def test_lambda_parameter_name_is_respected(self):
        src = "class A { void E() { PlayerEvents.Dying += (PlayerDyingEventArgs args) => { args.DamageHandler = null; }; } }"
        self.assertEqual(rules_of(lint(src)), ["SL003"])

    def test_setter_write_in_unrelated_handler_is_not_flagged(self):
        src = "class A { void E() { PlayerEvents.Joined += ev => { ev.DamageHandler = null; }; } }"
        self.assertEqual(lint(src), [])

    def test_override_must_be_marked_override(self):
        # A same-named plain method is not a CustomEventsHandler subscription.
        self.assertEqual(lint("class A { void OnPlayerValidatedVisibility(object ev) { } }"), [])


class IgnoredSetterTest(unittest.TestCase):
    """SL003: always-ignored setters keep the warning, conditionally ignored ones are an info hint."""

    CONDITIONAL = [
        ("Scp939Events.Attacking", "Scp939", "ev.Target = null;"),
        ("Scp127Events.Talking", "Scp127", "ev.Priority = 3;"),
        ("PlayerEvents.UpdatingEffect", "Player", "ev.Duration = 5f;"),
        ("ServerEvents.CommandExecuting", "Server", 'ev.Command = null;'),
        ("ServerEvents.CommandExecuted", "Server", "ev.ExecutedSuccessfully = true;"),
        ("ServerEvents.CommandExecuted", "Server", 'ev.Response = "x";'),
        ("PlayerEvents.Hurting", "Player", "ev.DamageHandler = null;"),
    ]

    @staticmethod
    def sl003(event, body):
        return [f for f in lint("class A { void E() { %s += ev => { %s }; } }" % (event, body)) if f.rule == "SL003"]

    def test_always_ignored_setter_is_a_warning(self):
        f = self.sl003("PlayerEvents.Dying", "ev.DamageHandler = null;")
        self.assertEqual([(x.severity, x.rule) for x in f], [("warn", "SL003")])
        self.assertIn("is ignored by the game", f[0].message)
        self.assertNotIn("ignored when", f[0].message)

    def test_conditionally_ignored_setters_are_info_and_quote_the_condition(self):
        for event, _group, body in self.CONDITIONAL:
            with self.subTest(event=event, body=body):
                f = self.sl003(event, body)
                self.assertEqual([(x.severity, x.rule) for x in f], [("info", "SL003")])
                self.assertIn("ignored when ", f[0].message)
                self.assertNotIn("is ignored by the game", f[0].message)
                self.assertNotIn("\n", f[0].message)
                # the quoted condition is the catalog text, not a made-up one
                setter = body.split("=")[0].split(".")[1].strip()
                when = dict(scpsl_lint.Catalog.conditionally_ignored_setters(CATALOG.events[event]))[setter]
                self.assertIn(when.rstrip(". "), f[0].message)

    def test_conditional_finding_does_not_fail_the_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(tmp, "a.cs")
            with open(src, "w", encoding="utf-8") as fh:
                fh.write("class A { void E() { PlayerEvents.UpdatingEffect += ev => { ev.Duration = 5f; }; } }\n")
            res = run_cli(src)
            quiet = run_cli(src, "--min-severity", "warn")
        self.assertEqual(res.returncode, 0, res.stdout)
        self.assertIn("info SL003", res.stdout)
        self.assertNotIn("SL003", quiet.stdout)

    def test_unrelated_assignment_is_not_flagged(self):
        # Reading the property, or assigning a different one, is not the trap.
        self.assertEqual(self.sl003("PlayerEvents.UpdatingEffect", "var d = ev.Duration; ev.IsAllowed = false;"), [])


class SuppressionTest(unittest.TestCase):
    def test_same_line(self):
        self.assertEqual(lint("class A { void M() { GC.Collect(); // scpsl-lint: ignore SL010\n } }"), [])

    def test_previous_comment_line(self):
        src = "class A {\n void M() {\n // scpsl-lint: ignore SL010 -- measured, once at map load\n GC.Collect();\n }\n}\n"
        self.assertEqual(lint(src), [])

    def test_other_rule_id_does_not_suppress(self):
        src = "class A { void M() { GC.Collect(); // scpsl-lint: ignore SL004\n } }"
        self.assertEqual(rules_of(lint(src)), ["SL010"])

    def test_previous_code_line_does_not_suppress(self):
        src = "class A {\n void M() {\n int x = 1; // scpsl-lint: ignore SL010\n GC.Collect();\n }\n}\n"
        self.assertEqual(rules_of(lint(src)), ["SL010"])

    def test_ignore_file(self):
        src = "// scpsl-lint: ignore-file SL010\nclass A { void M() { GC.Collect(); Timing.KillCoroutines(); } }\n"
        self.assertEqual(rules_of(lint(src)), ["SL006"])

    def test_bare_ignore_suppresses_everything_on_the_line(self):
        self.assertEqual(lint("class A { void M() { GC.Collect(); Timing.KillCoroutines(); // scpsl-lint: ignore\n } }"), [])


class LexerTest(unittest.TestCase):
    def test_mask_preserves_length_and_newlines(self):
        for path in fixture_paths():
            with self.subTest(fixture=os.path.basename(path)):
                with open(path, encoding="utf-8") as fh:
                    raw = fh.read()
                code, keep = scpsl_lint.mask_source(raw)
                self.assertEqual(len(code), len(raw))
                self.assertEqual(len(keep), len(raw))
                self.assertEqual([i for i, c in enumerate(raw) if c == "\n"],
                                 [i for i, c in enumerate(code) if c == "\n"])

    def test_comments_and_strings_are_masked(self):
        code, keep = scpsl_lint.mask_source('x(); // GC.Collect()\ny = "GC.Collect()"; /* GC.Collect() */ z();')
        self.assertNotIn("GC", code)
        self.assertIn("x();", code)
        self.assertIn("z();", code)
        self.assertIn('"GC.Collect()"', keep)
        self.assertNotIn("// GC", keep)

    def test_interpolation_hole_stays_code(self):
        code, _ = scpsl_lint.mask_source('var s = $"a {Server.Tps} b";')
        self.assertIn("Server.Tps", code)
        self.assertNotIn("{", code)

    def test_unterminated_string_does_not_swallow_the_file(self):
        self.assertEqual(rules_of(lint('class A { void M() { var s = "oops;\n GC.Collect(); } }')), ["SL010"])


class CliTest(unittest.TestCase):
    def test_exit_code_1_when_an_error_is_found(self):
        res = run_cli(os.path.join(FIXTURES, "sl006_kill_all.cs"))
        self.assertEqual(res.returncode, 1)
        self.assertRegex(res.stdout, r"sl006_kill_all\.cs:\d+:\d+: error SL006: ")

    def test_exit_code_0_for_clean_and_warn_only_input(self):
        self.assertEqual(run_cli(os.path.join(FIXTURES, "clean_plugin.cs")).returncode, 0)
        warn_only = run_cli(os.path.join(FIXTURES, "sl014_config_vector4.cs"))
        self.assertEqual(warn_only.returncode, 0)
        self.assertIn("warn SL014", warn_only.stdout)

    def test_json_output(self):
        res = run_cli(os.path.join(FIXTURES, "sl009_sl010_misc.cs"), "--json")
        data = json.loads(res.stdout)
        self.assertEqual(res.returncode, 1)
        self.assertEqual(sorted({d["rule"] for d in data}), ["SL009", "SL010"])
        for d in data:
            self.assertEqual(sorted(d), ["column", "file", "line", "message", "rule", "severity"])
            self.assertEqual(d["severity"], "error")

    def test_json_is_valid_when_there_are_no_findings(self):
        res = run_cli(os.path.join(FIXTURES, "clean_plugin.cs"), "--json")
        self.assertEqual(json.loads(res.stdout), [])
        self.assertEqual(res.returncode, 0)

    def test_min_severity_filters_output_but_not_the_exit_code(self):
        path = os.path.join(FIXTURES, "sl013_assembly_location.cs")
        self.assertEqual(run_cli(path).returncode, 0)
        shown = run_cli(path, "--min-severity", "warn")
        self.assertIn("SL013", shown.stdout)
        hidden = run_cli(path, "--min-severity", "error")
        self.assertNotIn("SL013", hidden.stdout)
        mixed = run_cli(os.path.join(FIXTURES, "sl015_disable_no_shutdown.cs"), "--min-severity", "warn")
        self.assertNotIn("SL015", mixed.stdout)
        errs = run_cli(os.path.join(FIXTURES, "sl006_kill_all.cs"), "--min-severity", "error")
        self.assertEqual(errs.returncode, 1)

    def test_directory_scan_recurses_and_skips_build_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            os.makedirs(os.path.join(tmp, "src", "Sub"))
            os.makedirs(os.path.join(tmp, "obj"))
            with open(os.path.join(tmp, "src", "Sub", "a.cs"), "w") as fh:
                fh.write("class A { void M() { GC.Collect(); } }\n")
            with open(os.path.join(tmp, "obj", "gen.cs"), "w") as fh:
                fh.write("class G { void M() { GC.Collect(); } }\n")
            res = run_cli(tmp, "--json")
        data = json.loads(res.stdout)
        self.assertEqual([os.path.basename(d["file"]) for d in data], ["a.cs"])

    def test_missing_path_is_exit_2(self):
        self.assertEqual(run_cli("/definitely/not/here.cs").returncode, 2)

    def test_disable_option(self):
        res = run_cli(os.path.join(FIXTURES, "sl006_kill_all.cs"), "--disable", "SL006")
        self.assertEqual(res.returncode, 0)

    def test_list_rules(self):
        res = run_cli("--list-rules")
        self.assertEqual(res.returncode, 0)
        for rule in scpsl_lint.RULES:
            self.assertIn(rule, res.stdout)

    def test_sl015_is_project_wide(self):
        # Disable() lives in one file, the Shutdown subscription in another: no finding.
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "Plugin.cs"), "w") as fh:
                fh.write("class P : Plugin { public override void Enable() {}\n"
                         " public override void Disable() { Save(); X.Y -= Z; } }\n")
            alone = run_cli(tmp, "--json")
            with open(os.path.join(tmp, "Wire.cs"), "w") as fh:
                fh.write("class W { void E() { ServerEvents.Shutdown += OnS; } void OnS() {} }\n")
            together = run_cli(tmp, "--json")
        self.assertEqual([d["rule"] for d in json.loads(alone.stdout)], ["SL015"])
        self.assertEqual(json.loads(together.stdout), [])


if __name__ == "__main__":
    unittest.main()
