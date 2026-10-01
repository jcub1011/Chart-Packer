# FFmpeg for background video and audio

`ChartPacker.Ffmpeg` binds FFmpeg 9.0's shared libraries (`avformat`, `avcodec`, `avutil`,
`swscale`, and `swresample`, which the Opus decoder needs). `ChartPacker.Video` plays songs'
background videos through them. `ChartPacker.Audio` decodes Vorbis, Opus and MPEG audio packets
with them, having read the containers itself. The libraries go in
`ChartPacker.Ffmpeg/runtimes/<rid>/native/`, which is gitignored. From there they're copied beside
the executable on build and publish, as SDL's library is.

Without them the app builds and runs:

- a song's video shows "Video preview unavailable";
- audio is decoded by the managed decoders (NVorbis, NLayer and Concentus), which are several times
  slower (see `tools/DecodeBench/RESULTS.md`);
- the video decoder tests and the FFmpeg rows of the audio decoder tests are skipped as
  inconclusive.

Libraries built before the audio decoders were added still play video. Audio falls back to the
managed decoders.

## For a release: `build.sh <rid>`

This builds FFmpeg from the `n9.0.2` tag with only what the app needs, and nothing GPL or non-free:

- the demuxers and decoders of the registered video formats (sng-spec §8);
- the decoders of the registered audio formats: `vorbis`, `opus`, `mp1float`, `mp2float` and
  `mp3float`;
- swresample.

The result is LGPL 2.1+ and a few MB per RID.

`build/publish.ps1` offers to run this for its RID when the libraries are missing or have no
swresample. Pass `-Yes` to run it, and install what it needs, without being asked.

| RID | Where to run it |
| --- | --- |
| `win-x64`, `win-arm64` | Natively in MSYS2 (UCRT64 shell for `win-x64`, CLANGARM64 on an ARM PC for `win-arm64`), or cross-compiled on Linux or WSL with `mingw-w64` (`x86_64-w64-mingw32-gcc`) or llvm-mingw (`aarch64-w64-mingw32-clang`) |
| `linux-x64`, `linux-arm64` | That platform |
| `osx-x64`, `osx-arm64` | macOS with Xcode's command-line tools |

It needs `git`, `make`, `pkg-config`, a C compiler and, for x86-64, `nasm`. The script checks for
these before it starts. If any are missing, it shows the command that installs them and asks
before running it. It uses pacman on MSYS2; apt, dnf or pacman on Linux; and `xcode-select` and
Homebrew on macOS. Pass `--yes` (`build.sh --yes <rid>`) to install without being asked. Without a
terminal, it prints the command and stops.

Git Bash can't install a compiler. FFmpeg's `configure` is a POSIX shell script that needs GNU make
and a GCC or Clang toolchain, and MSYS2 is the standard way to get those on Windows. So when you run
a Windows RID from Git Bash, the script runs itself again in MSYS2. If MSYS2 isn't in `C:\msys64`
(or `MSYS2_ROOT`), it offers to install it with winget first.

The build runs in `build/ffmpeg/.work`. When the repo's path has a space in it, such as
`C:\Users\First Last\...`, it runs in `/tmp/chartpacker-ffmpeg` instead, because FFmpeg's configure
won't build from such a path. Set `FFMPEG_WORK` to choose another place.

### On Windows, with MSYS2

From Git Bash, or from MSYS2's UCRT64 shell:

```
cd "/c/Users/<you>/source/repos/Chart-Packer"
build/ffmpeg/build.sh win-x64
```

The first run offers to install MSYS2 (`winget install MSYS2.MSYS2`). Then, in MSYS2's UCRT64 shell,
it offers to install the toolchain:

```
pacman -S --needed git make diffutils mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-nasm
```

`configure` is slow under MSYS2, so the build takes several minutes. The DLLs are linked with
`-static`, so they need nothing from MSYS2 at run time. The script checks each DLL's imports, and
stops if one needs a toolchain DLL such as `libwinpthread-1.dll`. Don't run it
from the plain MSYS shell, whose binaries need `msys-2.0.dll`.

For `win-arm64`, it builds in the CLANGARM64 shell, which needs an ARM PC. You can also
cross-compile it from WSL with llvm-mingw.

### On Windows, with WSL

1. Install a distribution, once, from PowerShell: `wsl --install -d Ubuntu`.
2. Build from the repo, which WSL sees under `/mnt/c`:

   ```
   cd "/mnt/c/Users/<you>/source/repos/Chart-Packer"
   build/ffmpeg/build.sh win-x64
   ```

   The first run offers to install the tools and the mingw-w64 cross-compiler with apt. It takes a few minutes. The libraries it copies into `ChartPacker.Ffmpeg/runtimes/win-x64/native/`
   replace whatever was there, including a `fetch-prebuilt.sh` download.

## For development: `fetch-prebuilt.sh <rid>`

This downloads a prebuilt LGPL FFmpeg 9.0 from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) for `win-x64`, `win-arm64`,
`linux-x64` or `linux-arm64`, and copies the libraries in. It needs no compiler, and runs in Git
Bash on Windows. These builds enable much more than the app needs, so they're about 120 MB.
Don't ship them.

## Another version

The bindings in `ChartPacker.Ffmpeg/Interop/Native.cs` declare the leading fields of FFmpeg's
structs for these major versions: avformat 63, avcodec 63, avutil 61, swscale 10.
`FfmpegLibrary` refuses any other major version. To move to one:

1. Check those fields, and the constants beside them, against the new headers.
2. Update the version constants.
3. Update the tag in `build.sh` and the branch in `fetch-prebuilt.sh`.
