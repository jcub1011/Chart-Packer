#!/usr/bin/env bash
# Builds the minimal LGPL FFmpeg that ChartPacker.Ffmpeg binds (for video and audio), and copies its
# shared libraries into ChartPacker.Ffmpeg/runtimes/<rid>/native/, from where they ship beside the executable.
#
# Usage: build/ffmpeg/build.sh [--yes] <rid>
#   --yes: install missing prerequisites without asking.
#   rid: win-x64, win-arm64 (natively in an MSYS2 UCRT64 / CLANGARM64 shell, or cross-compiled
#        with mingw-w64 / llvm-mingw from Linux or WSL),
#        linux-x64, linux-arm64 (on that platform), osx-x64, osx-arm64 (on macOS, with Xcode).
#
# Needs: git, make, a C compiler, nasm (x86-64 only), pkg-config. When any are missing, it offers to
# install them (with pacman on MSYS2, apt, dnf or pacman on Linux, xcode-select and brew on macOS). Run
# from Git Bash, it builds Windows RIDs in MSYS2 instead, offering to install MSYS2 with winget first.
#
# Only what the app plays is built: the demuxers of the registered video extensions (sng-spec §8)
# and the decoders their videos use, and the decoders of the registered audio formats (Vorbis, Opus,
# MPEG audio), whose packets the app reads itself, so they need no demuxers. The Opus decoder needs
# swresample. Nothing GPL or non-free is enabled, so the result is LGPL 2.1+.
set -euo pipefail

yes=
[[ "${1:-}" == --yes || "${1:-}" == -y ]] && { yes=1; shift; }
rid="${1:?usage: build.sh [--yes] <rid>}"
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

# Asks before running a command that installs something; --yes skips the question.
confirm() {
  echo "$1" >&2
  [[ -n "$yes" ]] && return 0
  [[ -t 0 ]] || { echo "Not asking, since there's no terminal; run it yourself, or pass --yes." >&2; return 1; }
  local answer
  read -r -p "Proceed? [y/N] " answer
  [[ "$answer" =~ ^[Yy] ]]
}

# On Windows, MSYS2 builds natively with its own mingw-w64 toolchain; elsewhere Windows is cross-compiled.
# Git Bash sets MSYSTEM too, but has no pacman to install the toolchain with.
msys="${MSYSTEM:-}"
if [[ -n "$msys" && "$rid" == win-* ]] && ! command -v pacman >/dev/null 2>&1; then
  msys2="${MSYS2_ROOT:-/c/msys64}"
  if [[ ! -x "$msys2/usr/bin/bash.exe" ]]; then
    command -v winget >/dev/null 2>&1 || { echo "Git Bash can't build FFmpeg, and winget isn't here to install MSYS2 with. Install MSYS2 from https://www.msys2.org, then run this again." >&2; exit 1; }
    confirm "Git Bash can't build FFmpeg; MSYS2 can. Install MSYS2 into C:\msys64 with: winget install --id MSYS2.MSYS2 -e" || exit 1
    winget install --id MSYS2.MSYS2 -e --accept-source-agreements --accept-package-agreements
    [[ -x "$msys2/usr/bin/bash.exe" ]] || { echo "MSYS2 isn't in $msys2 after installing it. Set MSYS2_ROOT to where it is." >&2; exit 1; }
  fi
  shell=UCRT64; [[ "$rid" == win-arm64 ]] && shell=CLANGARM64
  echo "Building in MSYS2's $shell shell." >&2
  exec "$msys2/usr/bin/env.exe" MSYSTEM="$shell" CHERE_INVOKING=1 "$msys2/usr/bin/bash.exe" -l "$root/build/ffmpeg/build.sh" ${yes:+--yes} "$rid"
fi
[[ "$msys" == MSYS ]] && { echo "Run this from an MSYS2 UCRT64, MINGW64 or CLANGARM64 shell, not the MSYS one: its binaries need msys-2.0.dll." >&2; exit 1; }

# --disable-autodetect below turns threading off too, so each platform's is turned back on.
cross=()
cc=cc
case "$rid" in
  win-x64|win-arm64)
    arch=x86_64; [[ "$rid" == win-arm64 ]] && arch=aarch64
    if [[ -n "$msys" ]]; then
      case "$msys:$rid" in
        UCRT64:win-x64|MINGW64:win-x64) cc=gcc ;;
        CLANG64:win-x64|CLANGARM64:win-arm64) cc=clang ;;
        *) echo "Can't build $rid from MSYS2's $msys shell: use UCRT64 for win-x64, or CLANGARM64 (on an ARM PC) for win-arm64." >&2; exit 1 ;;
      esac
      cross=(--target-os=mingw32 --arch="$arch" --cc="$cc" --enable-w32threads)
    else
      cc="$arch-w64-mingw32-gcc"
      cross=(--enable-cross-compile --target-os=mingw32 --arch="$arch" --cross-prefix="$arch-w64-mingw32-" --enable-w32threads)
    fi
    # So the DLLs don't need the toolchain's libgcc_s_seh-1.dll or libwinpthread-1.dll beside them.
    cross+=(--extra-ldflags=-static) ;;
  # Each library finds the others beside itself, wherever the app is installed.
  linux-x64|linux-arm64) cross=(--enable-pthreads --extra-ldflags='-Wl,-rpath,$ORIGIN') ;;
  osx-x64)     cross=(--enable-pthreads --arch=x86_64 --install-name-dir='@loader_path' --extra-cflags="-arch x86_64 -mmacosx-version-min=11.0" --extra-ldflags="-arch x86_64 -mmacosx-version-min=11.0"); cc=clang ;;
  osx-arm64)   cross=(--enable-pthreads --arch=arm64 --install-name-dir='@loader_path' --extra-cflags="-arch arm64 -mmacosx-version-min=11.0" --extra-ldflags="-arch arm64 -mmacosx-version-min=11.0"); cc=clang ;;
  *) echo "Unknown rid '$rid'." >&2; exit 1 ;;
esac

# Everything the build needs, checked before a long clone and configure.
needs=(git make pkg-config "$cc")
[[ "$rid" == *-x64 ]] && needs+=(nasm)
[[ -n "$msys" ]] && needs+=(cmp)
missing_tools() {
  missing=()
  for tool in "${needs[@]}"; do
    command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
  done
}

# The command that installs what's missing, for this platform's package manager; empty when there's none.
install_command() {
  local nasm=; [[ "$rid" == *-x64 ]] && nasm=1
  if [[ -n "$msys" ]]; then
    local p="${MINGW_PACKAGE_PREFIX:-mingw-w64-ucrt-x86_64}"
    echo "pacman -S --needed --noconfirm git make diffutils $p-$cc $p-pkgconf${nasm:+ $p-nasm}"
  elif [[ "$(uname -s)" == Darwin ]]; then
    if ! command -v clang >/dev/null 2>&1 || ! command -v make >/dev/null 2>&1 || ! command -v git >/dev/null 2>&1; then
      echo "xcode-select --install"
    elif command -v brew >/dev/null 2>&1; then
      echo "brew install pkg-config${nasm:+ nasm}"
    fi
  else
    local sudo=; (( EUID != 0 )) && sudo="sudo "
    local mingw=
    if [[ "$rid" == win-* ]]; then
      # Distributions package gcc for x86-64 Windows only; win-arm64 needs llvm-mingw.
      [[ "$rid" == win-arm64 ]] && return 0
      mingw=1
    fi
    if command -v apt-get >/dev/null 2>&1; then
      echo "${sudo}apt-get update && ${sudo}apt-get install -y git make pkg-config build-essential${nasm:+ nasm}${mingw:+ mingw-w64}"
    elif command -v dnf >/dev/null 2>&1; then
      echo "${sudo}dnf install -y git make pkgconf-pkg-config gcc${nasm:+ nasm}${mingw:+ mingw64-gcc}"
    elif command -v pacman >/dev/null 2>&1; then
      echo "${sudo}pacman -S --needed --noconfirm git make pkgconf gcc${nasm:+ nasm}${mingw:+ mingw-w64-gcc}"
    fi
  fi
}

missing_tools
if (( ${#missing[@]} > 0 )); then
  echo "Can't build FFmpeg for $rid yet: ${missing[*]} not found." >&2
  install="$(install_command)"
  if [[ -z "$install" ]]; then
    case "$rid" in
      win-arm64) echo "Install llvm-mingw (https://github.com/mstorsjo/llvm-mingw) and put its bin on PATH; see build/ffmpeg/README.md." >&2 ;;
      *) echo "Install them with your package manager; see build/ffmpeg/README.md." >&2 ;;
    esac
    exit 1
  fi
  confirm "They can be installed with: $install" || exit 1
  eval "$install"
  if [[ "$install" == "xcode-select --install" ]]; then
    echo "Run this again once the Command Line Tools have finished installing." >&2
    exit 1
  fi
  missing_tools
  if (( ${#missing[@]} > 0 )); then
    echo "Still missing after installing: ${missing[*]}. Run this again, or install them yourself; see build/ffmpeg/README.md." >&2
    exit 1
  fi
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
  win-*)
    # Fail rather than ship DLLs that need one of the toolchain's own, which users won't have.
    objdump=objdump; [[ -z "$msys" ]] && objdump="$arch-w64-mingw32-objdump"
    if command -v "$objdump" >/dev/null 2>&1; then
      for f in "$prefix"/bin/av*-*.dll "$prefix"/bin/sw*-*.dll; do
        extra="$("$objdump" -p "$f" | awk '/DLL Name:/ {print $3}' | grep -iv -E '^(kernel32|bcrypt|user32|advapi32|ole32|shell32|msvcrt|ucrtbase|api-ms-win-.*|av[a-z]+-[0-9]+|sw[a-z]+-[0-9]+)\.dll$' || true)"
        [[ -z "$extra" ]] || { echo "$(basename "$f") needs $(echo $extra), which won't be on users' machines." >&2; exit 1; }
      done
    fi
    cp "$prefix"/bin/av*-*.dll "$prefix"/bin/sw*-*.dll "$out/" ;;
  osx-*) for f in "$prefix"/lib/lib*.[0-9]*.dylib; do [[ "$f" =~ \.[0-9]+\.dylib$ ]] && cp -L "$f" "$out/"; done ;;
  *)     for f in "$prefix"/lib/lib*.so.[0-9]*; do [[ "$f" =~ \.so\.[0-9]+$ ]] && cp -L "$f" "$out/"; done ;;
esac

echo "FFmpeg $tag for $rid is in $out:"
ls -l "$out"
