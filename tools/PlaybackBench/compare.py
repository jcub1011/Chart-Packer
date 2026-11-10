#!/usr/bin/env python3
"""Compares two BenchmarkDotNet runs of tools/PlaybackBench (the JSON each wrote) benchmark by benchmark.

    compare.py <before-results-dir> <after-results-dir>

Each benchmark is matched by its method and parameters. Time is judged against the noise of both runs: a change counts
as real only when the two means differ by more than three standard errors of each (and 2%), so a benchmark that
didn't change reads 'same'. Allocations are exact, not noisy, so any difference in bytes per call is real. An After-only
benchmark (New*) is matched to its Old* twin in the Before run.
"""
import glob
import json
import math
import os
import sys


def load(directory):
    found = {}
    for path in glob.glob(os.path.join(directory, "**", "*-report-full.json"), recursive=True):
        with open(path, encoding="utf-8") as f:
            report = json.load(f)
        for b in report["Benchmarks"]:
            stats = b["Statistics"]
            if not stats:
                continue
            memory = b.get("Memory") or {}
            found[(b["Type"], b["Method"], b.get("Parameters") or "")] = {
                "mean": stats["Mean"],
                "stderr": stats["StandardError"],
                "stddev": stats["StandardDeviation"],
                "n": stats["N"],
                "bytes": memory.get("BytesAllocatedPerOperation", 0),
                "gen0": memory.get("Gen0Collections", 0),
            }
    return found


def twin(method):
    return "Old" + method[3:] if method.startswith("New") else method


def fmt_time(ns):
    for unit, size in (("ms", 1e6), ("us", 1e3)):
        if ns >= size:
            return f"{ns / size:.2f} {unit}"
    return f"{ns:.1f} ns"


def main():
    before, after = load(sys.argv[1]), load(sys.argv[2])
    if not before or not after:
        sys.exit("no results found")
    print(f"{'benchmark':<44}{'before':>12}{'after':>12}{'time':>10}  {'verdict':<9}{'B/op before':>13}{'B/op after':>12}")
    for key in sorted(after):
        typ, method, parameters = key
        old = before.get((typ, method, parameters)) or before.get((typ, twin(method), parameters))
        if old is None:
            continue
        new = after[key]
        ratio = new["mean"] / old["mean"] if old["mean"] else math.nan
        noise = 3 * math.hypot(old["stderr"], new["stderr"])
        if abs(new["mean"] - old["mean"]) <= max(noise, 0.02 * old["mean"]):
            verdict = "same"
        else:
            verdict = "FASTER" if ratio < 1 else "SLOWER"
        name = f"{typ.split('.')[-1].replace('Benchmarks', '')}.{method}" + (f" [{parameters}]" if parameters else "")
        print(f"{name:<44}{fmt_time(old['mean']):>12}{fmt_time(new['mean']):>12}{ratio:>9.2f}x  {verdict:<9}{old['bytes']:>13}{new['bytes']:>12}")


if __name__ == "__main__":
    main()
