#!/usr/bin/env bash
# Builds the minimal LGPL FFmpeg that ChartPacker.Ffmpeg binds (for video and audio), and copies its
# shared libraries into ChartPacker.Ffmpeg/runtimes/<rid>/native/, from where they ship beside the executable.
#
# Usage: build/ffmpeg/build.sh <rid>
#   rid: win-x64, win-arm64 (cross-compiled with mingw-w64 / llvm-mingw from Linux or WSL),
#        linux-x64, linux-arm64 (on that platform), osx-x64, osx-arm64 (on macOS, with Xcode).
#
# Needs: git, make, a C compiler, nasm (x86-64 only), pkg-config.
#
# Only what the app plays is built: the demuxers of the registered video extensions (sng-spec §8)
# and the decoders their videos use, and the decoders of the registered audio formats (Vorbis, Opus,
# MPEG audio), whose packets the app reads itself, so they need no demuxers. The Opus decoder needs
# swresample. Nothing GPL or non-free is enabled, so the result is LGPL 2.1+.
set -euo pipefail

rid="${1:?usage: build.sh <rid>}"
tag="n9.0"

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/ChartPacker.Ffmpeg/runtimes/$rid/native"

# FFmpeg's configure refuses to build out of tree from a path with whitespace in it, so a repo
# under such a path (C:\Users\First Last\...) builds in /tmp instead; only the result is copied back.
if [[ -n "${FFMPEG_WORK:-}" ]]; then
  work="$FFMPEG_WORK"
elif [[ "$root" =~ [[:space:]] ]]; then
  work="${TMPDIR:-/tmp}/chartpacker-ffmpeg"
else
  work="$root/build/ffmpeg/.work"
fi
if [[ "$work" =~ [[:space:]] ]]; then
  echo "FFmpeg can't be built in '$work': its path has whitespace. Set FFMPEG_WORK to a path without any." >&2
  exit 1
fi
src="$work/ffmpeg-$tag"

# --disable-autodetect below turns threading off too, so each platform's is turned back on.
cross=()
cc=cc
case "$rid" in
  win-x64)     cross=(--enable-cross-compile --target-os=mingw32 --arch=x86_64 --cross-prefix=x86_64-w64-mingw32- --enable-w32threads); cc=x86_64-w64-mingw32-gcc ;;
  win-arm64)   cross=(--enable-cross-compile --target-os=mingw32 --arch=aarch64 --cross-prefix=aarch64-w64-mingw32- --enable-w32threads); cc=aarch64-w64-mingw32-gcc ;;
  # Each library finds the others beside itself, wherever the app is installed.
  linux-x64|linux-arm64) cross=(--enable-pthreads --extra-ldflags='-Wl,-rpath,$ORIGIN') ;;
  osx-x64)     cross=(--enable-pthreads --arch=x86_64 --install-name-dir='@loader_path' --extra-cflags="-arch x86_64 -mmacosx-version-min=11.0" --extra-ldflags="-arch x86_64 -mmacosx-version-min=11.0"); cc=clang ;;
  osx-arm64)   cross=(--enable-pthreads --arch=arm64 --install-name-dir='@loader_path' --extra-cflags="-arch arm64 -mmacosx-version-min=11.0" --extra-ldflags="-arch arm64 -mmacosx-version-min=11.0"); cc=clang ;;
  *) echo "Unknown rid '$rid'." >&2; exit 1 ;;
esac

# Everything the build needs, checked before a long clone and configure.
needs=(git make pkg-config "$cc")
[[ "$rid" == *-x64 ]] && needs+=(nasm)
missing=()
for tool in "${needs[@]}"; do
  command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
done
if (( ${#missing[@]} > 0 )); then
  echo "Can't build FFmpeg for $rid: ${missing[*]} not found." >&2
  case "$rid" in
    win-*) echo "Windows builds are cross-compiled on Linux or WSL (not Git Bash), with mingw-w64 or llvm-mingw; see build/ffmpeg/README.md." >&2 ;;
  esac
  exit 1
fi

if [[ ! -d "$src" ]]; then
  mkdir -p "$work"
  git clone --depth 1 --branch "$tag" https://git.ffmpeg.org/ffmpeg.git "$src"
fi

build="$work/build-$rid"
prefix="$work/install-$rid"
rm -rf "$build" "$prefix"
mkdir -p "$build"
cd "$build"

"$src/configure" \
  --prefix="$prefix" \
  "${cross[@]}" \
  --enable-shared --disable-static \
  --disable-programs --disable-doc --disable-network --disable-autodetect --disable-debug \
  --disable-everything \
  --disable-avdevice --disable-avfilter \
  --enable-avformat --enable-avcodec --enable-swscale --enable-swresample \
  --enable-demuxer=mov,avi,matroska,ogg,mpegps,mpegvideo,m4v,h264,hevc,ivf \
  --enable-decoder=h264,hevc,vp8,vp9,theora,mpeg1video,mpeg2video,mpeg4,msmpeg4v1,msmpeg4v2,msmpeg4v3,mjpeg,vorbis,opus,mp1float,mp2float,mp3float \
  --enable-parser=h264,hevc,vp8,vp9,mpegvideo,mpeg4video,mjpeg \
  --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb,vp9_superframe_split

make -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
make install

mkdir -p "$out"
rm -f "$out"/*
case "$rid" in
  win-*) cp "$prefix"/bin/av*-*.dll "$prefix"/bin/sw*-*.dll "$out/" ;;
  osx-*) for f in "$prefix"/lib/lib*.[0-9]*.dylib; do [[ "$f" =~ \.[0-9]+\.dylib$ ]] && cp -L "$f" "$out/"; done ;;
  *)     for f in "$prefix"/lib/lib*.so.[0-9]*; do [[ "$f" =~ \.so\.[0-9]+$ ]] && cp -L "$f" "$out/"; done ;;
esac

echo "FFmpeg $tag for $rid is in $out:"
ls -l "$out"
