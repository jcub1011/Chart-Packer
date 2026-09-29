# FFmpeg for background video

`ChartPacker.Video` plays songs' background videos through FFmpeg 9.0's shared libraries
(`avformat`, `avcodec`, `avutil`, `swscale`). They go in
`ChartPacker.Video/runtimes/<rid>/native/`, which is gitignored. From there they're copied beside
the executable on build and publish, as SDL's library is.

Without them the app builds and runs, and a song's video shows "Video preview unavailable". The
video decoder tests are skipped as inconclusive.

## For a release: `build.sh <rid>`

This builds FFmpeg from the `n9.0` tag with only the demuxers and decoders of the registered video
formats (sng-spec §8), and nothing GPL or non-free. The result is LGPL 2.1+ and a few MB per RID.

| RID | Where to run it |
| --- | --- |
| `win-x64`, `win-arm64` | Linux or WSL, with `mingw-w64` (`x86_64-w64-mingw32-gcc`) or llvm-mingw (`aarch64-w64-mingw32-clang`) |
| `linux-x64`, `linux-arm64` | That platform |
| `osx-x64`, `osx-arm64` | macOS with Xcode's command-line tools |

It needs `git`, `make`, `pkg-config`, a C compiler and, for x86-64, `nasm`.

## For development: `fetch-prebuilt.sh <rid>`

This downloads a prebuilt LGPL FFmpeg 9.0 from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) for `win-x64`, `win-arm64`,
`linux-x64` or `linux-arm64`, and copies the libraries in. It needs no compiler, and runs in Git
Bash on Windows. These builds enable much more than the app needs, so they're about 120 MB.
Don't ship them.

## Another version

The bindings in `ChartPacker.Video/Interop/Ffmpeg.cs` declare the leading fields of FFmpeg's
structs for these major versions: avformat 63, avcodec 63, avutil 61, swscale 10.
`FfmpegLibrary` refuses any other major version. To move to one, check those fields against the
new headers, then update the version constants, the tag in `build.sh` and the branch in
`fetch-prebuilt.sh`.
