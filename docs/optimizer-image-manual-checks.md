# Optimizer images: manual checks

The image optimizer's decisions (size fitting, which result is kept, renames, the song.ini and
metadata follow-up) are covered by `ImageOptimizationTests` and `OptimizeJobImageTests`. The real
encoder, `AvaloniaImageOptimizer`, needs Avalonia's Skia platform, which the test projects don't
start, so it's checked by hand in Chart Packer or Chart Builder.

Defaults: album art fits 512x512, a background image fits 1920x1080, JPEG quality 80, nothing is
ever scaled up, and `highway.*` is never touched. Use throwaway copies of charts, not the library.

For each case below, run the optimizer on **a song folder and a `.sng`** of it, once in place and
once with an output folder, and look at the estimate (it's the real encode, so it should match the
size that's written) and at the written file.

| # | Input | Expect |
| - | ----- | ------ |
| 1 | Opaque PNG album art, 1024x1024 | Becomes `album.jpg`, 512x512, much smaller. `song.ini` / sng metadata values that said `album.png` now say `album.jpg`. Opens in the game. |
| 2 | Opaque PNG that's already tiny and well compressed (say 64x64 flat colours) | Left alone, as **already optimal**: the PNG is smaller than its JPEG. Nothing written, no rename. |
| 3 | PNG with transparency (soft edges or a cut-out), larger than the limit | Stays `.png`, resized, transparency intact (no black or white fringe). Never becomes JPEG. |
| 4 | PNG with transparency, already within the limit | Left alone. |
| 5 | Large JPEG background, 3840x2160 | Stays `.jpg`, 1920x1080, smaller. Aspect ratio unchanged. |
| 6 | Large JPEG with the `.jpeg` extension | Keeps the `.jpeg` name. |
| 7 | Already-small JPEG (say 300x300, quality 60) | Skipped: the result isn't smaller, or the saving is below the margin. Never upscaled. |
| 8 | CMYK JPEG, progressive JPEG, JPEG with an odd or truncated segment | Either optimizes cleanly or is skipped as an estimate failure (the chart's others still run). Never crashes the run, never leaves a half file. |
| 9 | Tall or wide background (4000x1000 and 1000x4000) | Fits inside 1920x1080 by whichever side binds; no distortion. |
| 10 | A PNG named `.jpg`, and a JPEG named `.png` | Read by content, not name; the result's extension is valid for what was written. |
| 11 | JPEG with an EXIF rotation | The EXIF tag is dropped. The pixels come out as the game would have drawn them (games ignore EXIF), so it looks the same in game. Note it if it doesn't. |
| 12 | `album.png` and `album.jpg` both in one chart | The PNG's rename is skipped (the name is taken), the JPG is recompressed in place. |
| 13 | Remove option on a strategy | The image is deleted and nothing is encoded. The other strategy is unaffected. |
| 14 | Each strategy switched off alone | Only the other one runs. |
| 15 | A transparent PNG and an opaque PNG in different charts, album art switched on | Each chart's breakdown shows its own image: the opaque one recompressed with its size before and after, the transparent one kept or recompressed as PNG. |
| 16 | An oversized image whose resized result would be *larger* (a small, tightly compressed PNG or JPEG over the limit, such as 600x600 palette art) | Reported as **would grow**, not already optimal, and left as it is. The original is never taken as already within the limits. |
| 17 | An opaque PNG saved with an alpha channel (RGBA, every pixel 255) | Still becomes a JPEG: the alpha is found to be unused. A PNG with a real see-through pixel stays PNG. |
| 18 | `song.ini` with `name = album.png` (or another non-image key naming a renamed file) next to `cover = album.png` | Only `cover` and `background` are rewritten; `name` and the rest keep the old text. |
| 19 | A folder whose `song.ini` has no `[song]` section, with a `.png` that would become a `.jpg` | The image is left as it is and reported as failed (its metadata can't be pointed at the new name); the chart's other files still run. |
| 20 | Plan, then apply, a library of images | Applying writes what the estimate encoded rather than encoding a second time (watch the CPU; it should be about half of before). Edit an image between plan and apply: that one is encoded afresh. |
| 21 | A CgBI PNG (from an iOS app bundle) | Grouped as `image/png` in estimates; skipped as an estimate failure, since Skia can't decode it. |

After every run: no `*.chartpacker-tmp` files or folders anywhere in the library or output folder,
including after cancelling mid-run.
