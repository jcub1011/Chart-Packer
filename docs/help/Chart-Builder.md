# Chart Builder (the editor)

Chart Builder's one main window, where charts are made. It creates charts from scratch or edits existing ones, and saves them as `notes.mid` plus `song.ini`, ready for YARG and Clone Hero. When a chart is done, it can pack it into a `.sng`.

It opens on an empty "Untitled Chart". Opening a song or starting a new chart swaps what the window shows. Closing it closes the app.

## Parts of the window

- **Title bar:** the **File**, **Edit** and **View** menus, the song's name (click it to open the song's folder) and the version.
- **Toolbar:** the tools (Select, Place, Erase, Phrase, Tempo, Sync, Loop), the tool's options, snap and grid, and Undo and Redo.
- **Left panel:** Chart Settings (parts, metadata, tracks), Art, Sections and Tempo Map.
- **Lanes** (centre): each is one part at one difficulty, as a note highway, or a piano roll for vocals. Each lane has its own menu for tasks such as [Reduce](Reduce.md) and [Generate Phrases](Generate-Phrases.md).
- **Right panel:** Properties of what's selected, Findings (see [Chart Issues](Chart-Issues.md)), Pending Changes, [Preview](Preview.md) and [Difficulty](Difficulty.md).
- **Transport bar and timeline** (bottom): play, record, playtest, speed, loop, metronome, and the whole-song timeline.
- **Message bar:** the latest message. Click it to open [Messages](Messages.md).

## Menus

- **File:** new, open, open recent, save, pack, export a copy, and [Settings](Settings.md).
- **Edit:** undo and redo, clipboard, select all, quantize, go to measure, find matching passages, and [Keyboard Shortcuts](Keyboard-Shortcuts.md).
- **View:** [Messages](Messages.md), the [Preview](Preview.md) window and the [Difficulty](Difficulty.md) window.

## Typical workflow

1. Start a new chart (or drop audio onto the window), or open a song.
2. In Chart Settings, tick the parts, fill in the metadata, and add tracks.
3. Build the tempo map, either from the audio or by hand.
4. Add sections and events.
5. Chart notes with the tools or by recording, then add phrases.
6. Make lower difficulties with Reduce.
7. Play it, playtest it, and clear the Findings list.
8. Save (Ctrl+S), then **Pack…** to export a `.sng`.

Drag-and-drop works too. Dropping a folder, a `.sng`, a `song.ini` or a chart file opens that song, and dropping audio starts a new chart.

*Details and screenshots are coming.*

[Back to Home](Home.md)
