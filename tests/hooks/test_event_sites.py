"""event-sites.py must count only live event declarations.

LabAPI keeps a retired event as a commented-out declaration. The script used to match the text
inside the comment, so it reported 378 declared events (one more than the catalog) and a phantom
"declared, NOT in catalog" row. These tests build a tiny fake LabAPI tree and check the count.
"""
import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "skills" / "researching-scpsl-internals" / "scripts" / "event-sites.py"


def load():
    spec = importlib.util.spec_from_file_location("event_sites", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class DeclaredTest(unittest.TestCase):
    def declared(self, text):
        mod = load()
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp) / "Events" / "Handlers"
            d.mkdir(parents=True)
            (d / "PlayerEvents.EventHandlers.cs").write_text(text)
            return mod.declared(tmp)

    def test_live_declarations_are_counted(self):
        got = self.declared(
            "public static event LabEventHandler<PlayerJoinedEventArgs>? Joined;\n"
            "public static event LabEventHandler Plain;\n"
        )
        self.assertEqual(got, {"PlayerEvents.Joined", "PlayerEvents.Plain"})

    def test_commented_out_declaration_is_not_an_event(self):
        got = self.declared(
            "    // public static event LabEventHandler<PlayerAimingWeaponEventArgs>? AimingWeapon;\n"
            "    public static event LabEventHandler<PlayerJoinedEventArgs>? Joined;\n"
        )
        self.assertEqual(got, {"PlayerEvents.Joined"})


if __name__ == "__main__":
    unittest.main()
