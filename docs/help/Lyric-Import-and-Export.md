# Lyric Import and Export

Brings lyrics into a vocal part from a file, with their pitches when the file has them, or writes a vocal part's lyrics out as an UltraStar `.txt` song or an `.lrc` file.

**Open it from:** **File → Import Lyrics…** (pick a file first) or **File → Export Lyrics…**.

## Importing

Each line of the file becomes a phrase. The whole import is one undoable step.

A word with a **pitch** in the file becomes a sung note at that pitch. A word with no pitch becomes a **spoken** note: a note with no pitch, marked `#` in its lyric, which you can then turn into a sung note in the vocal lane.

| File | Word timing | Pitches |
| --- | --- | --- |
| `.lrc` | Lines are timed. With the **A2** extension (`<mm:ss.xx>` before each word) each word is timed too. | None |
| `.srt`, `.vtt` | Each cue is a line; its words share the cue's time. | None |
| `.ttml`, `.dfxp` | Each paragraph is a line; timed spans are timed words. | None |
| `.kar` | A MIDI karaoke file: each syllable is timed. | From the melody, when one is found (below) |
| UltraStar `.txt` | Each syllable is timed from the file's `#BPM` and `#GAP`. | Normal and golden notes; freestyle and rap notes are spoken |
| `.mp3`, `.ogg`, `.opus`, `.flac`, `.m4a`, `.wav` | The lyrics in the file's tags: timed (SYLT) lyrics, or `LYRICS` text that is LRC. Lyrics with no times can't be placed and are refused. | None |

A word with no time of its own shares the time before the next timed word, or the end of its line, with the words beside it, by how many letters each has. A line with no end ends where the next begins, but never more than a second, and about a seventh of a second a letter, after it starts, so a gap in the singing isn't filled by the last word.

### Pitches and Slides

- An UltraStar pitch counts semitones from middle C, so pitch `0` is MIDI note 60.
- An UltraStar `~` syllable carries the syllable before it on. At another pitch it becomes a **slide**: a note with the lyric `+`. At the same pitch it only holds the syllable longer.
- The part's pitches are moved by whole octaves into the range the games sing in, 36 to 84. First the whole part moves by the octaves that put the most notes in range, then any note still out of range moves on its own. Most files need no move.
- A karaoke file doesn't say which notes are sung, so Chart Builder looks for the melody: the track, drums aside, with the most notes starting on a syllable. The melody is used only when it has a note on at least half of the syllables. Then each syllable with a note is sung at its pitch and held to that note's end. Melody notes that follow straight on before the next syllable carry the syllable on, as slides. A syllable with no note is spoken. If the melody guessed is wrong, untick **Use Pitches from the File**.

### Duets

An UltraStar duet has two singers, `P1` and `P2`. Lines marked `P3`, which some older duets use for both singers, go to both. When the file is a duet, the **Vocal Part** list starts on **Harmony 1 + 2 (Duet)**: the first singer goes into Harmony 1 and the second into Harmony 2, in one undo step. Choose a single part instead to import only the first singer.

### Options

- **Vocal Part**: Vocals, one of the harmonies, or a duet's two. A part the chart doesn't have yet is added, in the same undo step.
- **Replace Existing Lyrics**: the part's notes, lyrics and phrases are removed first. Its star power is kept.
- **Merge with Existing Lyrics**: the part keeps what it has. A word that would start on a note the part has is left out, a word that would run into one is shortened to stop there, and a phrase that would overlap one the part has is not added.
- **Use Word Timing from the File**: shown when the file has word times. Untick it to time every word from its line instead. For an LRC with A2 this ignores the A2 tags. The pitches go too, so every word is spoken.
- **Use Pitches from the File**: shown when the file has pitches. Untick it to make every word spoken. A slide then only holds the word before it longer.

The file's times are the audio's, so the song's delay is taken off them. A word before the start of the song is moved to the start; one that ends before it is left out. The message bar says how many words were added, and how many were left out.

## Exporting

Choose the **Format** first. **UltraStar (.txt)** is the default.

### UltraStar

Writes an UltraStar song (format 1.1.0) that UltraStar Deluxe, UltraStar Play and other karaoke players read:

- The header has the song's name, artist, charter, genre and year from its `song.ini`, and its audio file as both `#AUDIO` and `#MP3`, for older players. The `#VOCALS` header is the vocals stem, when the song has one. **Save the `.txt` in the song's folder, beside its audio**, so the player finds it.
- UltraStar puts every note's start and end on a whole beat, so `#BPM` is chosen to keep each one within **10 ms** of where it is in the chart. Chart Builder takes the song's average tempo from the first note to the last, so a tempo change in the silence before the singing doesn't count. It then tries 1, 2, 3, … times that tempo and writes the first that fits. A song whose notes sit on its grid fits at a small multiple; triplets fit at three times. Notes that are off the grid, or after tempo changes, need a beat short enough that the nearest one is within the limit, which takes `#BPM` to around 750–950. `#GAP` is the first note's time, delay included. Each note is timed from the audio, so tempo changes don't add up to a drift.
- Each note is written at its pitch. A spoken note (`#` or `^`) is a rap note, `R`, which the games score without its pitch. A slide (`+`), or a note with no lyric, is written as `~`.
- Each phrase is a line. The chart's symbols (`#`, `^`, `-`, `=` and the rest) are taken off the syllables, and a syllable that ends a word is followed by a space.
- Of two notes on the same tick only the lowest is written, and a note less than a beat after the one before is moved to the next beat.

When Harmony 1 and Harmony 2 both have lyrics, the **Vocal Part** list also has **Harmony 1 + 2 (Duet)**. It writes the two as an UltraStar duet, Harmony 1 as `P1` and Harmony 2 as `P2`.

### LRC

Writes one line for each phrase of the chosen part, stamped with the time of its first lyric. The chart's symbols are taken off, and a slide's `+` is dropped. LRC has no pitches and no duets.

- **Include Word Timing (A2)**: puts a `<mm:ss.xx>` tag before each syllable, and one at the end of each line. Untick it for plain LRC, which every player reads.

Times in both formats include the song's delay, so they match the audio file.

[Back to Home](Home.md)
