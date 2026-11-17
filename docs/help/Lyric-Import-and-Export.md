# Lyric Import and Export

Brings lyrics into a vocal part from a file, or writes a vocal part's lyrics out as an `.lrc` file.

**Open it from:** **File → Import Lyrics…** (pick a file first) or **File → Export Lyrics…**.

## Importing

Every imported word becomes a **spoken** note: a note with no pitch, marked `#` in its lyric, which you can then turn into a sung note in the vocal lane. Each line of the file becomes a phrase. The whole import is one undoable step.

| File | Word timing |
| --- | --- |
| `.lrc` | Lines are timed. With the **A2** extension (`<mm:ss.xx>` before each word) each word is timed too. |
| `.srt`, `.vtt` | Each cue is a line; its words share the cue's time. |
| `.ttml`, `.dfxp` | Each paragraph is a line; timed spans are timed words. |
| `.kar` | A MIDI karaoke file: each syllable is timed. |
| UltraStar `.txt` | Each syllable is timed from the file's `#BPM` and `#GAP`. Only the first singer of a duet is read. |
| `.mp3`, `.ogg`, `.opus`, `.flac`, `.m4a`, `.wav` | The lyrics in the file's tags: timed (SYLT) lyrics, or `LYRICS` text that is LRC. Lyrics with no times can't be placed and are refused. |

A word with no time of its own shares the time before the next timed word, or the end of its line, with the words beside it, by how many letters each has. A line with no end ends where the next begins, but never more than a second, and about a seventh of a second a letter, after it starts, so a gap in the singing isn't filled by the last word.

### Options

- **Vocal part**: Vocals or one of the harmonies. A part the chart doesn't have yet is added, in the same undo step.
- **Replace Existing Lyrics**: the part's notes, lyrics and phrases are removed first. Its star power is kept.
- **Merge with Existing Lyrics**: the part keeps what it has. A word that would start on a note the part has is left out, a word that would run into one is shortened to stop there, and a phrase that would overlap one the part has is not added.
- **Use Word Timing from the File**: shown when the file has word times. Untick it to time every word from its line instead. For an LRC with A2 this ignores the A2 tags.

The file's times are the audio's, so the song's delay is taken off them. A word before the start of the song is moved to the start; one that ends before it is left out. The message bar says how many words were added, and how many were left out.

## Exporting

Writes one line for each phrase of the chosen part, stamped with the time of its first lyric. The chart's symbols (`#`, `^`, `-`, `=` and the rest) are taken off, and a slide's `+` is dropped.

- **Include Word Timing (A2)**: puts a `<mm:ss.xx>` tag before each syllable, and one at the end of each line. Untick it for plain LRC, which every player reads.

Times include the song's delay, so they match the audio file.

[Back to Home](Home.md)
