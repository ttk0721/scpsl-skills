"""Tests for the hot / burst / conditionally hot redesign (SL001, SL018, SL019), the catalog
classification, `using static` resolution and the extra main-thread contexts of SL007.

Run together with test_lint.py:  python3 -m unittest discover -s tools/scpsl-lint/tests
"""
import json
import os
import re
import tempfile
import unittest

import test_lint as base

scpsl_lint = base.scpsl_lint
CATALOG = base.CATALOG
TOOL_DIR = base.TOOL_DIR
lint = base.lint
rules_of = base.rules_of
run_cli = base.run_cli


class CatalogClassificationTest(unittest.TestCase):
    """The catalog must agree with its own definitions (hot / burst / conditionallyHot)."""

    HOT_FREQUENCIES = {"per-frame-per-pair", "per-frame", "high-rate-network"}

    def test_hot_is_exactly_the_per_frame_and_network_classes(self):
        for key, rec in CATALOG.events.items():
            with self.subTest(event=key):
                self.assertEqual(bool(rec["hot"]), rec["frequency"] in self.HOT_FREQUENCIES)

    def test_conditionally_hot_is_exactly_hot_conditions_present(self):
        for key, rec in CATALOG.events.items():
            with self.subTest(event=key):
                self.assertEqual(bool(rec["conditionallyHot"]), bool(rec.get("hotConditions")))

    def test_shot_and_hit_events_are_classed_alike(self):
        # PlacingBulletHole used to be hot while ShootingWeapon / ShotWeapon were not.
        for name in ("ShootingWeapon", "ShotWeapon", "PlacingBulletHole", "PlacedBulletHole", "SendingHitmarker",
                     "SentHitmarker", "CheckedHitmarker", "Hurting", "Hurt", "PlacingBlood"):
            rec = CATALOG.events["PlayerEvents." + name]
            with self.subTest(event=name):
                self.assertTrue(rec["burst"])
                self.assertFalse(rec["hot"])
        for rec in CATALOG.events.values():
            if rec["burst"]:
                self.assertEqual(rec["frequency"], "per-action")

    def test_hurting_is_conditionally_hot_and_not_hot(self):
        for name in ("Hurting", "Hurt"):
            rec = CATALOG.events["PlayerEvents." + name]
            self.assertTrue(rec["conditionallyHot"])
            self.assertFalse(rec["hot"])

    def test_meta_defines_the_three_classes(self):
        for key in ("hotDefinition", "burstDefinition", "conditionallyHotDefinition"):
            self.assertIn(key, CATALOG.meta)

    def test_embedded_hot_list_matches_the_catalog(self):
        path = os.path.join(TOOL_DIR, "..", "SlKit", "src", "Runtime", "HotEventAudit.HotList.cs")
        if not os.path.exists(path):
            self.skipTest("SlKit sources not present")
        with open(path, encoding="utf-8") as fh:
            listed = re.findall(r'^\s+"([A-Za-z0-9]+Events\.[A-Za-z0-9]+)",\s*$', fh.read(), re.M)
        self.assertEqual(listed, [k for k, r in sorted(CATALOG.events.items()) if r["hot"]])

    def test_generated_markdown_has_the_reverify_section_and_the_three_classes(self):
        path = os.path.join(TOOL_DIR, "..", "..", "skills", "labapi-events", "events-catalog.md")
        if not os.path.exists(path):
            self.skipTest("skills not present")
        with open(path, encoding="utf-8") as fh:
            md = fh.read()
        self.assertIn("## Re-verifying on a new build", md)
        self.assertIn("## Burst events", md)
        self.assertIn("**conditionallyHot**", md)


class Sl001Test(unittest.TestCase):
    def test_hot_subscription_is_a_warning_that_cites_the_measurement(self):
        f = lint("class A { void E() { PlayerEvents.ValidatedVisibility += OnV; } void OnV(object e) {} }")
        self.assertEqual(rules_of(f), ["SL001"])
        self.assertEqual(f[0].severity, "warn")
        self.assertIn("measured cheap", f[0].message)
        self.assertIn("tools/hot-bench/README.md", f[0].message)
        self.assertNotIn("avoid it", f[0].message)

    def test_messages_carry_no_benchmark_numbers(self):
        # The figures live in tools/hot-bench/README.md only; copies go stale in many places at once.
        for src in ("class A { void E() { PlayerEvents.ValidatedVisibility += OnV; } void OnV(object e) {} }",
                    "class A { void E() { PlayerEvents.Hurting += OnH; } void OnH(object e) {} }",
                    "class H : CustomEventsHandler { public override void OnPlayerValidatedVisibility(object e) "
                    '{ Logger.Debug("x"); } }'):
            for f in lint(src):
                with self.subTest(rule=f.rule):
                    self.assertNotRegex(f.message, r"\d+(?:[.,]\d+)?\s*(?:ms|raises)|dummies|116")

    def test_a_cheap_gated_handler_gives_only_the_warning(self):
        src = ("class A { static bool on; void E() { PlayerEvents.ValidatedVisibility += OnV; }\n"
               " void OnV(PlayerValidatedVisibilityEventArgs ev) { if (!on) return; counter++; } int counter; }")
        self.assertEqual(rules_of(lint(src)), ["SL001"])

    def test_conditionally_hot_event_warns_and_names_the_condition(self):
        f = lint("class A { void E() { PlayerEvents.Hurting += OnH; } void OnH(object e) {} }")
        self.assertEqual(rules_of(f), ["SL001"])
        self.assertIn("conditionally hot", f[0].message)
        self.assertIn("hotConditions", f[0].message)
        self.assertNotIn("\n", f[0].message)

    def test_conditionally_hot_message_reads_as_clean_sentences(self):
        # The catalog text carries parenthetical asides and several sentences; the message must not
        # quote half of one (a cut mid-parenthesis, a stray "...)" or a doubled full stop).
        for event in ("PlayerEvents.Hurting", "PlayerEvents.Hurt"):
            with self.subTest(event=event):
                f = lint("class A { void E() { %s += OnH; } void OnH(object e) {} }" % event)
                msg = f[0].message
                self.assertEqual(msg.count("("), msg.count(")"))
                self.assertNotIn("...", msg)
                self.assertNotIn("..", msg)
                self.assertNotIn(" ,", msg)
                self.assertNotIn(" ;", msg)
                self.assertIn("Catalog hotConditions: Per-frame per victim", msg)
                self.assertRegex(msg, r"hotConditions: [^.]+\. Keep the handler trivial")

    def test_summarize_condition(self):
        s = scpsl_lint.summarize_condition
        self.assertEqual(s("Only while a (rare) thing runs (see notes, e.g. X). Baseline is low."),
                         "Only while a thing runs")
        self.assertEqual(s(None), "")
        self.assertEqual(s("  spaced   out  "), "spaced out")
        long = "word " * 80
        out = s(long, limit=50)
        self.assertTrue(out.endswith(" ..."))
        self.assertLessEqual(len(out), 54)

    def test_burst_only_event_has_no_sl001(self):
        self.assertEqual(lint("class A { void E() { PlayerEvents.ShootingWeapon += OnS; } void OnS(object e) {} }"), [])

    def test_exit_code_is_zero_for_a_subscription_alone(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(tmp, "a.cs")
            with open(src, "w", encoding="utf-8") as fh:
                fh.write("class A { void E() { PlayerEvents.ValidatedVisibility += OnV; } void OnV(object e) {} }\n")
            res = run_cli(src)
        self.assertEqual(res.returncode, 0, res.stdout)
        self.assertIn("warn SL001", res.stdout)

    def test_using_static_import_resolves_the_bare_event_name(self):
        src = ("using static LabApi.Events.Handlers.PlayerEvents;\n"
               "class A { void E() { ValidatedVisibility += OnV; } void OnV(object e) {} }")
        f = lint(src)
        self.assertEqual(rules_of(f), ["SL001"])
        self.assertIn("PlayerEvents.ValidatedVisibility", f[0].message)

    def test_bare_name_without_the_import_is_not_an_event(self):
        self.assertEqual(lint("class A { void E() { ValidatedVisibility += OnV; } void OnV(object e) {} }"), [])

    def test_using_static_of_an_unrelated_class_is_ignored(self):
        src = ("using static System.Math;\n"
               "class A { void E() { ValidatedVisibility += OnV; } void OnV(object e) {} }")
        self.assertEqual(lint(src), [])

    def test_using_static_handler_body_is_inspected(self):
        src = ("using static LabApi.Events.Handlers.PlayerEvents;\n"
               "class A { void E() { ValidatedVisibility += OnV; }\n"
               ' void OnV(object e) { Logger.Debug("x"); } }')
        self.assertEqual(rules_of(lint(src)), ["SL001", "SL018"])

    def test_member_access_with_the_same_name_is_not_a_subscription(self):
        src = ("using static LabApi.Events.Handlers.PlayerEvents;\n"
               "class A { void E() { other.ValidatedVisibility += OnV; this.ReceivingVoiceMessage += OnV; } "
               "void OnV(object e) {} }")
        self.assertEqual(lint(src), [])

    def test_alias_import_resolves(self):
        src = ("using PE = LabApi.Events.Handlers.PlayerEvents;\n"
               "class A { void E() { PE.ReceivingVoiceMessage += OnV; } void OnV(object e) {} }")
        self.assertEqual(rules_of(lint(src)), ["SL001"])

    def test_two_static_imports_declaring_the_same_name_are_skipped(self):
        # `Attacking` exists in Scp049Events and Scp939Events: C# would not compile a bare use.
        src = ("using static LabApi.Events.Handlers.Scp049Events;\n"
               "using static LabApi.Events.Handlers.Scp939Events;\n"
               "class A { void E() { Attacking += OnA; } void OnA(object e) {} }")
        self.assertEqual(lint(src), [])


class HandlerWorkTest(unittest.TestCase):
    def hot(self, body, event="OnPlayerValidatedVisibility", args="PlayerValidatedVisibilityEventArgs ev"):
        return lint("class H : CustomEventsHandler {\n public override void %s(%s)\n {\n%s\n }\n}\n"
                    % (event, args, body))

    def test_each_kind_of_work_is_an_error_in_a_hot_handler(self):
        cases = {
            'Logger.Debug("x");': "Logger.Debug",
            'var s = $"{ev.Player}";': "string interpolation",
            'var s = a + "x";': "string concatenation",
            'var s = string.Format("{0}", a);': "string.Format",
            "var l = new List<int>();": "allocation",
            "var l = new int[4];": "allocation",
            "var l = items.Where(x => x > 1);": "LINQ",
            "var l = items.ToList();": "LINQ",
            "lock (gate) { n++; }": "lock",
            "Monitor.Enter(gate);": "lock",
            "foreach (var p in Player.ReadyList) { n++; }": "player list enumeration",
            "var n = Player.Count;": "Player.Count",
            'throw new Exception("x");': "throw",
            'var t = File.Exists("a");': "file IO",
            'var t = client.GetStringAsync("u");': "network IO",
            "Timing.CallDelayed(1f, () => { });": "Timing.CallDelayed",
            "Timing.RunCoroutine(Loop());": "Timing.RunCoroutine",
        }
        for body, label in cases.items():
            with self.subTest(body=body):
                f = self.hot(body)
                self.assertEqual(rules_of(f), ["SL001", "SL018"], [x.format() for x in f])
                sl018 = [x for x in f if x.rule == "SL018"][0]
                self.assertEqual(sl018.severity, "error")
                self.assertIn(label, sl018.message)

    def test_cheap_handler_body_is_clean(self):
        body = ("if (!enabled) return; counter++; var k = set.Contains(ev.Player.PlayerId);\n"
                "var v = new Vector3(0f, 1f, 0f); var q = new Quaternion(0, 0, 0, 1);\n"
                "var m = Math.Max(1, 2); var n = Player.List.Count; var c = new Color(1f, 1f, 1f);")
        self.assertEqual(rules_of(self.hot(body)), ["SL001"])

    def test_all_work_on_one_line_is_one_finding_naming_everything(self):
        f = self.hot('Logger.Debug($"{ev.Player} " + x);')
        sl018 = [x for x in f if x.rule == "SL018"]
        self.assertEqual(len(sl018), 1)
        for label in ("Logger.Debug", "string interpolation", "string concatenation"):
            self.assertIn(label, sl018[0].message)

    def test_burst_handler_gets_a_warning_not_an_error(self):
        f = self.hot('Logger.Info("shot");', event="OnPlayerShootingWeapon", args="PlayerShootingWeaponEventArgs ev")
        self.assertEqual(rules_of(f), ["SL019"])
        self.assertEqual(f[0].severity, "warn")

    def test_conditionally_hot_handler_gets_the_error(self):
        f = self.hot('Logger.Info("hurt");', event="OnPlayerHurting", args="PlayerHurtingEventArgs ev")
        self.assertEqual(rules_of(f), ["SL001", "SL018"])

    def test_method_subscribed_to_hot_and_burst_events_takes_the_strict_rule(self):
        src = ("class A { void E() { PlayerEvents.ShootingWeapon += OnX; PlayerEvents.ValidatedVisibility += OnX; }\n"
               ' void OnX(object e) { Logger.Debug("x"); } }')
        self.assertEqual(rules_of(lint(src)), ["SL001", "SL018"])

    def test_ordinary_event_is_not_policed(self):
        f = lint("class H : CustomEventsHandler { public override void OnPlayerJoined(PlayerJoinedEventArgs ev) {\n"
                 ' Logger.Info("joined " + ev.Player); lock (g) { } var l = new List<int>(); } }')
        self.assertEqual(f, [])

    def test_sl007_wins_over_sl018_on_the_same_line(self):
        f = self.hot('File.WriteAllText("a", "b");')
        self.assertEqual(rules_of(f), ["SL001", "SL007"])

    def test_catch_body_is_the_failure_path_and_is_skipped(self):
        body = ('try { counter++; } catch (Exception e)\n { RateLimitedLog.Error("k", () => "failed: " + e); '
                'Logger.Error("x" + e); }')
        self.assertEqual(rules_of(self.hot(body)), ["SL001"])

    def test_work_after_the_catch_block_is_still_reported(self):
        body = 'try { counter++; } catch (Exception e) { }\nLogger.Debug("x");'
        self.assertEqual(rules_of(self.hot(body)), ["SL001", "SL018"])

    def test_lazy_init_allocation_is_amortised_and_skipped(self):
        body = ("if (!used.TryGetValue(id, out var u))\n { u = new Used(); used[id] = u; }\n"
                "if (cache == null)\n { cache = new List<int>(); }")
        self.assertEqual(rules_of(self.hot(body)), ["SL001"])

    def test_allocation_outside_a_lazy_init_is_reported(self):
        self.assertEqual(rules_of(self.hot("var x = new Used();")), ["SL001", "SL018"])

    def test_work_outside_a_handler_is_not_reported(self):
        src = 'class A { void M() { Logger.Debug("x" + y); lock (z) { } var l = new List<int>(); } }'
        self.assertEqual(lint(src), [])

    def test_suppression_with_a_reason_works(self):
        f = self.hot('Logger.Debug("x"); // scpsl-lint: ignore SL018 -- measured, behind a debug flag')
        self.assertEqual(rules_of(f), ["SL001"])

    def test_sl008_and_sl005_hand_over_inside_hot_handlers(self):
        f = self.hot('Logger.Debug("x"); Timing.CallDelayed(1f, () => { });')
        self.assertEqual(sorted(set(rules_of(f))), ["SL001", "SL018"])

    def test_catalog_is_followed_at_runtime_for_the_work_rule(self):
        # A burst event made hot in a custom catalog turns SL019 into SL018.
        with open(scpsl_lint.default_catalog_path(), encoding="utf-8") as fh:
            data = json.load(fh)
        for rec in data["events"]:
            if rec["event"] == "PlayerEvents.ShootingWeapon":
                rec["hot"] = True
        cat = scpsl_lint.Catalog(data)
        src = ("class H : CustomEventsHandler { public override void OnPlayerShootingWeapon(object ev) "
               '{ Logger.Info("x"); } }')
        self.assertEqual(rules_of(scpsl_lint.lint_texts([("a.cs", src)], cat)), ["SL001", "SL018"])
        self.assertEqual(rules_of(lint(src)), ["SL019"])


class Sl007ContextTest(unittest.TestCase):
    def test_blocking_calls_in_command_execute(self):
        src = ("class C : ICommand {\n"
               " public bool Execute(ArraySegment<string> a, ICommandSender s, out string r)\n {\n"
               '  Thread.Sleep(5000);\n  File.WriteAllText("a", "b");\n  r = ""; return true;\n }\n}\n')
        f = lint(src)
        self.assertEqual(rules_of(f), ["SL007", "SL007"])
        self.assertEqual([x.line for x in f], [4, 5])

    def test_execute_parent_of_a_parent_command(self):
        src = ("class P : ParentCommand {\n"
               " protected override bool ExecuteParent(ArraySegment<string> a, ICommandSender s, out string r)\n"
               ' {\n  Thread.Sleep(1);\n  r = ""; return true;\n }\n}\n')
        self.assertEqual(rules_of(lint(src)), ["SL007"])

    def test_execute_of_an_unrelated_class_is_not_a_command(self):
        self.assertEqual(lint("class X { void Execute() { Thread.Sleep(1); } }"), [])

    def test_slclock_every_lambda_is_main_thread(self):
        src = 'class A { void E() { SlClock.Every(TimeSpan.FromSeconds(1), () => File.AppendAllText("l", "t"), "n"); } }'
        self.assertEqual(rules_of(lint(src)), ["SL007"])

    def test_slclock_method_group_is_followed(self):
        src = ('class A { void E() { SlClock.Every(TimeSpan.FromSeconds(1), Flush, "n"); }\n'
               ' void Flush() { File.AppendAllText("l", "t"); } void Other() { File.AppendAllText("l", "t"); } }')
        self.assertEqual([x.line for x in lint(src)], [2])

    def test_after_everystaggered_and_mainthread_post(self):
        for call in ('SlClock.After(t, () => { Thread.Sleep(1); }, "n")',
                     'SlClock.EveryStaggered(t, () => src, p => Thread.Sleep(1), "n")',
                     "MainThread.Post(() => Thread.Sleep(1))",
                     "Timing.CallDelayed(1f, () => Thread.Sleep(1))",
                     'SlKit.SlClock.Every(t, () => Thread.Sleep(1), "n")'):
            with self.subTest(call=call):
                self.assertEqual(rules_of(lint("class A { void E() { %s; } }" % call)), ["SL007"])

    def test_background_tryrun_work_is_off_thread_but_its_callback_is_not(self):
        src = ('class A { void E() {\n Background.TryRun(\n  () => File.ReadAllText("a"),\n'
               '  text => File.WriteAllText("b", text));\n } }')
        self.assertEqual([x.line for x in lint(src)], [4])

    def test_task_run_inside_a_main_thread_context_stays_quiet(self):
        src = 'class A { void E() { SlClock.Every(t, () => { Task.Run(() => Thread.Sleep(5)); }, "n"); } }'
        self.assertEqual(rules_of(lint(src)), [])

    def test_fully_qualified_spellings(self):
        for stmt in ("using (var w = new System.Net.WebClient()) { }",
                     "System.Threading.Thread.Sleep(1);",
                     'System.IO.File.ReadAllText("a");',
                     "var w = new global::System.Net.WebClient();"):
            with self.subTest(stmt=stmt):
                src = "class H : CustomEventsHandler { public override void OnPlayerJoined(object ev) { %s } }" % stmt
                self.assertEqual(rules_of(lint(src)), ["SL007"])

    def test_qualified_thread_and_task_count_as_off_thread(self):
        src = ("class H : CustomEventsHandler { public override void OnPlayerJoined(object ev) {\n"
               " var t = new System.Threading.Thread(() => Thread.Sleep(5));\n"
               " System.Threading.Tasks.Task.Run(() => Thread.Sleep(5)); } }")
        self.assertEqual(lint(src), [])


class Sl007EveryCallTest(unittest.TestCase):
    """N6(a): several blocking calls on one line are all reported, each at its own column."""

    CMD = ("class C : ICommand {\n"
           " public bool Execute(ArraySegment<string> a, ICommandSender s, out string r)\n {\n%s\n"
           '  r = ""; return true;\n }\n}\n')

    def test_two_file_calls_on_one_line_are_two_findings(self):
        f = lint(self.CMD % '  File.WriteAllText("a", "b"); File.ReadAllText("a");')
        self.assertEqual(rules_of(f), ["SL007", "SL007"])
        self.assertEqual([x.line for x in f], [4, 4])
        self.assertEqual(len({x.column for x in f}), 2)

    def test_mixed_blocking_kinds_on_one_line(self):
        f = lint(self.CMD % '  Thread.Sleep(1); task.Wait(); var t = x.GetAwaiter().GetResult();')
        self.assertEqual([x.column for x in f], sorted({x.column for x in f}))
        self.assertEqual(len(f), 3)

    def test_ignore_comment_still_silences_the_whole_line(self):
        f = lint(self.CMD % '  File.WriteAllText("a", "b"); File.ReadAllText("a"); // scpsl-lint: ignore SL007')
        self.assertEqual(f, [])

    def test_async_file_io_is_not_a_blocking_call(self):
        self.assertEqual(lint(self.CMD % '  var t = File.WriteAllTextAsync("a", "b");'), [])


class Sl007PropagationTest(unittest.TestCase):
    """N6(b): same-file helpers called from a main-thread context are main-thread contexts."""

    CMD = ("class C : ICommand {\n"
           " public bool Execute(ArraySegment<string> a, ICommandSender s, out string r)\n {\n%s\n"
           '  r = ""; return true;\n }\n%s\n}\n')

    def cmd(self, body, extra):
        return lint(self.CMD % (body, extra))

    def test_helper_called_from_execute_is_reported_and_names_the_path(self):
        f = self.cmd("  Save();", " void Save() { Thread.Sleep(5); }")
        self.assertEqual(rules_of(f), ["SL007"])
        self.assertIn("reached from Execute", f[0].message)
        self.assertIn("Save()", f[0].message)

    def test_three_levels_are_followed_and_the_fourth_is_not(self):
        extra = (" void A() { B(); }\n void B() { C(); }\n void C() { Thread.Sleep(1); D(); }\n"
                 " void D() { Thread.Sleep(2); }")
        f = self.cmd("  A();", extra)
        self.assertEqual([x.line for x in f], [9])   # C (3rd level) yes, D (4th level) no
        self.assertIn("Execute -> A -> B -> C", f[0].message)

    def test_helper_called_from_an_event_handler(self):
        src = ("class A { void E() { PlayerEvents.Joined += OnJ; }\n void OnJ(object e) { Persist(); }\n"
               ' void Persist() { File.AppendAllText("l", "t"); } }')
        f = lint(src)
        self.assertEqual(rules_of(f), ["SL007"])
        self.assertIn("reached from OnJ", f[0].message)

    def test_helper_called_from_update_and_a_scheduler_lambda(self):
        src = ("class A { void Update() { Poll(); } void Poll() { Thread.Sleep(1); }\n"
               ' void E() { SlClock.Every(t, () => Tick(), "n"); } void Tick() { Thread.Sleep(2); } }')
        f = lint(src)
        self.assertEqual([x.line for x in f], [1, 2])
        self.assertIn("reached from Update", f[0].message)
        self.assertIn("SlClock.Every callback", f[1].message)

    def test_this_and_type_qualified_calls_are_followed(self):
        f = self.cmd("  this.A(); C.B();", " void A() { Thread.Sleep(1); }\n static void B() { Thread.Sleep(2); }")
        self.assertEqual(len(f), 2)

    def test_calls_inside_offthread_work_are_not_followed(self):
        extra = " void W() { Thread.Sleep(1); }\n void P() { Thread.Sleep(2); }\n string L() { return File.ReadAllText(\"x\"); }"
        for body in ("  Task.Run(() => W());", "  Task.Run(W);", "  new Thread(P).Start();",
                     "  Background.TryRun(() => L(), t => { }, e => { });"):
            with self.subTest(body=body):
                self.assertEqual(self.cmd(body, extra), [])

    def test_a_method_also_run_on_a_worker_is_never_a_main_thread_helper(self):
        # Conservative: handing the method to a worker anywhere in the file keeps it quiet.
        f = self.cmd("  W(); Task.Run(W);", " void W() { Thread.Sleep(1); }")
        self.assertEqual(f, [])

    def test_unreached_helper_and_other_receivers_stay_quiet(self):
        extra = (" void Orphan() { Thread.Sleep(1); }\n void Foreign() { Thread.Sleep(2); }\n"
                 " void Local() { void Inner() { Thread.Sleep(3); } }")
        self.assertEqual(self.cmd("  other.Foreign();", extra), [])

    def test_a_same_named_method_of_another_class_is_not_followed(self):
        src = ("class C : ICommand {\n"
               " public bool Execute(ArraySegment<string> a, ICommandSender s, out string r) { Flush(); r = \"\"; return true; }\n"
               " void Flush() { }\n}\n"
               "class Other { void Flush() { Thread.Sleep(1); } }\n")
        self.assertEqual(lint(src), [])

    def test_shutdown_handler_may_flush_synchronously(self):
        # SL015 tells plugins to flush synchronously in ServerEvents.Shutdown; that is not an SL007.
        src = ("class A { void E() { ServerEvents.Shutdown += OnS; }\n void OnS() { Flush(); }\n"
               ' void Flush() { File.WriteAllText("s", "x"); } }')
        self.assertEqual(lint(src), [])

    def test_recursion_terminates(self):
        f = self.cmd("  A();", " void A() { B(); Thread.Sleep(1); }\n void B() { A(); }")
        self.assertEqual(rules_of(f), ["SL007"])

    def test_suppression_works_on_a_helper_line(self):
        f = self.cmd("  Save();", " void Save() { Thread.Sleep(5); } // scpsl-lint: ignore SL007")
        self.assertEqual(f, [])


class RepoSourcesTest(unittest.TestCase):
    def test_the_repos_own_c_sharp_has_no_errors(self):
        # HotBench subscribes to ValidatedVisibility on purpose (SL001 warning); it must carry no error.
        tools = os.path.normpath(os.path.join(TOOL_DIR, ".."))
        dirs = [os.path.join(tools, d, "src") for d in ("hot-bench", "SlKit", "sl-probe")]
        dirs = [d for d in dirs if os.path.isdir(d)]
        if not dirs:
            self.skipTest("repo sources not present")
        res = run_cli(*dirs)
        self.assertEqual(res.returncode, 0, res.stdout)
        self.assertNotIn("error ", res.stdout)


if __name__ == "__main__":
    unittest.main()
