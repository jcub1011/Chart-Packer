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
