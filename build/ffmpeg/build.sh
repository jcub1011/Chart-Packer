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
work="${FFMPEG_WORK:-$root/build/ffmpeg/.work}"
src="$work/ffmpeg-$tag"

if [[ ! -d "$src" ]]; then
  mkdir -p "$work"
  git clone --depth 1 --branch "$tag" https://git.ffmpeg.org/ffmpeg.git "$src"
fi

cross=()
case "$rid" in
  win-x64)     cross=(--enable-cross-compile --target-os=mingw32 --arch=x86_64 --cross-prefix=x86_64-w64-mingw32-) ;;
  win-arm64)   cross=(--enable-cross-compile --target-os=mingw32 --arch=aarch64 --cross-prefix=aarch64-w64-mingw32-) ;;
  # Each library finds the others beside itself, wherever the app is installed.
  linux-x64|linux-arm64) cross=(--extra-ldflags='-Wl,-rpath,$ORIGIN') ;;
  osx-x64)     cross=(--arch=x86_64 --install-name-dir='@loader_path' --extra-cflags="-arch x86_64 -mmacosx-version-min=11.0" --extra-ldflags="-arch x86_64 -mmacosx-version-min=11.0") ;;
  osx-arm64)   cross=(--arch=arm64 --install-name-dir='@loader_path' --extra-cflags="-arch arm64 -mmacosx-version-min=11.0" --extra-ldflags="-arch arm64 -mmacosx-version-min=11.0") ;;
  *) echo "Unknown rid '$rid'." >&2; exit 1 ;;
esac

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
