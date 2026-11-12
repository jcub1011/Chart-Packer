# Optimizer UI: manual checks

The optimizer's decisions, its estimates and the settings file are covered by the tests in
`ChartShared.Core.Tests` (`Optimization/` and `Settings/OptimizeSettingsFileTests`). The Optimize
window itself, its menu entries and what it does to real files on screen are checked by hand in
Chart Packer. The per-backend checks are in `optimizer-image-manual-checks.md` and
`optimizer-video-manual-checks.md`.

Defaults: every strategy off, Opus 128 kbps, video Full HD at CQ 32 with compatibility conversion
off, JPEG quality 80 (album art inside 512x512, backgrounds inside 1920x1080), margin 10%, writing to
a separate folder. Use throwaway copies of charts, not the library.

Setup: a library folder with a mix of song folders and `.sng` files, including a chart with several
stems, one with MP3 stems, one with Opus stems, one with a VP8 video, one with a VP9 (or AV1) video,
one with PNG album art and a background image, and one with unsaved metadata changes open in Chart
Packer. A few dozen charts, so that measuring takes long enough to watch.

## Opening

| # | Do | Expect |
| - | -- | ------ |
| 1 | Open the Tools menu | **Optimize Library…** is listed after Deduplicate and is disabled until a library has been scanned. It has a tooltip. |
| 2 | Select three songs, right-click | **Optimize…** is listed in the context menu. It opens the window on those three, with **Selected charts (3)** chosen under Charts. |
| 3 | Select nothing, Tools → Optimize Library… | The window lists every song. The main window's list is not scanned again, and is as it was when the window closes. |
| 4 | With songs selected, switch Charts between Selected and Whole library | The list and the counts follow. The main window's selection is still there afterwards. |
| 5 | Open the window while a song is selected in the main window | The window has the main window's colours for that song. |
| 6 | Open the window and press F1, or the **?** button | The Optimize Library help page opens. |

## Measuring

| # | Do | Expect |
| - | -- | ------ |
| 7 | Open the window with nothing switched on | Every chart is listed with its size and no saving. The summary says to switch something on, and so does the footer beside a disabled Optimize. |
| 8 | Switch on Compress audio to Opus | The title bar fills and the footer counts charts measured. Within a few seconds every chart has a figure: measured ones plain, the rest starting with **~** and a fainter bar. The summary and the figure beside the strategy start with the total. |
| 9 | Watch it finish | Each **~** figure is replaced by a measured one. The summary ends on "Every chart is measured". The total doesn't jump far from its first value. |
| 10 | Close the window and open it again | The figures are there almost at once, without measuring again. |
| 11 | While it measures, select a chart that still has a **~** | It's measured next, and its files are listed under it. |
| 12 | Select a measured chart | Its files are listed: each stem "To Opus" with size before and after, files that are kept with why (saves too little, would get larger, already small enough). The file savings add up to the chart's. Clicking it again closes the list. |
| 12a | Select a measured chart whose album art or background image is recompressed | Under its files, each such image is shown twice: as it is and as it would be, with the file size and picture size under each. A click on the pictures doesn't close the row. |
| 12b | Select a chart whose video is recompressed (raise the CQ if none is) | A frame of the video appears before and after within a second or two, with "Frame 1 of 5" and its time. The arrows step through five frames; stepping back to one already seen is instant. |
| 12c | Change the CQ or the JPEG quality, and select the chart again once it's measured | The "after" pictures are made with the new setting. |
| 12d | Look down the list while it measures and after | Every chart has a line under its name, "Nothing done" where nothing applies, so rows are all one height and the list doesn't shift as charts are measured. |
| 13 | Check the chart with Opus stems, and the one with MP3 stems | Opus is "Nothing to shrink", with its stems kept as already small enough. MP3 saves less than Vorbis, or is kept as saving too little. |
| 14 | Press **Pause measuring**, then **Resume measuring** | The count stops, the spinner goes, and the processor is idle; then it carries on from where it was. |
| 15 | Change the audio bitrate from 128 to 80 | Measuring starts again and the savings are larger. Change it back: the figures return at once. |
| 16 | Drag **Skip small savings** up and down | The figures change without the processor working: charts under the margin become "Nothing to shrink". |
| 17 | Switch on video with the VP9 chart, compatibility off | That video is kept, "converting its codec is switched off", and saves nothing. With **Convert unsupported codecs** on it's measured, and if it grows the chart's figure is a **+**. |
| 18 | Switch on Mix stems down to one together with audio | A chart with several stems shows "Stems mixed down to one", and its stems aren't also counted under Compress audio. A chart with one stem is transcoded as before. |
| 19 | Switch on only Clean up metadata | Charts with something to clean say "No smaller" with "Metadata cleaned up" under the name. Optimize works. |
| 20 | Open the window with a strategy's backend missing (use a build without libvpx) | Video is shown disabled with the reason, cannot be ticked, and stays off even if it was on when the window was last used. |

## The list

| # | Do | Expect |
| - | -- | ------ |
| 21 | Type part of a song's name in the search box, then press Escape in it | Only matching charts are listed; Escape clears it. |
| 22 | Sort by Size, Name, Problems first, then Saving | The order follows. Sorting by saving puts the largest first. |
| 23 | Turn on the **Shrinks** chip, then **Nothing to shrink**, then both | Only those charts are listed; the counts on the chips add up to the number of charts. |
| 24 | Search for something that matches nothing | "No charts match", with a button that clears the search and the chips. |
| 25 | Scroll a library of a few thousand charts while it measures | Scrolling stays smooth, and the list doesn't reorder itself under the pointer more than once or twice. |

## Running

| # | Do | Expect |
| - | -- | ------ |
| 26 | With **A separate folder** chosen and no folder | Optimize is disabled, and the footer says to choose a folder. Browse for one: the button names the number of charts that will change. |
| 27 | Optimize into the folder | There is no confirmation. The settings grey out. Each chart in turn shows the file being worked on and a bar filling, then "Saved" with its size and "Estimated" under its name. The summary counts as it goes. |
| 28 | When it's done | The library is unchanged, and only the charts that changed are in the folder, in the same layout. **Open output folder** opens it. Selecting a chart lists each file's size before and after with the estimate. The saved sizes are close to the estimates. |
| 29 | Run again into the same output folder (change a setting and back first) | Charts already there are left alone as already in the output folder, and the **Problems** chip counts them. |
| 30 | Choose **Over the originals** | An amber warning appears under it. Optimize asks to confirm, naming the number of charts and saying it can't be undone. Cancelling changes nothing. |
| 31 | Confirm; run in place on a song folder and a `.sng` | Both charts are smaller and still open and play in the game. The library list shows the new sizes after the window closes. |
| 32 | Press Optimize while charts are still being measured | The button greys out and says "Busy…", Stop appears beside it, and the footer says "Getting ready…" at once. Clicking the greyed button does nothing. When the run ends it says Optimize again. The measured charts are optimized first, largest saving first, then the footer says charts are being measured before they're optimized, and those rows say "Measuring…". |
| 33 | Start a run on several large videos and press Stop, or the spinner in the title bar | The run stops. Charts finished keep their changes, the one in progress is as it was, and the summary starts with "Stopped". No `*.chartpacker-tmp` is left. |
| 34 | Try to close the window mid-run (the X, Escape) | The run is stopped and the window closes once it has. |
| 35 | Close the window while it's measuring | It closes at once. |
| 36 | Run on a chart that has unsaved metadata changes open in the main window | It is left alone, with the reason under its name. |
| 37 | Run on a chart open in Chart Builder | It is left alone as in use elsewhere. |
| 38 | After an in-place run, change a setting | The charts that were rewritten are read again, and their figures are for the files as they now are. |

## Settings

| # | Do | Expect |
| - | -- | ------ |
| 39 | Switch strategies on, change their settings, choose an output folder, close and reopen the window | Everything is as it was left. |
| 40 | Restart Chart Packer and open the window | The same. |
| 41 | Press Reset | Every setting is back to the shipped one; where the charts are written is unchanged. |
| 42 | Delete `optimizer-settings.json` and `optimizer-estimates.json`, or fill them with garbage, and open the window | It opens with the shipped settings, measures from scratch, and doesn't crash. |
| 43 | Resize the window to its smallest | Nothing is cut off; the settings scroll, and chart names shorten with an ellipsis. |

After every run: no `*.chartpacker-tmp` files or folders anywhere in the library or output folder,
including after stopping mid-run, and the main window's inspector shows the song as it now is.
