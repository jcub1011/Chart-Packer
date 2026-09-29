# DecodeBench results

Phase 1 of the faster-decoding work: how fast the app's managed decoders are, how much of a
waveform read is decoding, and how fast FFmpeg's native decoders are on the same files.

The tables below were taken with Phase 1's prototype, which had FFmpeg demux and decode each file
itself (the `Ffmpeg` benchmark). The tool now runs every benchmark on both of the app's backends
(`Backend` = `Managed` or `Ffmpeg`), and there's no separate `Ffmpeg` benchmark. See
[Phase 2](#phase-2-ffmpeg-decodes-the-apps-audio) for what was built.

## Setup

- **Machine:** Intel Core i9-9900K (8 cores, 16 threads), Windows 10 22H2, .NET SDK 10.0.401,
  runtime 10.0.12.
- **Benchmarking:** BenchmarkDotNet 0.15.8. Each case has 2 warm-ups and 10 iterations in one
  launch. The derived columns (RTF, decode share, FFmpeg speed-up) are worked out from the
  **median**.
- **Runtimes:** two jobs.
  - **JIT:** RyuJIT.
  - **NativeAOT:** built by BenchmarkDotNet's NativeAOT toolchain, as the app ships.
- **FFmpeg:** the local BtbN 9.0 build (`avcodec-63`). Decoders: `opus`, `vorbis` and `mp3float`.
  All three output `FLTP`.
- **Run with:**

  ```
  dotnet run -c Release --project tools/DecodeBench -- --aot
  dotnet run -c Release --project tools/DecodeBench -- "<library>" "<multi-stem .sng>" --per-format 2 --aot
  ```

  Stems stored in a `.sng` are read in place through `SngEntryStream`, as the app reads them.
  Nothing is extracted. The library isn't in the repo, and it's named here only by stem shape.

## What each benchmark measures

| Benchmark | What it times |
|---|---|
| `Open` | `AudioDecoder.Open`: the MP3/Opus index scan and NVorbis's setup. |
| `Decode` | A `ReadFrames` loop over the whole stem, on a source already open, rewound to 0. |
| `PeaksOneThread` | `WaveformPeaks.Compute` over the whole stem, on one thread. |
| `PeaksChunked` | `StemPeaksReader` as `PlayerViewModel.ReadPeaks` reads: 15 s chunks, 15 at a time, each chunk but the first opening its own source. |
| `Ffmpeg` | FFmpeg demuxing and decoding the whole stem, one thread (`threads=1`), interleaving each frame into a float buffer as a source would. Opening isn't timed; it seeks back to the start. |

- **RTF:** the real-time factor, seconds of audio read per second of wall time.
- **Decode share:** `Decode` ÷ `PeaksOneThread`, meaning how much of a waveform read is decoding.
- **FFmpeg speed-up:** `Decode` ÷ `Ffmpeg`, both on one core.

## Summary

FFmpeg speed-up per core, median, on JIT and NativeAOT:

| Format | Fixture (1 s) | Real songs | Decode share of peaks |
|---|--:|--:|--:|
| MP3 (NLayer) | **8.4× / 7.9×** | none in the library | 92–100% |
| Vorbis (NVorbis) | **3.1× / 3.5×** | none in the library | 87–92% |
| Opus (Concentus) | **2.1–2.2×** | **1.9–2.3×** (one noisy outlier, 3.2×) | 93–102% |

1. **Decoding is the whole cost.** It takes 87–102% of a one-thread peaks read. Anything above
   100% is noise between two separate benchmarks. The peaks maths isn't worth optimising.
2. **JIT and Native AOT perform the same, within noise.** AOT is slightly slower on some rows,
   for example Opus 3:41 decodes at 186× on JIT and 160× on AOT, but it isn't consistent. So
   shipping AOT isn't what makes decoding slow.
3. **Chunked reading already scales well.** A 7:27 Opus song's peaks take 0.29–0.39 s chunked,
   against 1.8–2.0 s on one thread, which is 5–6.4× on 8 physical cores. A faster decoder
   speeds up both by about the same factor.
4. **Opening an Opus stem inside a `.sng` is 3–6× slower than a loose file**: 3.8–6.8 ms,
   against 1.0–1.3 ms. The Ogg page index scan does small reads through
   `BufferedStream`/`SngEntryStream`. Each chunk opens its own source, so a 30-chunk song spends
   about 150 ms of CPU opening. Measured against about 4.5 s of chunked CPU time, that's around 3%.
5. **The library is all Opus**: 28 stems, all stereo 48 kHz, one song multi-stem. So Opus is the
   format that matters for the waveform the user sees, and it's the one with the smallest
   FFmpeg gain.

## Frame counts (sanity)

- **FFmpeg matches the managed decoders exactly on a first read of every real song**, for
  example 21,488,586 frames for the 7:27 song.
- **On the fixtures:** MP3 and Opus match. FFmpeg's Vorbis gives **43,972** frames against
  NVorbis's 44,100, which is 128 short. That's FFmpeg's own start trim, and it's a sign of the
  Vorbis timeline risk the Phase 2 plan names.
- **After the benchmark's seek back to the start,** FFmpeg reads 312 extra Opus frames (the
  pre-skip isn't re-applied) and 636 extra Vorbis frames. That's under 1.5% more work on a
  1-second fixture, and a negligible amount on a song.

## Cheap fixes spotted in the wrappers (not measured; no profiler was run)

- **`MpegFrame.ReadBits`** (`ChartPacker.Audio/Decoding/MpegFrame.cs:117`) reads **one bit per
  loop iteration**. NLayer calls it for every header field and Huffman symbol. It's our code, and
  it may account for a large share of MP3's 8× gap. Reading up to 32 bits at a time, from a
  64-bit window, is a small change.
- **Per-frame and per-page allocations:**
  - `MpegFrame.Read` allocates a `new byte[FrameLength]` for every frame.
  - `OggPageReader.LoadPage` allocates a `new byte[BodyLength]` for every page.
  - Both could reuse one growing buffer. They're GC pressure more than CPU.
- **Opus open inside a `.sng`** (finding 4): chunk sources could share the stem's page index,
  rather than each scanning it again.

## Recommendation

- **MP3: Phase 2 is worth it (8×)**, but first try the `ReadBits` fix. It's cheap, and it may
  close much of the gap without FFmpeg. There are no MP3 stems in the library, so this matters
  less today.
- **Vorbis: Phase 2 clears the 2× bar (3–3.5×).** It's also the riskiest part, because of the new
  Ogg Vorbis source and the timeline work, and the library has no Vorbis stems.
- **Opus: borderline (about 2×).** It's the format the library actually uses. Phase 2 would about
  halve both waveform time and playback CPU for Opus. That's real, but it's at the threshold, not
  well past it. It's also the lowest-risk format to move, since `OpusSource` already does the
  container and timing and FFmpeg only replaces the packet decoder.

Suggested order, if you go ahead: **Opus first** (most used, low risk, about 2×), then **MP3**
(8×, after trying `ReadBits`), then **Vorbis** last (3×, highest risk). If about 2× on Opus isn't
worth adding swresample and the new `ChartPacker.Ffmpeg` project, the alternative is the cheap
fixes above, which help MP3 most and Opus hardly at all.

## Phase 2: FFmpeg decodes the app's audio

All three formats went to FFmpeg, through `ChartPacker.Ffmpeg`'s `FfmpegAudioDecoder`, for mono and
stereo stems. Only packets go to FFmpeg, and the app's own container code keeps the timing, trims
and seeks. The managed decoders stay as the fallback.

**The three cheap fixes above were made:**

- `ReadBits` reads up to 32 bits at once;
- one MP3 frame buffer is reused;
- one Ogg page buffer is reused.

The managed output is **bit-identical** before and after: a SHA-256 of the decoded samples, on a
straight read and after four seeks, for every fixture and two real Opus songs.

**How closely the backends agree** (`DecoderTests.TheBackendsAgree`):

| Format | Length and timing | Largest sample gap | SNR |
|---|---|--:|--:|
| Vorbis | same | 4.5e-7 | 131 dB |
| MP3 | same | 1.1e-3, in the last 3 frames | 87–109 dB |
| Opus fixtures | same (best lag 0) | 3.6e-4 (mono), 7.7e-2 (stereo) | 52–72 dB |
| Real 6-minute Opus song | same (best lag 0) | 0.15; 0.01% of frames off by more than 1e-3 | 59.6 dB |

FFmpeg's Opus decoder is a separate implementation from Concentus, and Opus is specified by the
quality of what's decoded rather than by exact samples, so the two differ at about 60 dB below the
signal. That's inaudible, and stems still line up.

**Not measured after Phase 2.** The benchmarks weren't re-run, at the user's call. Phase 1 had
already measured FFmpeg's decoders at about 2× (Opus), 3× (Vorbis) and 8× (MP3) the managed ones
per core, and `ChartPacker.Audio` now feeds them the same packets. To measure it, run the commands
under [Setup](#setup): each benchmark then has an `FFmpeg speed-up` column.

## Full results

### Fixtures (`ChartPacker.Core.Tests/Audio/Fixtures`, 1 s each)

| Method | Job | Audio | Median | RTF | Decode share | FFmpeg speed-up |
| --------------- | ---------- | -------------------------------------------------- | ------------: | ------: | -------------: | ----------------: |
| Open | JIT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 69.70 us | - | - | - |
| Decode | JIT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 5,163.98 us | 194× | - | - |
| PeaksOneThread | JIT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 5,619.90 us | 178× | 92% | - |
| PeaksChunked | JIT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 5,612.62 us | 178× | - | - |
| Ffmpeg | JIT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 616.59 us | 1622× | - | 8.4× |
| Open | NativeAOT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 150.38 us | - | - | - |
| Decode | NativeAOT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 5,001.54 us | 200× | - | - |
| PeaksOneThread | NativeAOT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 5,009.89 us | 200× | 100% | - |
| PeaksChunked | NativeAOT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 5,200.55 us | 192× | - | - |
| Ffmpeg | NativeAOT | mp3 2ch 0m01s chirp-stereo-44100.mp3 (loose) #3 | 631.03 us | 1585× | - | 7.9× |
| Open | JIT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 2,032.38 us | - | - | - |
| Decode | JIT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 1,542.27 us | 648× | - | - |
| PeaksOneThread | JIT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 1,775.90 us | 563× | 87% | - |
| PeaksChunked | JIT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 1,757.87 us | 569× | - | - |
| Ffmpeg | JIT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 503.70 us | 1985× | - | 3.1× |
| Open | NativeAOT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 2,203.07 us | - | - | - |
| Decode | NativeAOT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 1,781.82 us | 561× | - | - |
| PeaksOneThread | NativeAOT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 1,937.17 us | 516× | 92% | - |
| PeaksChunked | NativeAOT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 1,995.85 us | 501× | - | - |
| Ffmpeg | NativeAOT | ogg 2ch 0m01s chirp-stereo-44100.ogg (loose) #4 | 510.86 us | 1957× | - | 3.5× |
| Open | JIT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 72.11 us | - | - | - |
| Decode | JIT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 2,925.17 us | 342× | - | - |
| PeaksOneThread | JIT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 3,082.31 us | 324× | 95% | - |
| PeaksChunked | JIT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 3,113.79 us | 321× | - | - |
| Ffmpeg | JIT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 1,415.50 us | 706× | - | 2.1× |
| Open | NativeAOT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 152.44 us | - | - | - |
| Decode | NativeAOT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 2,998.76 us | 333× | - | - |
| PeaksOneThread | NativeAOT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 3,189.21 us | 314× | 94% | - |
| PeaksChunked | NativeAOT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 3,182.12 us | 314× | - | - |
| Ffmpeg | NativeAOT | opus 1ch 0m01s chirp-mono-48000.opus (loose) #1 | 1,448.40 us | 690× | - | 2.1× |
| Open | JIT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 73.69 us | - | - | - |
| Decode | JIT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 5,631.04 us | 178× | - | - |
| PeaksOneThread | JIT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 5,908.90 us | 169× | 95% | - |
| PeaksChunked | JIT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 5,855.49 us | 171× | - | - |
| Ffmpeg | JIT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 2,602.17 us | 384× | - | 2.2× |
| Open | NativeAOT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 157.79 us | - | - | - |
| Decode | NativeAOT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 5,691.38 us | 176× | - | - |
| PeaksOneThread | NativeAOT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 5,685.36 us | 176× | 100% | - |
| PeaksChunked | NativeAOT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 5,965.66 us | 168× | - | - |
| Ffmpeg | NativeAOT | opus 2ch 0m01s chirp-stereo-48000.opus (loose) #2 | 2,637.35 us | 379× | - | 2.2× |

### Real songs (Opus, from the Test Library)

| Method | Job | Audio | Median | RTF | Decode share | FFmpeg speed-up |
| --------------- | ---------- | ------------------------------------ | ---------------: | ------: | -------------: | ----------------: |
| Open | JIT | opus 2ch 3m41s song.opus (sng) #4 | 5,480.9 us | - | - | - |
| Decode | JIT | opus 2ch 3m41s song.opus (sng) #4 | 1,189,742.9 us | 186× | - | - |
| PeaksOneThread | JIT | opus 2ch 3m41s song.opus (sng) #4 | 1,180,909.1 us | 187× | 101% | - |
| PeaksChunked | JIT | opus 2ch 3m41s song.opus (sng) #4 | 207,327.4 us | 1068× | - | - |
| Ffmpeg | JIT | opus 2ch 3m41s song.opus (sng) #4 | 630,242.8 us | 351× | - | 1.9× |
| Open | NativeAOT | opus 2ch 3m41s song.opus (sng) #4 | 6,636.2 us | - | - | - |
| Decode | NativeAOT | opus 2ch 3m41s song.opus (sng) #4 | 1,387,162.7 us | 160× | - | - |
| PeaksOneThread | NativeAOT | opus 2ch 3m41s song.opus (sng) #4 | 1,355,134.2 us | 163× | 102% | - |
| PeaksChunked | NativeAOT | opus 2ch 3m41s song.opus (sng) #4 | 217,834.6 us | 1016× | - | - |
| Ffmpeg | NativeAOT | opus 2ch 3m41s song.opus (sng) #4 | 707,863.2 us | 313× | - | 2.0× |
| Open | JIT | opus 2ch 3m41s vocals.opus (sng) #3 | 6,358.8 us | - | - | - |
| Decode | JIT | opus 2ch 3m41s vocals.opus (sng) #3 | 1,341,528.4 us | 165× | - | - |
| PeaksOneThread | JIT | opus 2ch 3m41s vocals.opus (sng) #3 | 1,391,129.0 us | 159× | 96% | - |
| PeaksChunked | JIT | opus 2ch 3m41s vocals.opus (sng) #3 | 236,851.5 us | 935× | - | - |
| Ffmpeg | JIT | opus 2ch 3m41s vocals.opus (sng) #3 | 708,004.0 us | 313× | - | 1.9× |
| Open | NativeAOT | opus 2ch 3m41s vocals.opus (sng) #3 | 6,816.9 us | - | - | - |
| Decode | NativeAOT | opus 2ch 3m41s vocals.opus (sng) #3 | 1,411,267.6 us | 157× | - | - |
| PeaksOneThread | NativeAOT | opus 2ch 3m41s vocals.opus (sng) #3 | 1,454,173.4 us | 152× | 97% | - |
| PeaksChunked | NativeAOT | opus 2ch 3m41s vocals.opus (sng) #3 | 233,268.4 us | 949× | - | - |
| Ffmpeg | NativeAOT | opus 2ch 3m41s vocals.opus (sng) #3 | 713,892.2 us | 310× | - | 2.0× |
| Open | JIT | opus 2ch 7m27s song.opus (loose) #2 | 977.3 us | - | - | - |
| Decode | JIT | opus 2ch 7m27s song.opus (loose) #2 | 2,324,948.7 us | 193× | - | - |
| PeaksOneThread | JIT | opus 2ch 7m27s song.opus (loose) #2 | 1,949,623.6 us | 230× | 119% | - |
| PeaksChunked | JIT | opus 2ch 7m27s song.opus (loose) #2 | 285,449.3 us | 1568× | - | - |
| Ffmpeg | JIT | opus 2ch 7m27s song.opus (loose) #2 | 731,411.8 us | 612× | - | 3.2× |
| Open | NativeAOT | opus 2ch 7m27s song.opus (loose) #2 | 1,293.6 us | - | - | - |
| Decode | NativeAOT | opus 2ch 7m27s song.opus (loose) #2 | 1,872,768.9 us | 239× | - | - |
| PeaksOneThread | NativeAOT | opus 2ch 7m27s song.opus (loose) #2 | 2,010,751.9 us | 223× | 93% | - |
| PeaksChunked | NativeAOT | opus 2ch 7m27s song.opus (loose) #2 | 307,241.8 us | 1457× | - | - |
| Ffmpeg | NativeAOT | opus 2ch 7m27s song.opus (loose) #2 | 807,872.8 us | 554× | - | 2.3× |
| Open | JIT | opus 2ch 7m27s song.opus (sng) #1 | 3,794.2 us | - | - | - |
| Decode | JIT | opus 2ch 7m27s song.opus (sng) #1 | 1,810,795.9 us | 247× | - | - |
| PeaksOneThread | JIT | opus 2ch 7m27s song.opus (sng) #1 | 1,917,524.1 us | 233× | 94% | - |
| PeaksChunked | JIT | opus 2ch 7m27s song.opus (sng) #1 | 385,107.3 us | 1162× | - | - |
| Ffmpeg | JIT | opus 2ch 7m27s song.opus (sng) #1 | 820,815.7 us | 545× | - | 2.2× |
| Open | NativeAOT | opus 2ch 7m27s song.opus (sng) #1 | 4,038.2 us | - | - | - |
| Decode | NativeAOT | opus 2ch 7m27s song.opus (sng) #1 | 1,790,060.1 us | 250× | - | - |
| PeaksOneThread | NativeAOT | opus 2ch 7m27s song.opus (sng) #1 | 1,771,839.4 us | 253× | 101% | - |
| PeaksChunked | NativeAOT | opus 2ch 7m27s song.opus (sng) #1 | 303,907.8 us | 1473× | - | - |
| Ffmpeg | NativeAOT | opus 2ch 7m27s song.opus (sng) #1 | 784,314.9 us | 571× | - | 2.3× |
