# EncodeBench

Times the Library Optimization Tool's VP8 encodes and estimates on real videos. Never shipped; the videos are only
read, and what's encoded goes to a temp folder that's deleted.

```
dotnet run -c Release --project tools/EncodeBench -- speed <video>... [--frames 400]
dotnet run -c Release --project tools/EncodeBench -- parallel <video>... [--frames 300]
dotnet run -c Release --project tools/EncodeBench -- estimate <video>...
```

- **`speed`** encodes one window from the middle of each video at each encoder setting (`cpu-used` 0 to 5, libvpx's
  realtime mode, encoder threads, token partitions, scaler and decoder threads), and prints frames a second and size
  beside the default's.
- **`parallel`** encodes windows of the videos several at once, for each split of the machine between videos at once
  and threads each, and prints the frames a second of all of them together. It holds as many decoders as videos at
  once, so the widest splits need several GB of memory.
- **`estimate`** encodes each video whole at the default speed, then estimates it at each candidate setting, and
  prints each estimate over the size written, with the factor and the error left once the factor is applied.
- `--target sd|hd|fhd|qhd|uhd`, `--cq N` and `--threads N` set what's encoded (Full HD, CQ 32 and 4 threads by default).

Whole encodes take minutes each: give `estimate` two or three videos at a time.

## What it found

On a 16-core machine with FFmpeg n9.0.2, Full HD at CQ 32, on 1080p H.264 and VP8 sources.

**Encoder threads scale badly.** At `cpu-used` 2, one thread encoded 13 and 27 frames a second on two videos, two
threads 22 and 42, four 34 and 58, eight 38 and 62. More threads also wrote slightly larger files (eight threads 8%
more than four, one thread 7 to 11% less).

**The machine is nearly full already.** Frames a second of everything at once, by videos at once and threads each:

| videos | threads | total fps |
| --- | --- | --- |
| 1 | 8 | 35 |
| 2 | 8 | 51 |
| 4 | 4 | 67 |
| 4 | 8 | 71 |
| 6 | 4 | 77 |
| 8 | 2 | 73 |
| 8 | 4 | 72 |
| 12 | 2 | 79 |
| 16 | 1 | 80 |
| 16 | 2 | 79 |

Four videos with four threads each, what the tool did, is within 20% of the best of any split. Six with four is 15%
better and holds half as many decoders as the widest. Holding each decoder to its encode's thread count made every
split slower (by 5 to 40%), so decoders are left to FFmpeg.

**Speed is the encoder's setting.** Beside `cpu-used` 2 on two videos: 0 ran at 0.6 of its speed, 1 at 0.75, 3 at 1.0
to 1.2, 4 at 1.3 to 1.8, 5 at 3.0 to 3.4. At the same CQ, 5 wrote 0.90 and 1.34 of the size 2 did. Realtime mode was
no faster than `cpu-used` 5 until `cpu-used` 16, which wrote 1.5 times the size.

**Nothing else moved it.** Token partitions (2, 4, 8), scaler threads and decoder threads each changed the speed by
less than the run-to-run noise.

**Estimating at `cpu-used` 5** took 0.28 of the time of estimating at `cpu-used` 2 on six videos. Estimate over the
size a whole encode at `cpu-used` 2 wrote:

| video | at `cpu-used` 2 | at `cpu-used` 5 |
| --- | --- | --- |
| 1 | 0.97 | 0.82 |
| 2 | 0.95 | 0.83 |
| 3 | 1.01 | 1.14 |
| 4 | 1.33 | 1.21 |
| 5 | 0.91 | 0.92 |
| 6 | 1.33 | 1.40 |

Most of the error is the two windows not being the whole video, which the speed doesn't change. Scaled by its
average, the quick estimate is out by 19% on average and 33% at worst; the slow one by 15% and 23%.
