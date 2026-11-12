#!/usr/bin/env bash
# Builds the minimal LGPL FFmpeg that ChartShared.Ffmpeg binds (for video and audio), and copies its
# shared libraries into ChartShared.Ffmpeg/runtimes/<rid>/native/, from where they ship beside the executable.
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
# MPEG audio), whose packets the app reads itself, so they need no demuxers. AAC is built too, for a
# background video's audio track, which is read through avformat (so the container demuxer supplies
# it). The Opus decoder needs swresample. The encoders are libvpx's VP8, which the Library
# Optimization Tool writes background videos with (WebM is the only video format that plays on every
# platform in both games), and libopus, which writes Opus audio for packing and for the optimizer
# (without it the app encodes Opus with the slower managed Concentus). libvpx and libopus are BSD-3 and
# are linked in statically, so no libvpx or libopus library ships beside FFmpeg's. Nothing GPL or
# non-free is enabled (libx264 is GPL, so there's no H.264 output), so the result is LGPL 2.1+.
set -euo pipefail

yes=
[[ "${1:-}" == --yes || "${1:-}" == -y ]] && { yes=1; shift; }
rid="${1:?usage: build.sh [--yes] <rid>}"
tag="n9.0.2"

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/ChartShared.Ffmpeg/runtimes/$rid/native"

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
# --pkg-config-flags=--static makes libvpx's static library, and what it needs, what pkg-config reports.
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
    echo "pacman -S --needed --noconfirm git make diffutils $p-$cc $p-pkgconf $p-libvpx $p-opus${nasm:+ $p-nasm}"
  elif [[ "$(uname -s)" == Darwin ]]; then
    if ! command -v clang >/dev/null 2>&1 || ! command -v make >/dev/null 2>&1 || ! command -v git >/dev/null 2>&1; then
      echo "xcode-select --install"
    elif command -v brew >/dev/null 2>&1; then
      echo "brew install pkg-config libvpx opus${nasm:+ nasm}"
    fi
  else
    local sudo=; (( EUID != 0 )) && sudo="sudo "
    local mingw=
    if [[ "$rid" == win-* ]]; then
      # Distributions package gcc for x86-64 Windows only; win-arm64 needs llvm-mingw. Neither has a libvpx
      # or libopus for Windows to install: see check_pkg.
      [[ "$rid" == win-arm64 ]] && return 0
      mingw=1
    fi
    # Distributions have no libvpx or libopus for mingw-w64 to install: see check_pkg.
    local libs=; [[ -z "$mingw" ]] && libs=1
    if command -v apt-get >/dev/null 2>&1; then
      echo "${sudo}apt-get update && ${sudo}apt-get install -y git make pkg-config build-essential${nasm:+ nasm}${mingw:+ mingw-w64}${libs:+ libvpx-dev libopus-dev}"
    elif command -v dnf >/dev/null 2>&1; then
      echo "${sudo}dnf install -y git make pkgconf-pkg-config gcc${nasm:+ nasm}${mingw:+ mingw64-gcc}${libs:+ libvpx-devel opus-devel}"
    elif command -v pacman >/dev/null 2>&1; then
      echo "${sudo}pacman -S --needed --noconfirm git make pkgconf gcc${nasm:+ nasm}${mingw:+ mingw-w64-gcc}${libs:+ libvpx opus}"
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

# FFmpeg needs the headers and library of libvpx (the VP8 encoder) and libopus (the Opus encoder), found through
# pkg-config. Windows has none to install when cross-compiling (and win-arm64 has no package at all): build the library
# for it, static, and point PKG_CONFIG_PATH at its lib/pkgconfig. Elsewhere, and in MSYS2, they're installed above.
#   check_pkg <pkg-config name> <library> <what FFmpeg needs it for> <project URL> <package names>
check_pkg() {
  local pc="$1" lib="$2" purpose="$3" url="$4" packages="$5"
  pkg-config --exists "$pc" && return 0
  echo "$lib isn't found by pkg-config ($pc.pc), and FFmpeg needs it for $purpose." >&2
  if [[ -z "$msys" && "$rid" == win-* ]]; then
    echo "Build $lib ($url) for $rid as a static library with the mingw toolchain, and set PKG_CONFIG_PATH to its lib/pkgconfig; see build/ffmpeg/README.md." >&2
    return 1
  fi
  local install
  install="$(install_command)"
  if [[ -n "$install" && "$install" != "xcode-select --install" ]] && confirm "It can be installed with: $install"; then
    eval "$install"
    pkg-config --exists "$pc" && return 0
  fi
  echo "Install $lib with your package manager ($packages), and run this again." >&2
  return 1
}
check_pkg vpx libvpx "the VP8 encoder" https://chromium.googlesource.com/webm/libvpx "libvpx-dev, libvpx-devel, libvpx, or brew's libvpx" || exit 1
check_pkg opus libopus "the Opus encoder" https://opus-codec.org/downloads/ "libopus-dev, opus-devel, opus, or brew's opus" || exit 1

# FFmpeg's libraries must not need libvpx or libopus beside them when they run, so both have to be linked in. In MSYS2's
# -static link that's what happens; elsewhere a .so or .dylib next to the .a would win the link, so pkg-config is
# given a .pc of our own that names the .a. (Debian, Fedora and Homebrew's static libraries are position-independent.)
if [[ -z "$msys" && "$rid" != win-* ]]; then
  pc="$work/pkgconfig-$rid"
  rm -rf "$pc"
  mkdir -p "$pc"
  for entry in vpx:libvpx opus:libopus; do
    name="${entry%%:*}"; lib="${entry##*:}"
    libdir="$(pkg-config --variable=libdir "$name")"
    if [[ ! -f "$libdir/$lib.a" ]]; then
      echo "$lib has no static library ($libdir/$lib.a), which FFmpeg's libraries are linked with so they don't need $lib beside them." >&2
      exit 1
    fi
    sed "s|^Libs:.*|Libs: $libdir/$lib.a|" "$(pkg-config --variable=pcfiledir "$name")/$name.pc" > "$pc/$name.pc"
  done
  export PKG_CONFIG_PATH="$pc${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
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
  --pkg-config-flags=--static --enable-libvpx --enable-libopus \
  --enable-avformat --enable-avcodec --enable-swscale --enable-swresample \
  --enable-demuxer=mov,avi,matroska,ogg,mpegps,mpegvideo,m4v,h264,hevc,ivf \
  --enable-muxer=mov,mp4,matroska,webm,avi,ogg,ivf,mpeg \
  --enable-decoder=h264,hevc,vp8,vp9,theora,mpeg1video,mpeg2video,mpeg4,msmpeg4v1,msmpeg4v2,msmpeg4v3,mjpeg,aac,vorbis,opus,mp1float,mp2float,mp3float \
  --enable-parser=h264,hevc,vp8,vp9,mpegvideo,mpeg4video,mjpeg \
  --enable-encoder=libvpx_vp8,libopus \
  --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb,vp9_superframe_split,aac_adtstoasc,pgs_frame_merge \
  --enable-protocol=file

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
  osx-*)
    # libvpx and libopus must be inside libavcodec, not libraries it loads: they aren't shipped.
    if command -v otool >/dev/null 2>&1 && otool -L "$prefix"/lib/libavcodec.*.dylib | grep -qi -E 'vpx|opus'; then
      echo "libavcodec needs a libvpx or libopus library, which won't be on users' machines: they should have been linked in statically." >&2
      exit 1
    fi
    for f in "$prefix"/lib/lib*.[0-9]*.dylib; do [[ "$f" =~ \.[0-9]+\.dylib$ ]] && cp -L "$f" "$out/"; done ;;
  *)
    if command -v readelf >/dev/null 2>&1 && readelf -d "$prefix"/lib/libavcodec.so.[0-9]* | grep -i NEEDED | grep -qi -E 'vpx|opus'; then
      echo "libavcodec needs a libvpx or libopus library, which won't be on users' machines: they should have been linked in statically." >&2
      exit 1
    fi
    for f in "$prefix"/lib/lib*.so.[0-9]*; do [[ "$f" =~ \.so\.[0-9]+$ ]] && cp -L "$f" "$out/"; done ;;
esac

# Says this build has the VP8 encoder: Get-FfmpegProblem in build/publish.ps1 looks for it, as the DLLs alone don't say.
echo "libvpx VP8 encoder (BSD-3-Clause), linked into libavcodec." > "$out/libvpx.txt"
# The same for the Opus encoder, which Get-FfmpegProblem also looks for.
echo "libopus Opus encoder (BSD-3-Clause), linked into libavcodec." > "$out/libopus.txt"

echo "FFmpeg $tag for $rid is in $out:"
ls -l "$out"
