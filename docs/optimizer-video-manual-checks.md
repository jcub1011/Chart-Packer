# Optimizer video: manual checks

The video optimizer's skip rules, the opt-in conversion of unsupported codecs, the re-encoder (size
fitting, no audio, timing, cancel and failure) and the estimate's accuracy are covered by
`VideoReencoderTests`, `VideoOptimizerTests` and `OptimizeVideoTests`. They need FFmpeg with the
libvpx encoder beside the tests and are inconclusive without it. What they can't cover is a real
FFmpeg build (the release one, built in MSYS2), and playing the result in the games.

Defaults: VP8 in `.webm`, no audio track, fitted inside 1920x1080 (FHD) or 3840x2160 (UHD) and never
scaled up, libvpx CQ 32 with a bitrate cap, balanced speed (libvpx `cpu-used` 2), a keyframe every 128 frames, the source's frame rate. A
video that's already VP8 WebM within the target is left alone. Use throwaway copies of charts, not
the library.

## The FFmpeg build

1. `build/ffmpeg/build.sh win-x64` from Git Bash (or `build/publish.ps1`, which offers to), in a
   shell that has MSYS2 with `mingw-w64-ucrt-x86_64-libvpx` (the script offers to install it).
2. It must finish. If it stops at "needs `libvpx-1.dll`" (or any DLL that isn't on the allowed list),
   libvpx was linked as a shared library: it has to be the static `libvpx.a`. See the README.
3. `ChartShared.Ffmpeg/runtimes/win-x64/native/` has the five DLLs and `libvpx.txt`.
4. `dotnet test --project ChartShared.Core.Tests` runs the video tests rather than skipping them.
5. An FFmpeg built before this (no `libvpx.txt`) is refused by `build/publish.ps1`, which offers to
   rebuild it.

## The games

Play each result in Clone Hero and in YARG, on the platforms you have.

| # | Input | Expect |
| - | ----- | ------ |
| 1 | An H.264 1080p `.mp4` | Becomes `video.webm`, VP8, no audio. Plays in both games, in sync with the song, and looks right (colours, brightness). |
| 2 | A 4K VP8 `.webm` with FHD as the target | 1920x1080, much smaller. Plays. |
| 3 | The same with UHD as the target | Left alone: already VP8 WebM within the target. |
| 4 | A 720p H.264 | Stays 720p (never upscaled). |
| 5 | A portrait or an odd-sized video (1177x663) | Both sides even; the shape unchanged. |
| 6 | A video with a lead-in before its first frame, or with `video_start_time` set | The video still lines up with the audio. |
| 7 | A variable-frame-rate recording | Still in sync at the end, not just at the start. |
| 8 | A tiny low-bitrate H.264 (say 600 kbps) | Reported **would grow** and skipped: even CQ 32 spends more bits than the source did. |
| 9 | A VP9, AV1 or HEVC video, **convert for compatibility off** | Skipped as an unsupported codec; nothing estimated for the plan. The savings estimate still shows its codec, with the size it would be converted to. |
| 10 | The same with it on | Converted to VP8 WebM whatever the size does, margin and larger-file rules ignored; the result may be larger and is kept. |
| 11 | An AV1 video, with a release build of FFmpeg | Skipped as an estimate failure: the release build has no AV1 decoder (only the prebuilt development one does). |
| 12 | A `.sng` and a song folder of the same chart | The same result for each; the `.sng` is rewritten with the new video. |
| 13 | Cancel partway through a long video | No `.webm` or `*.chartpacker-tmp` is left, and the chart is unchanged. |
| 14 | A damaged or truncated video | Skipped as an estimate failure, or failed for that file only; the chart's other files still run. |
| 16 | Speed set to Fast, Balanced (the default), Slow and Best on one video | Each plays. Fast is about twice as fast as Slow and 10 to 20% larger for the same picture; Best is the slowest and smallest. An estimate at a speed matches the size that speed writes. |
| 17 | A library of videos with parallelism 1, then 2, then 4 | The same results each time, in the same order. Total time falls (about 2x at 4 on a 16-core machine); the CPU is shared, not oversubscribed, and the UI stays responsive. |
| 18 | Cancel with several videos encoding at once | All stop; no `.webm`, `.*.webm` or `*.chartpacker-tmp` is left, and each chart is as it was. |
| 15 | Estimate with single chart, then random sample, then all charts | All are grouped by source codec; a sampled estimate of a long video is within about 25% of the real size, and all charts is the slowest. |

After every run: no `*.chartpacker-tmp` files or folders, and no `.*.webm` temp files, anywhere in
the library or output folder, including after cancelling mid-run.
