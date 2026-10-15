#!/usr/bin/env python3
"""Builds, tests and lints everything in the repository, then prints one table.

What runs, in order:
  1. build   every *.csproj under examples/ and tools/ (recursive, bin/obj ignored); SlKit first,
             because most projects reference it and a broken SlKit makes every later row noise;
  2. dotnet  every plain-.NET test runner (a net10.0 Exe project with a Program.cs), via `dotnet run`;
  3. python  every test suite (a directory holding test_*.py) via `unittest discover`;
  4. lint    scpsl-lint over examples/ and over each plugin-shaped project under tools/ (SlKit, sl-probe,
             hot-bench, tickrate-fix). Each target is its own lint run because the linter treats one invocation as one
             project (SL015 looks for a shutdown hook across all files it was given), so a hook in one
             target must not hide a missing one in another. tools/scpsl-lint is NOT a target: its test
             fixtures are deliberately bad code.

Exit code 0 only if every row is OK. Usage:
  python3 examples/build-all.py [-p:SL_REFERENCES=/path/to/SCPSL_Data/Managed]
Extra arguments are passed to `dotnet build` only.
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
EXTRA = sys.argv[1:]
PRUNE = {"bin", "obj", "node_modules", ".git", "__pycache__"}
SCAN_ROOTS = [os.path.join(ROOT, "examples"), os.path.join(ROOT, "tools")]
PY_SCAN_ROOTS = SCAN_ROOTS + [os.path.join(ROOT, "tests")]
TEST_TIMEOUT = 600
LINT_TARGETS = ["examples", "tools/SlKit", "tools/sl-probe", "tools/hot-bench", "tools/tickrate-fix"]
# The linter prints findings on stdout and its summary line on stderr, so look at both and match the
# summary wherever it sits.
LINT_SUMMARY = re.compile(r"^scpsl-lint: \d+ file\(s\), .*$", re.M)

rows = []  # (kind, ok, target, detail)


def rel(p):
    return os.path.relpath(p, ROOT)


def walk(roots):
    for root in roots:
        for dp, dns, fns in os.walk(root):
            dns[:] = sorted(d for d in dns if d not in PRUNE)
            yield dp, fns


def find_csproj():
    return sorted(os.path.join(dp, f) for dp, fns in walk(SCAN_ROOTS) for f in fns if f.endswith(".csproj"))


def is_test_runner(proj):
    """net10.0 Exe project with a Program.cs next to it: runs with plain `dotnet run`, no game needed."""
    try:
        text = open(proj, encoding="utf-8", errors="ignore").read()
    except OSError:
        return False
    return ("<TargetFramework>net10.0</TargetFramework>" in text
            and "<OutputType>Exe</OutputType>" in text
            and os.path.isfile(os.path.join(os.path.dirname(proj), "Program.cs")))


def tail(text, n=3):
    lines = [l.rstrip() for l in text.splitlines() if l.strip()]
    return " | ".join(l.strip() for l in lines[-n:])


def build(proj):
    r = subprocess.run(["dotnet", "build", proj, "-c", "Release", "-nologo", "-v", "q", *EXTRA],
                       capture_output=True, text=True)
    errors = sorted({l.strip() for l in r.stdout.splitlines() if ": error " in l})
    detail = "" if r.returncode == 0 else (errors[0] if errors else tail(r.stdout + r.stderr))
    return r.returncode == 0, detail, errors


def run_dotnet_tests(proj):
    try:
        r = subprocess.run(["dotnet", "run", "--project", proj, "-c", "Release", "--no-build"],
                           capture_output=True, text=True, timeout=TEST_TIMEOUT)
    except subprocess.TimeoutExpired:
        return False, f"timed out after {TEST_TIMEOUT}s"
    return r.returncode == 0, tail(r.stdout + r.stderr, 1) or f"exit {r.returncode}"


def run_python_suite(directory):
    try:
        r = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s", directory],
                           capture_output=True, text=True, timeout=TEST_TIMEOUT)
    except subprocess.TimeoutExpired:
        return False, f"timed out after {TEST_TIMEOUT}s"
    # unittest writes its summary ("Ran N tests ... OK/FAILED") to stderr.
    # Some environments colourise unittest output; escape codes would wreck the table.
    plain = re.sub(r"\x1b\[[0-9;]*m", "", r.stderr + r.stdout)
    lines = [l for l in plain.splitlines() if l.strip()]
    ran = next((l for l in lines if l.startswith("Ran ")), "")
    verdict = lines[-1] if lines else f"exit {r.returncode}"
    return r.returncode == 0, f"{ran}; {verdict}".strip("; ")


def table():
    headers = ("KIND", "STATUS", "TARGET", "DETAIL")
    data = [(k, "OK" if ok else "FAIL", t, d) for k, ok, t, d in rows]
    # A list, not max(a, *generator): with no rows the star-expansion leaves max() a single int and it raises.
    w = [max([len(headers[i])] + [len(r[i]) for r in data]) for i in range(3)]
    fmt = f"{{:<{w[0]}}}  {{:<{w[1]}}}  {{:<{w[2]}}}  {{}}"
    print(fmt.format(*headers))
    print(fmt.format(*("-" * x for x in w), "-" * 6))
    for r in data:
        print(fmt.format(*r).rstrip())


def main():
    projects = find_csproj()
    slkit = os.path.join(ROOT, "tools", "SlKit", "src", "SlKit.csproj")
    ordered = [slkit] + [p for p in projects if p != slkit] if slkit in projects else projects

    built = {}
    for p in ordered:
        ok, detail, errors = build(p)
        built[p] = ok
        rows.append(("build", ok, rel(p), detail))
        for e in errors[1:20]:
            print("    " + e, file=sys.stderr)
        if p == slkit and not ok:
            # Everything downstream would only repeat SlKit's errors.
            table()
            print("SlKit failed to build; stopping.")
            return 1

    for p in ordered:
        if is_test_runner(p):
            if built.get(p):
                ok, detail = run_dotnet_tests(p)
            else:
                ok, detail = False, "not run: build failed"
            rows.append(("dotnet", ok, rel(p), detail))

    suites = sorted({dp for dp, fns in walk(PY_SCAN_ROOTS) if any(f.startswith("test_") and f.endswith(".py") for f in fns)})
    for d in suites:
        ok, detail = run_python_suite(d)
        rows.append(("python", ok, rel(d), detail))

    lint_failures = []
    for target in LINT_TARGETS:
        lint = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "scpsl-lint", "scpsl_lint.py"),
                               os.path.join(ROOT, target)], capture_output=True, text=True)
        out = lint.stdout + lint.stderr
        m = LINT_SUMMARY.search(out)
        detail = m.group(0) if m else (tail(out, 1) or f"no summary line (exit {lint.returncode})")
        rows.append(("lint", lint.returncode == 0, target + "/", detail))
        if lint.returncode != 0:
            lint_failures.append(out)

    table()
    failed = [r for r in rows if not r[1]]
    for out in lint_failures:
        print(out)
    n_build = sum(1 for r in rows if r[0] == "build")
    print(f"{n_build} project(s) built, {len(rows)} check(s): {'ALL OK' if not failed else f'{len(failed)} FAILURE(S)'}")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
