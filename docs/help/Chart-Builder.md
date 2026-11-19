# Chart Builder

Chart Builder is where charts are made and edited.

Chart Builder saves charts as `notes.mid` and `song.ini`. A chart in the `.chart` format is converted to `notes.mid` when it is saved. **Note:** this conversion can be lossy, so back up the original `.chart`. When a chart is done, you can pack it into a `.sng` file.

## How Do I Create A New Chart?

Making a new chart is simple. Open Chart Builder and add a song file through the **Chart Settings** side menu. By default, the **Tracks** section is at the bottom. Supported audio file types are `.opus`, `.ogg`, `.mp3` and `.wav`.

### Track Structure

If you only have a single audio file for the whole song, add the track as "song". If you have the audio stems (like guitar, drums, vocal, etc.), add each track under the corresponding track name. When you add a track this way, the source file name doesn't matter: Chart Builder saves the file under the correct name without modifying the source file. Audio dropped onto the window is different, because its file name must say which stem it is (and no other file can already be that stem).

## Lining Up A Background Video

In the **Art** tab, set the video's **Video start** box to choose where the song starts in the video. Positive values skip that much of the video; negative values hold its first frame.

The stopwatch button beside the box, **Auto Align by Waveform**, lines the video up using its own audio track and the song's mix. It's disabled when the video has no audio. Under the row, the chart waveform is drawn above the video waveform: drag either to adjust the start by eye. Both the button and a drag are undoable.

## Importing And Exporting Lyrics

**File → Import Lyrics…** brings lyrics into a vocal part from an `.lrc` file (with or without the A2 word timing), or from `.srt`, `.vtt`, `.ttml`, `.kar`, an UltraStar `.txt`, or the lyrics tags of an audio file. Words from an UltraStar song, or a karaoke file with a melody, are sung at their pitches, and an UltraStar duet goes into Harmony 1 and 2; every other word is spoken. You choose whether the part's own lyrics are replaced or merged with, and the import is undoable. **File → Export Lyrics…** writes a part, or Harmony 1 and 2 as a duet, as an UltraStar `.txt` with its pitches, or a part as an `.lrc`, with or without A2 word timing. Lyrics timed to a different recording can be lined up with the **Alignment** tool (Alt+7), which shifts and stretches every vocal part to fit. See [Lyric Import and Export](Lyric-Import-and-Export.md).

## Lining Up A Part With The Audio

A chart or lyrics timed to a different recording of the song start late or early, and drift if that recording plays faster or slower. The **Alignment** tool (the toolbar's double-arrow button, or **Alt+7**) shifts and stretches the part in focus to fit, in the audio's time, so tempo changes don't bend it. Click the lane first:

- **On a vocal lane** it moves every vocal part's notes, lyrics, phrases and star power. Turn off **All Vocal Parts** to move only the part in focus.
- **On an instrument's lane** it moves every difficulty that has notes, with their HOPO, strum and tap markings and disco flips, and the star power, solos, drum fills, rolls, trills and tom markers they share. Turn off **All Difficulties** to move only the difficulty in focus; the shared phrases then stay where they are.

Drag inside the shaded span to shift, or drag the handle before the first note or after the last to stretch with the other end kept. Hold **Shift** for fine dragging, or type an **Offset (ms)** and **Speed (%)** (50–200%) in the toolbar. Each change is one undo step. If speeding up puts two notes on the same place, only one is kept, and the status bar says so. See [Lining Up Imported Lyrics](Lyric-Import-and-Export.md#lining-up-imported-lyrics) for a walkthrough.

## Saving And Loading Lane Layouts

The **View** menu keeps named lane layouts. **Load Layout** puts a layout's lanes in place of those open (asking first if they aren't saved as a layout), and **Save Layout** saves the lanes open over an existing layout (after asking) or as a **New Layout…**. Changing the lanes never changes a layout until you save over it. **Manage Layouts…** renames, reorders, overwrites and deletes them. Chart Builder starts with **Default**: Expert Guitar and Expert Drums side by side. See [Manage Layouts](Manage-Layouts.md).

## Typical Workflow

1. Start a new chart (or drop audio onto the window), or open a song.
2. In Chart Settings, tick the parts, fill in the metadata, and add tracks.
3. Build the tempo map, either from the audio or by hand.
4. Add sections and events.
5. Chart notes with the tools or by recording, then add phrases.
6. Make lower difficulties with Reduce.
7. Play it, playtest it, and clear the Findings list.
8. Save (Ctrl+S), then **Pack…** to export a `.sng`.

[Back to Home](Home.md)
