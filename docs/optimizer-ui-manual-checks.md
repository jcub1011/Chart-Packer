# Optimizer UI: manual checks

The optimizer's decisions and the settings file are covered by the tests in
`ChartShared.Core.Tests` (`Optimization/` and `Settings/OptimizeSettingsFileTests`). The Optimize
window itself, its menu entries and what it does to real files on screen are checked by hand in
Chart Packer. The per-backend checks are in `optimizer-image-manual-checks.md` and
`optimizer-video-manual-checks.md`.

Defaults: every strategy off, Opus 128 kbps, video Full HD at CQ 32 with compatibility conversion
off, JPEG quality 80 (album art inside 512x512, backgrounds inside 1920x1080), margin 10%, estimate
from a random sample. Use throwaway copies of charts, not the library.

Setup: a small library folder with a mix of song folders and `.sng` files, including a chart with
several stems, one with a VP8 video, one with a VP9 (or AV1) video, one with PNG album art and a
background image, and one with unsaved metadata changes open in Chart Packer.

| # | Do | Expect |
| - | -- | ------ |
| 1 | Open the Tools menu | **Optimize Library…** is listed after Deduplicate and is disabled until a library has been scanned. It has a tooltip. |
| 2 | Select three songs, right-click | **Optimize…** is listed in the context menu. It opens the window on those three. |
| 3 | Select nothing, Tools → Optimize Library… | The library is scanned again (with progress), and the window works on every song. |
| 4 | With songs selected, switch Charts between Selected and Whole library | The library is scanned again for Whole library, and the count shown matches. Switching back shows the selected songs. |
| 5 | Open the window with a strategy's backend missing (rename the libvpx marker or use a build without it) | Video is shown disabled with the reason, cannot be ticked, and stays off even if the saved defaults had it on. |
| 6 | Turn on Compress audio to Opus, Estimate, with Estimate from set to One chart, then A sample, then All charts | Each lists audio codecs with files, saving across the library and per chart. One chart picks the chart from a list. A sample gives the same numbers every time. Nothing on disk changes. |
| 7 | Turn on Recompress album art | One chart disappears from Estimate from, and if it was chosen the choice moves to A sample. Turning art off again brings it back. |
| 8 | Estimate Recompress background video with the VP9 chart, compatibility off | VP9 is listed with a "compatibility" tag and what converting would cost, in the warning colour if it grows. The total leaves it out. A run leaves that video alone. |
| 9 | Same, with Convert unsupported codecs for compatibility on | The total includes it. A run converts the VP9 video to VP8 WebM even if the file gets larger. |
| 10 | Video at Full HD, then Ultra HD, then change the CQ | The estimate changes with each: Ultra HD keeps more of a large video, a lower CQ is larger. |
| 11 | Audio: the 128 kbps default, then the 80 kbps "smaller" preset | 80 kbps estimates smaller. Both are labelled in the list. |
| 12 | Optimize in place with the output folder off | A confirmation names the number of charts and says it can't be undone. Cancelling it changes nothing. |
| 13 | Confirm; run in place on a song folder and a `.sng` | Progress shows in the title bar and the footer. Each chart is listed with what it saved, and the summary counts optimized, skipped and failed. Both charts are smaller and still open and play in the game. The library list shows the new sizes afterwards. |
| 14 | Tick Write changed charts to a folder, Browse for one, Optimize | There is no in-place warning and no confirmation. Optimize stays disabled until a folder is chosen. The library is unchanged, and only the charts that changed are in the folder, in the same layout. |
| 15 | Run again into the same output folder | Charts already there are skipped as already in the output folder. |
| 16 | Start a run on several large videos and press Stop | The run stops. Charts finished keep their changes, the one in progress is as it was, and the footer says Stopped with a summary. No `*.chartpacker-tmp` is left. |
| 17 | Try to close the window mid-run (the close button, Escape, the X) | The run is stopped and the window stays open until it has; then Close works. |
| 18 | Run on a chart that has unsaved metadata changes open in the main window | It is skipped, with the reason in the results. |
| 19 | Run on a chart open in Chart Builder | It is skipped as in use elsewhere. |
| 20 | Run with only metadata cleanup on | No estimate is available (the Estimate button is disabled), and Optimize works. |
| 21 | Turn on a strategy, change its settings, Save as defaults, close and reopen the window | The window starts with those settings. |
| 22 | Restart Chart Packer and open the window | The saved settings are still there. |
| 23 | Change some settings, Reset | Every setting is back to the shipped one. Close without saving, reopen: the saved defaults are as they were. |
| 24 | Delete `optimizer-settings.json`, or fill it with garbage, and open the window | It opens with the shipped defaults and doesn't crash. |
| 25 | Open the window and press F1, or the **?** button | The Optimize Library help page opens. |
| 26 | Resize the window narrower | Nothing is cut off; the settings scroll. |
| 27 | Run in place on the whole library, then Estimate again without closing | The library is scanned again after the run, so the new estimate sees the optimized charts. |

After every run: no `*.chartpacker-tmp` files or folders anywhere in the library or output folder,
including after stopping mid-run, and the main window's inspector shows the song as it now is.
