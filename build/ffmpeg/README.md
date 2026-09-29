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

This builds FFmpeg from the `n9.0` tag with only what the app needs, and nothing GPL or non-free:

- the demuxers and decoders of the registered video formats (sng-spec §8);
- the decoders of the registered audio formats: `vorbis`, `opus`, `mp1float`, `mp2float` and
  `mp3float`;
- swresample.

The result is LGPL 2.1+ and a few MB per RID.

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

The bindings in `ChartPacker.Ffmpeg/Interop/Native.cs` declare the leading fields of FFmpeg's
structs for these major versions: avformat 63, avcodec 63, avutil 61, swscale 10.
`FfmpegLibrary` refuses any other major version. To move to one:

1. Check those fields, and the constants beside them, against the new headers.
2. Update the version constants.
3. Update the tag in `build.sh` and the branch in `fetch-prebuilt.sh`.
