#!/usr/bin/env python3
"""The running-a-test-server scripts must not hang, must not accept the EULA on their own, must not
signal a process they cannot prove is theirs, and must refuse non-Linux systems.

Nothing here touches a real server: LocalAdmin is a stub shell script in a temp install folder, HOME,
the config root and the state folder are all temp folders, and every call has a timeout.

Regressions covered:
  - "start-server.sh 7777 --install" (flag LAST) looped forever: "shift 2" with one argument left shifts
    nothing.
  - --acceptEULA was always passed, also on installs the owner never ran.
  - stop-server.sh --kill sent SIGTERM to the process GROUP of whatever pid the pid file held; a reused
    pid that led its own group would have been killed with all its children. Plain stop also wrote to the
    FIFO with a blocking open and hung when no server held it.
  - start-server.sh searched Steam libraries while a doc said "do not search the disk": the rule is now
    default locations plus libraryfolders.vdf, and nothing deeper.
Run: python3 -m unittest discover -s tests/hooks
"""
import os
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import time
import unittest

SCRIPTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "skills", "running-a-test-server", "scripts")
# Far from 7777/7778/9999 and above the privileged range; nothing of the owner's lives here.
PORT = "47123"
TIMEOUT = 20

STUB = """#!/usr/bin/env bash
# Stand-in for LocalAdmin. Records its arguments, prints the boot line, then reads the console.
printf '%s\\n' "$@" > "$STUB_ARGS_FILE"
# A server that shrugs off SIGTERM, to prove stop-server.sh checks the effect of what it sends.
[ -n "${STUB_IGNORE_TERM:-}" ] && trap '' TERM
# A "game child" in the same process group that also ignores SIGTERM (the ignore survives exec).
if [ -n "${STUB_CHILD_IGNORE_TERM:-}" ]; then ( trap '' TERM; exec sleep 120 ) & fi
echo "Waiting for players..."
while IFS= read -r line; do
  if [ "$line" = exit ] && [ -z "${STUB_IGNORE_EXIT:-}" ]; then
    exit 0
  fi
  echo "got: $line"
done
"""

DEFAULT_ENV_DROP = ("SLT_INSTALL", "SLT_CONFIG_ROOT", "SLT_RUN_DIR", "SLT_WAIT", "SLT_STOP_WAIT",
                    "SLT_KILL_WAIT", "SLT_PROC_NET",
                    "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME")


class Sandbox:
    """A throwaway home, config root, state folder and one stub install."""

    def __init__(self, root):
        self.root = root
        self.home = os.path.join(root, "home")
        self.cfg = os.path.join(root, "cfg", "SCP Secret Laboratory")
        self.run_dir = os.path.join(root, "run")
        self.install = os.path.join(root, "install")
        self.args_file = os.path.join(root, "args.txt")
        os.makedirs(self.home)
        os.makedirs(os.path.join(self.cfg, "config"))
        self.make_install(self.install)

    def make_install(self, path):
        os.makedirs(os.path.join(path, "SCPSL_Data"), exist_ok=True)
        launcher = os.path.join(path, "LocalAdmin")
        with open(launcher, "w") as f:
            f.write(STUB)
        os.chmod(launcher, os.stat(launcher).st_mode | stat.S_IXUSR)

    def record_eula(self, value):
        with open(os.path.join(self.cfg, "config", "localadmin_internal_data.json"), "w", encoding="utf-8-sig") as f:
            f.write('{"GitHubPersonalAccessToken":null,"EulaAccepted":%s,"PluginManagerWarningDismissed":false}' % value)

    def env(self, **extra):
        env = {k: v for k, v in os.environ.items() if k not in DEFAULT_ENV_DROP}
        env.update(HOME=self.home, SLT_CONFIG_ROOT=self.cfg, SLT_RUN_DIR=self.run_dir,
                   SLT_INSTALL=self.install, STUB_ARGS_FILE=self.args_file)
        env.update(extra)
        return env

    def state(self, port=PORT):
        return os.path.join(self.run_dir, port)

    def pidfile(self, port=PORT):
        return os.path.join(self.state(port), "server.pid")


def read_file(path, mode="r"):
    with open(path, mode) as f:
        return f.read()


def script(name):
    return os.path.join(SCRIPTS, name)


class ScriptTestCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.sb = Sandbox(self._tmp.name)
        self._spawned = []  # Popen objects started by a test
        self.addCleanup(self._reap)

    def _reap(self):
        # Whatever a test started must not outlive it.
        for proc in self._spawned:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                pass
            proc.kill() if proc.poll() is None else None
            proc.wait()
        # Every state folder, not only PORT: a test that starts a server elsewhere must not leak it.
        if os.path.isdir(self.sb.run_dir):
            for entry in os.listdir(self.sb.run_dir):
                pf = os.path.join(self.sb.run_dir, entry, "server.pid")
                if os.path.isfile(pf):
                    try:
                        os.killpg(int(read_file(pf).strip()), signal.SIGKILL)
                    except (ValueError, ProcessLookupError, PermissionError, OSError):
                        pass

    def run_script(self, name, *args, env=None, timeout=TIMEOUT):
        """Runs a script; a hang fails the test (TimeoutExpired is turned into a failure)."""
        try:
            out = subprocess.run(["bash", script(name), *args], capture_output=True, text=True,
                                 timeout=timeout, env=env or self.sb.env(), stdin=subprocess.DEVNULL)
        except subprocess.TimeoutExpired as e:
            self.fail(f"{name} {' '.join(args)} hung (> {timeout}s): {e.stderr!r}")
        return out

    def start(self, *extra, env=None):
        return self.run_script("start-server.sh", PORT, "--wait", "15", *extra, env=env)

    def alive(self, pid):
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        # A zombie still answers kill 0; /proc tells them apart.
        try:
            with open(f"/proc/{pid}/stat") as f:
                return f.read().rsplit(") ", 1)[1].split()[0] != "Z"
        except OSError:
            return False

    def lookup_env(self):
        """An environment without SLT_INSTALL: the scripts must find installs on their own."""
        env = self.sb.env()
        del env["SLT_INSTALL"]
        return env

    def make_library(self, library, install_dir):
        """A Steam library named by libraryfolders.vdf, holding one stub install. Returns its path."""
        steam = os.path.join(self.sb.home, ".local", "share", "Steam")
        os.makedirs(os.path.join(steam, "steamapps"), exist_ok=True)
        os.makedirs(os.path.join(library, "steamapps", "common"), exist_ok=True)
        with open(os.path.join(steam, "steamapps", "libraryfolders.vdf"), "w") as f:
            f.write('"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"%s"\n\t}\n}\n' % library)
        installed = os.path.join(library, "steamapps", "common", install_dir)
        self.sb.make_install(installed)
        return installed

    def wait_gone(self, pid, seconds=5):
        end = time.time() + seconds
        while time.time() < end:
            if not self.alive(pid):
                return True
            time.sleep(0.1)
        return False


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class ArgumentParsingTest(ScriptTestCase):
    """#1: a flag without its value is an error, never a loop."""

    def test_start_flag_without_value_is_usage_error(self):
        for flag in ("--install", "--wait"):
            with self.subTest(flag=flag):
                out = self.run_script("start-server.sh", PORT, flag, timeout=10)
                self.assertEqual(out.returncode, 64, out.stderr)
                self.assertIn("needs a value", out.stderr)

    def test_start_flag_followed_by_another_flag_is_usage_error(self):
        out = self.run_script("start-server.sh", PORT, "--install", "--accept-eula", timeout=10)
        self.assertEqual(out.returncode, 64, out.stderr)

    def test_start_rejects_bad_values_and_unknown_flags(self):
        for args in (["--wait", "soon"], ["--bogus"], ["abc"]):
            with self.subTest(args=args):
                port_arg = PORT if args[0].startswith("--") else args[0]
                rest = args if args[0].startswith("--") else []
                out = self.run_script("start-server.sh", port_arg, *rest, timeout=10)
                self.assertEqual(out.returncode, 64, out.stderr)

    def test_other_scripts_reject_missing_values(self):
        cases = [
            ("list-created.sh", [PORT, "--since"]),
            ("list-created.sh", [PORT, "--since", "yesterday"]),
            ("list-created.sh", [PORT, "--nope"]),
            ("stop-server.sh", [PORT, "--nope"]),
            ("list-ports.sh", ["--has"]),
            ("list-ports.sh", ["--nope"]),
            ("check-private.sh", [PORT, "a.log", "extra"]),
            ("send-command.sh", [PORT]),
            ("find-install.sh", ["extra"]),
        ]
        for name, args in cases:
            with self.subTest(script=name, args=args):
                out = self.run_script(name, *args, timeout=10)
                self.assertEqual(out.returncode, 64, out.stdout + out.stderr)


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class EulaTest(ScriptTestCase):
    """#17: --acceptEULA only with an explicit opt-in, or a recorded acceptance."""

    def args_passed(self):
        with open(self.sb.args_file) as f:
            return f.read().split()

    def test_refuses_without_opt_in_and_leaves_no_trace(self):
        out = self.start()
        self.assertEqual(out.returncode, 7, out.stdout + out.stderr)
        self.assertIn("ASK", out.stderr)
        self.assertFalse(os.path.exists(self.sb.args_file), "LocalAdmin must not have been started")
        self.assertFalse(os.path.exists(self.sb.state()), "a refusal must not create state")

    def test_null_record_is_not_an_acceptance(self):
        self.sb.record_eula("null")
        out = self.start()
        self.assertEqual(out.returncode, 7, out.stdout + out.stderr)

    def test_flag_opts_in(self):
        out = self.start("--accept-eula")
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertIn("--acceptEULA", self.args_passed())
        self.assertIn("READY", out.stdout)
        self.assertEqual(self.run_script("stop-server.sh", PORT).returncode, 0)

    def test_recorded_acceptance_starts_without_the_flag_and_is_never_rewritten(self):
        # N8: the record is machine-wide, so it must not make the script pass --acceptEULA for the owner.
        # LocalAdmin does not prompt when an acceptance is stored, so starting without the flag is enough.
        self.sb.record_eula('"2026-04-11T12:13:50.4107586Z"')
        record = os.path.join(self.sb.cfg, "config", "localadmin_internal_data.json")
        before = read_file(record, "rb")
        out = self.start()
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertIn("2026-04-11T12:13:50", out.stdout)
        self.assertNotIn("--acceptEULA", self.args_passed())
        self.assertEqual(read_file(record, "rb"), before, "the owner's file is read-only for us")
        self.assertEqual(self.run_script("stop-server.sh", PORT).returncode, 0)

    def test_recorded_acceptance_plus_explicit_flag_passes_it(self):
        self.sb.record_eula('"2026-04-11T12:13:50.4107586Z"')
        out = self.start("--accept-eula")
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertIn("--acceptEULA", self.args_passed())
        self.assertEqual(self.run_script("stop-server.sh", PORT).returncode, 0)

    def test_acceptEULA_is_never_in_the_launch_line_unless_the_flag_was_given(self):
        # Source-level guard: the only place that may add --acceptEULA is the --accept-eula branch.
        src = read_file(script("start-server.sh"))
        branch = src.split('if [ "$accept_eula" -eq 1 ]; then', 1)[1].split("elif", 1)[0]
        self.assertIn("--acceptEULA", branch)
        self.assertEqual(src.count("eula_args=(--acceptEULA)"), 1)


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class StopSafetyTest(ScriptTestCase):
    """#18: never signal, and never block on, a pid that is not our LocalAdmin."""

    def spawn_stranger(self, *argv):
        """A live process that leads its own process group, like a reused pid could."""
        proc = subprocess.Popen(argv or ["sleep", "60"], start_new_session=True,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self._spawned.append(proc)
        return proc

    def plant_state(self, pid):
        os.makedirs(self.sb.state(), exist_ok=True)
        with open(self.sb.pidfile(), "w") as f:
            f.write(f"{pid}\n")
        fifo = os.path.join(self.sb.state(), "console.fifo")
        os.mkfifo(fifo)  # nobody reads it: a plain "> fifo" would block forever
        return fifo

    def test_unrelated_process_is_not_signalled_not_even_with_kill(self):
        # A group leader with a child in its group: SIGTERM to "-pid" would take both.
        stranger = self.spawn_stranger("bash", "-c", "sleep 60 & wait")
        time.sleep(0.3)
        members = subprocess.run(["ps", "-o", "pid=", "-g", str(stranger.pid)], capture_output=True, text=True).stdout.split()
        self.assertGreaterEqual(len(members), 2, "the test needs a second process in the group")
        self.plant_state(stranger.pid)
        env = self.sb.env(SLT_STOP_WAIT="1")
        for extra in ([], ["--kill"]):
            with self.subTest(extra=extra):
                out = self.run_script("stop-server.sh", PORT, *extra, env=env, timeout=15)
                self.assertEqual(out.returncode, 6, out.stdout + out.stderr)
                self.assertIn("Refusing", out.stderr)
                self.assertTrue(self.alive(stranger.pid), "the stranger must survive")
                after = subprocess.run(["ps", "-o", "pid=", "-g", str(stranger.pid)], capture_output=True, text=True).stdout.split()
                self.assertEqual(len(after), len(members), "its whole group must survive")
        self.assertTrue(os.path.isfile(self.sb.pidfile()), "an unverified pid file is left for a human")

    def test_same_launcher_name_but_other_port_is_refused(self):
        # A LocalAdmin for the owner's port must never be taken for ours.
        fake = os.path.join(self._tmp.name, "other", "LocalAdmin")
        os.makedirs(os.path.dirname(fake))
        with open(fake, "w") as f:
            f.write("#!/usr/bin/env bash\nsleep 60\n")  # no exec: the process keeps the LocalAdmin name
        os.chmod(fake, 0o755)
        stranger = self.spawn_stranger(fake, "7777")
        time.sleep(0.3)
        self.plant_state(stranger.pid)
        out = self.run_script("stop-server.sh", PORT, "--kill", env=self.sb.env(SLT_STOP_WAIT="1"), timeout=15)
        self.assertEqual(out.returncode, 6, out.stdout + out.stderr)
        self.assertIn("not for port", out.stderr)
        self.assertTrue(self.alive(stranger.pid))

    def test_pid_that_started_after_the_pid_file_is_refused(self):
        # Right name, right port, but born after the pid file: the signature of a reused pid.
        fake = os.path.join(self._tmp.name, "reuse", "LocalAdmin")
        os.makedirs(os.path.dirname(fake))
        with open(fake, "w") as f:
            f.write("#!/usr/bin/env bash\nsleep 60\n")
        os.chmod(fake, 0o755)
        stranger = self.spawn_stranger(fake, PORT)
        time.sleep(0.3)
        self.plant_state(stranger.pid)
        long_ago = time.time() - 3600
        os.utime(self.sb.pidfile(), (long_ago, long_ago))
        out = self.run_script("stop-server.sh", PORT, "--kill", env=self.sb.env(SLT_STOP_WAIT="1"), timeout=15)
        self.assertEqual(out.returncode, 6, out.stdout + out.stderr)
        self.assertIn("reused", out.stderr)
        self.assertTrue(self.alive(stranger.pid))

    def test_garbage_pid_file_signals_nothing(self):
        self.plant_state("-1")
        out = self.run_script("stop-server.sh", PORT, "--kill", env=self.sb.env(SLT_STOP_WAIT="1"))
        self.assertEqual(out.returncode, 6, out.stdout + out.stderr)

    def test_send_command_does_not_hang_on_a_reused_pid(self):
        stranger = self.spawn_stranger()
        self.plant_state(stranger.pid)
        out = self.run_script("send-command.sh", PORT, "slprobe", "status", timeout=10)
        self.assertEqual(out.returncode, 3, out.stdout + out.stderr)

    def test_start_replaces_a_stale_pid_file_instead_of_calling_it_running(self):
        stranger = self.spawn_stranger()
        self.plant_state(stranger.pid)
        os.remove(os.path.join(self.sb.state(), "console.fifo"))
        out = self.start("--accept-eula")
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertIn("stale pid file", out.stderr)
        self.assertNotIn("already running", out.stdout)
        self.assertTrue(self.alive(stranger.pid), "starting must not touch the stranger either")
        self.assertEqual(self.run_script("stop-server.sh", PORT).returncode, 0)

    def test_our_own_server_stops_gracefully_and_by_kill(self):
        out = self.start("--accept-eula")
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        sent = self.run_script("send-command.sh", PORT, "/dummy", "spawn", "Bot1")
        self.assertEqual(sent.returncode, 0, sent.stderr)
        self.assertIn("got: /dummy spawn Bot1", sent.stdout)
        pid = int(read_file(self.sb.pidfile()))
        stop = self.run_script("stop-server.sh", PORT)
        self.assertEqual(stop.returncode, 0, stop.stdout + stop.stderr)
        self.assertTrue(self.wait_gone(pid))
        self.assertFalse(os.path.exists(self.sb.pidfile()))

        # A server that ignores "exit": without --kill it stays (exit 5), with --kill the verified group dies.
        env = self.sb.env(STUB_IGNORE_EXIT="1", SLT_STOP_WAIT="1")
        out = self.start("--accept-eula", env=env)
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        pid = int(read_file(self.sb.pidfile()))
        soft = self.run_script("stop-server.sh", PORT, env=env)
        self.assertEqual(soft.returncode, 5, soft.stdout + soft.stderr)
        self.assertTrue(self.alive(pid))
        hard = self.run_script("stop-server.sh", PORT, "--kill", env=env)
        self.assertEqual(hard.returncode, 0, hard.stdout + hard.stderr)
        self.assertIn("process group", hard.stdout)
        self.assertTrue(self.wait_gone(pid))


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class PlatformTest(ScriptTestCase):
    """#19: Git Bash and friends are refused up front, with nothing created."""

    def fake_uname_env(self, system):
        bindir = os.path.join(self._tmp.name, "fakebin")
        os.makedirs(bindir, exist_ok=True)
        uname = os.path.join(bindir, "uname")
        with open(uname, "w") as f:
            f.write(f'#!/bin/sh\necho "{system}"\n')
        os.chmod(uname, 0o755)
        return self.sb.env(PATH=bindir + os.pathsep + os.environ["PATH"])

    def test_every_script_refuses_non_linux(self):
        calls = [("start-server.sh", [PORT, "--accept-eula"]), ("stop-server.sh", [PORT]),
                 ("send-command.sh", [PORT, "status"]), ("list-ports.sh", []), ("check-private.sh", [PORT]),
                 ("find-install.sh", []), ("list-created.sh", [PORT, "--since", "1"])]
        for system in ("MINGW64_NT-10.0-19045", "Darwin"):
            env = self.fake_uname_env(system)
            for name, args in calls:
                with self.subTest(system=system, script=name):
                    out = self.run_script(name, *args, env=env, timeout=10)
                    self.assertEqual(out.returncode, 69, out.stdout + out.stderr)
                    self.assertIn("Linux-only", out.stderr)
        self.assertFalse(os.path.exists(self.sb.run_dir), "a refusal must not create state")


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class InstallLookupTest(ScriptTestCase):
    """#23: default Steam locations plus libraryfolders.vdf, nothing recursive."""

    def test_finds_install_in_a_library_named_by_the_vdf(self):
        library = os.path.join(self._tmp.name, "games")
        self.make_library(library, "SCP Secret Laboratory Dedicated Server")
        out = self.run_script("find-install.sh", env=self.lookup_env())
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertIn(os.path.join(library, "steamapps", "common", "SCP Secret Laboratory Dedicated Server"), out.stdout)

    def test_does_not_search_anywhere_else(self):
        # A perfectly good install in a spot nobody documented: a recursive search would find it.
        self.sb.make_install(os.path.join(self.sb.home, "deep", "er", "SCP Secret Laboratory Dedicated Server"))
        out = self.run_script("find-install.sh", env=self.lookup_env())
        self.assertEqual(out.returncode, 1, out.stdout + out.stderr)
        self.assertIn("NOT FOUND", out.stderr)
        start = self.run_script("start-server.sh", PORT, "--accept-eula", env=self.lookup_env())
        self.assertEqual(start.returncode, 3, start.stdout + start.stderr)
        self.assertFalse(os.path.exists(self.sb.args_file))

    def test_two_installs_found_means_ask(self):
        library = os.path.join(self._tmp.name, "games")
        self.make_library(library, "SCP Secret Laboratory Dedicated Server")
        self.sb.make_install(os.path.join(self.sb.home, ".local", "share", "Steam", "steamapps", "common",
                                          "SCP Secret Laboratory Dedicated Server"))
        out = self.run_script("start-server.sh", PORT, "--accept-eula", env=self.lookup_env())
        self.assertEqual(out.returncode, 6, out.stdout + out.stderr)
        self.assertIn("ASK", out.stderr)
        self.assertFalse(os.path.exists(self.sb.args_file))


# Tools the scripts call. A PATH made only of these has no `ss`, which is the point.
TOOLS = ("bash", "env", "uname", "dirname", "basename", "sed", "awk", "sort", "xargs", "cat", "tr", "grep",
         "head", "tail", "stat", "date", "mkfifo", "setsid", "nohup", "ps", "pgrep", "getconf", "readlink",
         "find", "wc", "sleep", "rm", "mkdir", "sh")


class NetTestCase(ScriptTestCase):
    def no_ss_env(self, **extra):
        """An environment whose PATH holds the scripts' tools but not `ss`."""
        bindir = os.path.join(self._tmp.name, "nossbin")
        os.makedirs(bindir, exist_ok=True)
        for tool in TOOLS:
            real = shutil.which(tool)
            link = os.path.join(bindir, tool)
            if real and not os.path.lexists(link):
                os.symlink(real, link)
        self.assertIsNone(shutil.which("ss", path=bindir))
        return self.sb.env(PATH=bindir, **extra)

    def listen(self, kind):
        """Opens a listening socket (udp, tcp, udp6, tcp6) on loopback and returns its port."""
        v6 = kind.endswith("6")
        sock = socket.socket(socket.AF_INET6 if v6 else socket.AF_INET,
                             socket.SOCK_STREAM if kind.startswith("tcp") else socket.SOCK_DGRAM)
        self.addCleanup(sock.close)
        try:
            sock.bind(("::1" if v6 else "127.0.0.1", 0))
        except OSError as e:
            self.skipTest(f"cannot bind {kind} on loopback: {e}")
        if kind.startswith("tcp"):
            sock.listen()
        return sock.getsockname()[1]

    def free_port(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
        sock.close()
        return port

    def plant_ours(self, port):
        """State that says "this tooling created that port earlier", as a previous run leaves it."""
        os.makedirs(self.sb.state(str(port)), exist_ok=True)
        with open(os.path.join(self.sb.state(str(port)), "created-epoch"), "w") as f:
            f.write("1\n")

    def start_on(self, port, env):
        return self.run_script("start-server.sh", str(port), "--wait", "15", "--accept-eula", env=env)


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class PortFreeBeforeStartTest(NetTestCase):
    """S1: a created-epoch file said "ours" and skipped every port check. The port is now checked right
    before the launch, whatever the state says."""

    def test_listening_port_is_refused_even_when_the_state_says_ours(self):
        for label, env in (("ss", self.sb.env()), ("proc", self.no_ss_env())):
            for kind in ("udp", "tcp"):
                with self.subTest(method=label, kind=kind):
                    port = self.listen(kind)
                    self.plant_ours(port)
                    out = self.start_on(port, env)
                    self.assertEqual(out.returncode, 2, out.stdout + out.stderr)
                    self.assertIn("listening", out.stderr)
                    self.assertFalse(os.path.exists(self.sb.args_file), "LocalAdmin must not have been started")
                    self.assertFalse(os.path.exists(self.sb.pidfile(str(port))))

    def test_allow_existing_port_does_not_override_a_live_listener(self):
        # The owner may allow THEIR idle port; nobody allows a second server on a port that is in use now.
        port = self.listen("udp")
        out = self.run_script("start-server.sh", str(port), "--wait", "15", "--accept-eula",
                              "--allow-existing-port", env=self.sb.env())
        self.assertEqual(out.returncode, 2, out.stdout + out.stderr)
        self.assertFalse(os.path.exists(self.sb.args_file))

    def test_our_own_idle_port_still_starts(self):
        port = self.free_port()
        self.plant_ours(port)
        out = self.start_on(port, self.sb.env())
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertEqual(self.run_script("stop-server.sh", str(port)).returncode, 0)


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class ListeningFallbackTest(NetTestCase):
    """S3: without `ss` the listening check was skipped and --has called a busy port free."""

    def has(self, port, env):
        return self.run_script("list-ports.sh", "--has", str(port), env=env, timeout=10)

    def test_proc_net_fallback_sees_listeners(self):
        env = self.no_ss_env()
        for kind in ("udp", "tcp", "udp6", "tcp6"):
            with self.subTest(kind=kind):
                port = self.listen(kind)
                out = self.has(port, env)
                self.assertEqual(out.returncode, 0, out.stdout + out.stderr)

    def test_a_free_port_is_free_without_ss(self):
        out = self.has(self.free_port(), self.no_ss_env())
        self.assertEqual(out.returncode, 1, out.stdout + out.stderr)

    def test_a_connected_udp_socket_is_not_a_listener(self):
        # Only bound-and-unconnected UDP sockets count, like `ss -l`: a client socket talking to someone is
        # not a server on that port.
        server = self.listen("udp")
        client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.addCleanup(client.close)
        client.bind(("127.0.0.1", 0))
        client.connect(("127.0.0.1", server))
        out = self.has(client.getsockname()[1], self.no_ss_env())
        self.assertEqual(out.returncode, 1, out.stdout + out.stderr)

    def test_no_method_available_fails_closed(self):
        empty = os.path.join(self._tmp.name, "empty-proc-net")
        os.makedirs(empty)
        env = self.no_ss_env(SLT_PROC_NET=empty)
        free = self.free_port()
        for args in (["--has", str(free)], ["--listening", str(free)], ["--suggest"]):
            with self.subTest(args=args):
                out = self.run_script("list-ports.sh", *args, env=env, timeout=10)
                self.assertEqual(out.returncode, 3, out.stdout + out.stderr)
                self.assertIn("cannot tell", out.stderr)
        listing = self.run_script("list-ports.sh", env=env, timeout=10)
        self.assertEqual(listing.returncode, 0, listing.stderr)
        self.assertIn("UNKNOWN", listing.stdout)

    def test_traces_still_answer_when_no_method_is_available(self):
        os.makedirs(os.path.join(self.sb.cfg, "config", "47999"))
        empty = os.path.join(self._tmp.name, "empty-proc-net")
        os.makedirs(empty)
        out = self.has(47999, self.no_ss_env(SLT_PROC_NET=empty))
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)

    def test_start_refuses_when_nothing_can_tell_whether_the_port_is_free(self):
        empty = os.path.join(self._tmp.name, "empty-proc-net")
        os.makedirs(empty)
        env = self.no_ss_env(SLT_PROC_NET=empty)
        for ours in (False, True):
            with self.subTest(ours=ours):
                port = self.free_port()
                if ours:
                    self.plant_ours(port)
                out = self.start_on(port, env)
                self.assertEqual(out.returncode, 2, out.stdout + out.stderr)
                self.assertIn("cannot tell", out.stderr)
                self.assertFalse(os.path.exists(self.sb.args_file))


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class RecordedInstallTest(ScriptTestCase):
    """S2: list-created.sh took the first install it found, and start-server.sh never said which one it
    had used. The install is now recorded in the state and read back from it."""

    NAME = "SCP Secret Laboratory Dedicated Server"

    def two_installs(self):
        chosen = self.make_library(os.path.join(self._tmp.name, "games"), self.NAME)
        other = os.path.join(self.sb.home, ".local", "share", "Steam", "steamapps", "common", self.NAME)
        self.sb.make_install(other)
        return chosen, other

    def start_with(self, install, *extra):
        return self.run_script("start-server.sh", PORT, "--wait", "15", "--accept-eula", "--install", install,
                               *extra, env=self.lookup_env())

    def test_start_records_the_install_and_the_port(self):
        chosen, _ = self.two_installs()
        out = self.start_with(chosen)
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertEqual(os.path.realpath(read_file(os.path.join(self.sb.state(), "install")).strip()),
                         os.path.realpath(chosen))
        self.assertEqual(read_file(os.path.join(self.sb.state(), "port")).strip(), PORT)
        self.assertEqual(self.run_script("stop-server.sh", PORT, env=self.lookup_env()).returncode, 0)

    def test_list_created_scans_the_recorded_install_not_the_first_one_found(self):
        probe = os.path.join(self._tmp.name, "birth-probe")
        with open(probe, "w"):
            pass
        born = subprocess.run(["stat", "-c", "%W", probe], capture_output=True, text=True).stdout.strip()
        if born in ("", "0"):
            self.skipTest("this filesystem does not record birth time")
        chosen, other = self.two_installs()
        self.assertEqual(self.start_with(chosen).returncode, 0)
        # Files born after the start, one in each install: only the recorded one may be listed.
        for folder, name in ((chosen, "written-by-the-test"), (other, "written-by-somebody-else")):
            with open(os.path.join(folder, name), "w"):
                pass
        out = self.run_script("list-created.sh", PORT, env=self.lookup_env())
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertIn("written-by-the-test", out.stdout)
        self.assertNotIn("written-by-somebody-else", out.stdout)
        self.assertEqual(self.run_script("stop-server.sh", PORT, env=self.lookup_env()).returncode, 0)

    def test_without_a_record_and_with_several_installs_it_refuses_and_asks(self):
        self.two_installs()
        out = self.run_script("list-created.sh", PORT, "--since", "0", env=self.lookup_env())
        self.assertEqual(out.returncode, 6, out.stdout + out.stderr)
        self.assertIn("--install", out.stderr)
        self.assertNotIn("OTHER", out.stdout, "nothing may be scanned before the owner chose")

    def test_an_explicit_install_settles_it_when_there_is_no_record(self):
        chosen, _ = self.two_installs()
        out = self.run_script("list-created.sh", PORT, "--since", "0", "--install", chosen, env=self.lookup_env())
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)

    def test_restart_reuses_the_recorded_install_instead_of_asking_again(self):
        chosen, _ = self.two_installs()
        self.assertEqual(self.start_with(chosen).returncode, 0)
        self.assertEqual(self.run_script("stop-server.sh", PORT, env=self.lookup_env()).returncode, 0)
        again = self.run_script("start-server.sh", PORT, "--wait", "15", "--accept-eula", env=self.lookup_env())
        self.assertEqual(again.returncode, 0, again.stdout + again.stderr)
        self.assertEqual(os.path.realpath(read_file(os.path.join(self.sb.state(), "install")).strip()),
                         os.path.realpath(chosen))
        self.assertEqual(self.run_script("stop-server.sh", PORT, env=self.lookup_env()).returncode, 0)

    def test_another_install_than_the_recorded_one_is_refused(self):
        # Its files would land where list-created.sh no longer looks.
        chosen, other = self.two_installs()
        self.assertEqual(self.start_with(chosen).returncode, 0)
        self.assertEqual(self.run_script("stop-server.sh", PORT, env=self.lookup_env()).returncode, 0)
        os.remove(self.sb.args_file)
        out = self.start_with(other)
        self.assertEqual(out.returncode, 6, out.stdout + out.stderr)
        self.assertIn("recorded", out.stderr)
        self.assertFalse(os.path.exists(self.sb.args_file), "LocalAdmin must not have been started")


@unittest.skipUnless(sys.platform.startswith("linux"), "the scripts are Linux-only")
class KillIsVerifiedTest(ScriptTestCase):
    """S4: --kill sent SIGTERM, did not wait, did not look, exited 0 and left the pid file behind."""

    def env_stubborn(self, **extra):
        return self.sb.env(STUB_IGNORE_EXIT="1", SLT_STOP_WAIT="1", SLT_KILL_WAIT="1", **extra)

    def test_a_server_that_ignores_sigterm_is_reported_not_claimed_dead(self):
        env = self.env_stubborn(STUB_IGNORE_TERM="1")
        self.assertEqual(self.start("--accept-eula", env=env).returncode, 0)
        pid = int(read_file(self.sb.pidfile()))
        out = self.run_script("stop-server.sh", PORT, "--kill", env=env)
        self.assertEqual(out.returncode, 5, out.stdout + out.stderr)
        self.assertIn("--sigkill", out.stderr)
        self.assertTrue(self.alive(pid), "the stub ignores SIGTERM, so it is still up")
        self.assertTrue(os.path.isfile(self.sb.pidfile()), "the pid file is the only handle on a live server")

    def test_sigkill_is_an_explicit_second_step(self):
        env = self.env_stubborn(STUB_IGNORE_TERM="1")
        self.assertEqual(self.start("--accept-eula", env=env).returncode, 0)
        pid = int(read_file(self.sb.pidfile()))
        out = self.run_script("stop-server.sh", PORT, "--kill", "--sigkill", env=env)
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertIn("SIGKILL", out.stdout)
        self.assertTrue(self.wait_gone(pid))
        self.assertFalse(os.path.exists(self.sb.pidfile()), "removed once the process is gone")
        self.assertFalse(os.path.exists(os.path.join(self.sb.state(), "console.fifo")))

    def test_sigkill_without_kill_is_a_usage_error(self):
        out = self.run_script("stop-server.sh", PORT, "--sigkill", timeout=10)
        self.assertEqual(out.returncode, 64, out.stdout + out.stderr)
        self.assertIn("--kill", out.stderr)

    def test_sigkill_still_refuses_a_pid_that_is_not_ours(self):
        stranger = subprocess.Popen(["sleep", "60"], start_new_session=True,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self._spawned.append(stranger)
        os.makedirs(self.sb.state())
        with open(self.sb.pidfile(), "w") as f:
            f.write(f"{stranger.pid}\n")
        out = self.run_script("stop-server.sh", PORT, "--kill", "--sigkill", env=self.env_stubborn(), timeout=15)
        self.assertEqual(out.returncode, 6, out.stdout + out.stderr)
        self.assertTrue(self.alive(stranger.pid))

    def test_a_clean_sigterm_removes_the_pid_file(self):
        env = self.env_stubborn()
        self.assertEqual(self.start("--accept-eula", env=env).returncode, 0)
        pid = int(read_file(self.sb.pidfile()))
        out = self.run_script("stop-server.sh", PORT, "--kill", env=env)
        self.assertEqual(out.returncode, 0, out.stdout + out.stderr)
        self.assertTrue(self.wait_gone(pid))
        self.assertFalse(os.path.exists(self.sb.pidfile()))

    def test_a_surviving_game_child_keeps_the_pid_file_and_the_exit_code_non_zero(self):
        # LocalAdmin dies on SIGTERM, its game child (same group) ignores it: the server is NOT gone.
        env = self.env_stubborn(STUB_CHILD_IGNORE_TERM="1")
        self.assertEqual(self.start("--accept-eula", env=env).returncode, 0)
        pid = int(read_file(self.sb.pidfile()))
        out = self.run_script("stop-server.sh", PORT, "--kill", env=env)
        self.assertEqual(out.returncode, 5, out.stdout + out.stderr)
        self.assertIn("still", out.stderr)
        self.assertTrue(os.path.isfile(self.sb.pidfile()))
        # A later plain stop must not call that "not running" either.
        again = self.run_script("stop-server.sh", PORT, env=env)
        self.assertEqual(again.returncode, 5, again.stdout + again.stderr)
        self.assertTrue(os.path.isfile(self.sb.pidfile()))
        os.killpg(pid, signal.SIGKILL)  # the test's own cleanup of what the script rightly left alone


if __name__ == "__main__":
    unittest.main()
