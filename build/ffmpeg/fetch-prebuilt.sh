#!/usr/bin/env bash
# Fetches a prebuilt LGPL FFmpeg 9.0 (BtbN/FFmpeg-Builds) for local development, and copies the
# libraries ChartPacker.Video loads into ChartPacker.Video/runtimes/<rid>/native/.
#
# Usage: build/ffmpeg/fetch-prebuilt.sh <rid>     rid: win-x64, win-arm64, linux-x64, linux-arm64
#
# These builds enable far more than the app needs, so they're large. Releases use build.sh, which
# builds only the demuxers and decoders the app plays. There are no macOS builds; use build.sh.
set -euo pipefail

rid="${1:?usage: fetch-prebuilt.sh <rid>}"
branch="9.0"
case "$rid" in
  win-x64)     asset="ffmpeg-n${branch}-latest-win64-lgpl-shared-${branch}.zip" ;;
  win-arm64)   asset="ffmpeg-n${branch}-latest-winarm64-lgpl-shared-${branch}.zip" ;;
  linux-x64)   asset="ffmpeg-n${branch}-latest-linux64-lgpl-shared-${branch}.tar.xz" ;;
  linux-arm64) asset="ffmpeg-n${branch}-latest-linuxarm64-lgpl-shared-${branch}.tar.xz" ;;
  *) echo "No prebuilt FFmpeg for '$rid'; use build.sh." >&2; exit 1 ;;
esac

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/ChartPacker.Video/runtimes/$rid/native"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

curl -fL --retry 3 -o "$work/$asset" "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$asset"
case "$asset" in
  *.zip) unzip -q "$work/$asset" -d "$work" ;;
  *)     tar -xJf "$work/$asset" -C "$work" ;;
esac
dist="$(find "$work" -mindepth 1 -maxdepth 1 -type d | head -n 1)"

mkdir -p "$out"
rm -f "$out"/*
# avcodec links against swresample in these builds, so it's copied too.
for lib in avutil swresample swscale avcodec avformat; do
  case "$rid" in
    win-*) cp "$dist"/bin/"$lib"-*.dll "$out/" ;;
    *)     for f in "$dist"/lib/lib"$lib".so.[0-9]*; do [[ "$f" =~ \.so\.[0-9]+$ ]] && cp -L "$f" "$out/"; done ;;
  esac
done

echo "FFmpeg for $rid is in $out:"
ls -l "$out"
