#!/bin/bash
# Runs the benchmarks on a git revision (default HEAD) and on the working tree, one after the other, and compares them.
#
#   tools/PlaybackBench/run-ab.sh [revision] [-- BenchmarkDotNet arguments]
#
# The revision is exported to a temp directory and this tool copied into it (with -p:Before=true, which leaves out the
# benchmarks of what the revision lacks), so both sides run the same benchmarks over their own code. Leave the machine
# alone while it runs: anything else using the CPU is noise in the numbers.
set -e
REV="${1:-HEAD}"
shift || true
[ "$1" == "--" ] && shift
# BenchmarkDotNet asks which benchmarks to run when it isn't told, so say all of them unless the caller picked.
[ $# -eq 0 ] && set -- --filter '*'
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${TMPDIR:-/tmp}/playback-bench"
OUT="$WORK/results"
rm -rf "$WORK/before-src" "$OUT"
mkdir -p "$WORK/before-src" "$OUT/before" "$OUT/after"
git -C "$REPO" archive "$REV" | tar -x -C "$WORK/before-src"
rm -rf "$WORK/before-src/tools/PlaybackBench"
mkdir -p "$WORK/before-src/tools/PlaybackBench"
(cd "$REPO/tools/PlaybackBench" && cp ./*.cs ./*.csproj "$WORK/before-src/tools/PlaybackBench/")

run() { # side, project root, extra msbuild args
  (cd "$2" && dotnet run -c Release --project tools/PlaybackBench $3 -- --artifacts "$OUT/$1" "${@:4}")
}

run before "$WORK/before-src" "-p:Before=true" "$@"
run after "$REPO" "" "$@"
python3 "$REPO/tools/PlaybackBench/compare.py" "$OUT/before" "$OUT/after"
