#!/usr/bin/env python3
"""Summarise (and optionally compare) sl-probe CSV logs.

The probe writes one CSV line per second with that second's frame percentiles. This script
combines the lines of a whole round into numbers you can compare between two runs.

What is exact and what is not:
  * "% frames > 50 ms" and "> 100 ms" are exact: each second's percentage is weighted by the
    number of frames in that second.
  * The worst single frame is exact (the maximum of the per-second maxima).
  * p95 / p99 of ALL frames of a round cannot be rebuilt from per-second percentiles. Instead the
    script reports how the per-second p95 / p99 are distributed over the seconds of the round:
    the median second, the 95th-percentile second and the worst second. That answers "how bad is a
    bad second" and is comparable between runs. For one exact p99 over a window use
    `slprobe report` with a large window_frames.

Usage:
  slprobe_csv.py run.csv
  slprobe_csv.py baseline.csv candidate.csv --from-mark RoundStarted --to-mark RoundEnded --trim-start 30

Exit code 0 on success, 2 when a file has no usable rows.
"""
import argparse
import csv
import math
import sys

IDLE_TARGET_MAX = 5  # idle mode sets Application.targetFrameRate to idle_mode_tickrate (default 1)


def load(path, from_mark, to_mark, trim_start, trim_end):
    rows = []
    with open(path, newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            try:
                rows.append({
                    "utc": r["utc"],
                    "frames": int(r["frames"]),
                    "fps": float(r["fps"]),
                    "p50": float(r["period_p50_ms"]),
                    "p95": float(r["period_p95_ms"]),
                    "p99": float(r["period_p99_ms"]),
                    "max": float(r["period_max_ms"]),
                    "b95": float(r["busy_p95_ms"]),
                    "b99": float(r["busy_p99_ms"]),
                    "over50": float(r["pct_over_50ms"]),
                    "over100": float(r["pct_over_100ms"]),
                    "target": int(r["target_frame_rate"]),
                    "marks": [m for m in r["marks"].split("|") if m],
                })
            except (KeyError, ValueError):
                continue  # truncated last line or foreign file: skip the row, do not guess

    if from_mark:
        start = next((i for i, r in enumerate(rows) if from_mark in r["marks"]), None)
        if start is None:
            raise SystemExit(f"{path}: marker '{from_mark}' not found")
        rows = rows[start:]
    if to_mark:
        stop = next((i for i, r in enumerate(rows) if to_mark in r["marks"] and i > 0), None)
        if stop is None:
            raise SystemExit(f"{path}: marker '{to_mark}' not found after '{from_mark or 'start'}'")
        rows = rows[: stop + 1]
    if trim_start:
        rows = rows[int(trim_start):]
    if trim_end:
        rows = rows[: max(0, len(rows) - int(trim_end))]
    return rows


def nearest_rank(values, p):
    ordered = sorted(values)
    k = max(1, math.ceil(p / 100.0 * len(ordered)))
    return ordered[k - 1]


def summarise(rows):
    live = [r for r in rows if r["frames"] > 0 and not (0 < r["target"] <= IDLE_TARGET_MAX)]
    idle = len([r for r in rows if r["frames"] > 0]) - len(live)
    if not live:
        return None
    frames = sum(r["frames"] for r in live)
    return {
        "seconds": len(live),
        "idle seconds skipped": idle,
        "frames": frames,
        "mean fps": sum(r["fps"] for r in live) / len(live),
        "% frames > 50 ms": sum(r["frames"] * r["over50"] for r in live) / frames,
        "% frames > 100 ms": sum(r["frames"] * r["over100"] for r in live) / frames,
        "worst frame ms": max(r["max"] for r in live),
        "p95 of a typical second (median) ms": nearest_rank([r["p95"] for r in live], 50),
        "p99 of a typical second (median) ms": nearest_rank([r["p99"] for r in live], 50),
        "p99 of a bad second (95th pct) ms": nearest_rank([r["p99"] for r in live], 95),
        "p99 of the worst second ms": max(r["p99"] for r in live),
        "% of seconds with p99 > 50 ms": 100.0 * sum(1 for r in live if r["p99"] > 50) / len(live),
        "busy p95 of a typical second ms": nearest_rank([r["b95"] for r in live], 50),
        "busy p99 of a bad second ms": nearest_rank([r["b99"] for r in live], 95),
    }


def fmt(v):
    return f"{v:.2f}" if isinstance(v, float) else str(v)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("csv", nargs="+", help="one CSV to summarise, or two to compare (baseline first)")
    ap.add_argument("--from-mark", help="start at the first line carrying this marker, e.g. RoundStarted")
    ap.add_argument("--to-mark", help="stop at the first later line carrying this marker, e.g. RoundEnded")
    ap.add_argument("--trim-start", type=int, default=0, help="drop this many seconds after the start (round-start loading hitches)")
    ap.add_argument("--trim-end", type=int, default=0, help="drop this many seconds before the end")
    args = ap.parse_args()
    if len(args.csv) > 2:
        ap.error("give one file to summarise or two to compare")

    results = []
    for path in args.csv:
        s = summarise(load(path, args.from_mark, args.to_mark, args.trim_start, args.trim_end))
        if s is None:
            print(f"{path}: no usable (non-idle) rows", file=sys.stderr)
            return 2
        results.append(s)

    keys = list(results[0].keys())
    width = max(len(k) for k in keys) + 2
    if len(results) == 1:
        for k in keys:
            print(f"{k:<{width}}{fmt(results[0][k])}")
        return 0

    a, b = results
    print(f"{'':<{width}}{'baseline':>12}{'candidate':>12}{'delta':>12}")
    for k in keys:
        delta = b[k] - a[k]
        sign = "+" if delta > 0 else ""
        print(f"{k:<{width}}{fmt(a[k]):>12}{fmt(b[k]):>12}{(sign + fmt(delta)):>12}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
