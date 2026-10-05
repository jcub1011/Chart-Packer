# Chart-Packer Charter — Specification v1

| | |
| --- | --- |
| **Status** | Draft |
| **Scope** | Charter v1: creating, editing and exporting charts inside Chart-Packer |
| **Companion docs** | [project-spec-v1.md](project-spec-v1.md) (the app this builds on) and [sng-spec.md](sng-spec.md) (the container charts are packed into) |
| **Output format** | `notes.mid`, in the Rock Band / Clone Hero layout that YARG reads ([§7](#midi-layout)) |

---

## 1. Overview and goals

The **Charter** is a chart editor built into Chart-Packer. It creates charts from scratch, edits
the charts in a user's library, and saves them as `notes.mid`, ready for Clone Hero, YARG and other
games that read the format. v1 covers guitar, bass and drums. The charter opens from the library
and saves through the same draft and save path the metadata editor uses
([project-spec F5](project-spec-v1.md#f5-metadata-editor)). A chart can go from an empty folder to
a packed `.sng` without leaving the app.

Most of what the charter needs already exists in some form. Chart-Packer reads `.chart` and
`.mid` as YARG does, writes MIDI, converts `.chart` to MIDI and checks the result, plays stems
sample-aligned at any speed with pitch correction, draws waveforms and a spectrum, and draws a
read-only note highway. The charter adds an **editable** model on top of these, plus the tools
that make charting fast.

Goals:

1. **Correct.** A chart plays the same in Clone Hero and YARG as it looks in the editor. Every save
   is read back with the app's own YARG-compatible reader and compared against the model.
2. **Fast to chart.** Keyboard first. Common work (placing notes, chords, sustains, changing the
   snap, moving around the song) never needs the mouse.
3. **Audio-aware.** Waveforms, a spectrogram, onset markers and beat detection help place notes
   and build the tempo map. They suggest; the charter decides.
4. **Integrated.** Library → new chart or edit → save → pack, all in one place, with the same
   drafts, findings panel and atomic writes as the rest of the app.
5. **Lightweight.** Native AOT, the existing dependency budget, and **no new packages**.

This spec **supersedes** the "editing charts" non-goal in
[project-spec §2](project-spec-v1.md#2-non-goals-for-v1), and the "basic chart checks" item in
[project-spec §12](project-spec-v1.md#12-future-after-v1), which [C16](#c16-chart-lint) replaces.

## 2. Non-goals for v1

- **Vocals and harmonies editing.** `PART VOCALS` and `HARM1`–`HARM3` are kept byte for byte on
  save ([§4.2](#lossless-passthrough)). The existing vocals tools (`VocalsEditor`, lyrics lane)
  are unchanged.
- **Pro keys, keys, pro guitar/bass, dance, Elite drums.** Their tracks are kept as they are.
- **Venue, lighting, camera and crowd events.** Kept as they are.
- **Writing `.chart`.** `.chart` is imported only ([C2](#c2-open-and-import)).
- **Auto-charting.** No notes are generated from audio. Onsets and beats are shown as hints, and
  only the user turns them into notes or tempo markers ([C14](#c14-onset-and-beat-assist)).
- **Audio editing or encoding.** Stems are added, replaced and removed as files, as in
  [project-spec F7](project-spec-v1.md#f7-audio-playback), but never cut, mixed down or
  re-encoded.
- **Playing the chart with a controller** (scoring, hit windows). Previewing uses autoplay
  ([C18](#c18-preview)).

## 3. Users and scope

| | |
| --- | --- |
| **Users** | Chart authors, from first-time charters to people moving from Moonscraper or Editor on Fire |
| **Platforms** | As the app: Windows, macOS and Linux, Native AOT ([project-spec §3](project-spec-v1.md#3-target-users-and-platforms)) |

Instruments and variants in v1:

| Family | Parts (MIDI track) | Variants | Lanes |
| ------ | ------------------ | -------- | ----- |
| **5-fret** | Lead (`PART GUITAR`), Bass (`PART BASS`), Rhythm (`PART RHYTHM`), Co-op (`PART GUITAR COOP`) | — | Green, Red, Yellow, Blue, Orange, Open |
| **6-fret (GHL)** | `PART GUITAR GHL`, `PART BASS GHL`, `PART RHYTHM GHL`, `PART GUITAR COOP GHL` | — | White 1–3, Black 1–3, Open |
| **Drums** | `PART DRUMS` | **4-lane**, **Pro** (4-lane with cymbals), **5-lane** | Kick, Red, Yellow, Blue, (Orange), Green, plus 2× kick |

Every part can have Easy, Medium, Hard and Expert. The drum variant is a property of the song,
saved as `pro_drums` and `five_lane_drums` in `song.ini`, as the games read it
(`ChartReadOptions`, `DrumsLayouts`).

---

## 4. Architecture

```txt
Chart-Packer.slnx
├── ChartPacker/                  App: + Charter screen, editor canvases, calibration tool, new-chart wizard
├── ChartPacker.Audio/            + Analysis/ (spectrogram, onsets, tempo estimation), + Synth/ (click, beep, hit sounds)
├── ChartPacker.Core/             + Charting/ (editable model, commands, MIDI chart writer, lint, reductions)
└── ChartPacker.Core.Tests/       + Charting/, Audio/Analysis/ tests
```

Core stays UI-free and Avalonia-free, as now
([project-spec §4.1](project-spec-v1.md#41-chartpackercore)). The charter's model, commands, writer
and lint all live in Core, so a later CLI could use them.

<a id="editable-model"></a>

### 4.1 Editable model (`ChartPacker.Core/Charting/`)

The existing `ChartDocument` (`Charts/ChartModel.cs`) is **immutable and lossy by design**. It
holds what the library, the highway and the merger need: resolved notes, with HOPOs worked out
and forcing folded into a `Hopo` flag. The charter needs what the author wrote, so it gets its
own **mutable** model, kept separate:

| Type | Holds |
| ---- | ----- |
| `EditableChart` | The resolution, the tempo map, global events, the parts, and the passthrough tracks ([§4.2](#lossless-passthrough)) |
| `EditableTempoMap` | Tempo markers and time signatures in ticks, plus **anchors** (a tempo marker pinned to a time in the audio, [C4](#c4-tempo-map-and-grid)). It converts to the existing `TempoMap` for tick ↔ seconds and beat lines, so `TempoMap.SecondsAt`, `TickAt` and `BeatLines` are reused rather than rewritten. |
| `EditablePart` | One instrument (and drum variant): four `EditableTrack`s plus the phrases shared by every difficulty |
| `EditableTrack` | One difficulty's notes, ordered by tick, then lane |
| `EditableNote` | Tick, length, lane, and what the author set: forced HOPO, forced strum, tap, cymbal or tom, accent or ghost, 2× kick |
| `Phrase` | Kind (star power, solo, drum fill / activation, roll, trill, big rock ending), tick and length, and the difficulties it covers |
| `TextEvent` | Sections (`[section …]` / `[prc_…]`), `[music_start]`, `[music_end]`, `[coda]`, `[end]`, drum mixes (`[mix <d> drums<n>]`, disco flip) and free text |

What the model keeps that `ChartDocument` doesn't:

- forced HOPO and forced strum markers, kept apart from natural HOPOs, which are worked out only
  for display, by `NoteBuilder`'s rules;
- solos, drum fills and activation lanes, rolls and trills, and big rock endings as phrases
  (today they are only passed through `ChartToMidi` as pitch constants);
- accents and ghosts, which are note velocities when `[ENABLE_CHART_DYNAMICS]` is set;
- sections and other text events;
- each note's exact sustain length. The sustain cutoff is applied only when the note is displayed.

**Loading.** A `notes.mid` is read into the editable model by a new `MidiChartLoader`, which uses
the existing low-level `MidiFile`/`MidiEvent` reader and the layout in `MidiLayout`, and follows
`MidiChartReader`'s YARG rules for track naming, note pairing and duplicates. A `notes.chart` is
first converted with the existing `ChartToMidi`, so `.chart` import gets the conversion's checks
and `ChartMergeWarning`s for free, and there is one load path.

**Commands and undo.** Every change is an `IChartCommand` with `Apply` and `Revert` (add notes,
delete notes, move, change sustain, toggle flags, add phrase, edit tempo…). A composite command
groups the changes of one gesture, so a drag or a paste undoes in one step. The undo stack is
unlimited in steps but bounded in memory ([§11](#11-non-functional-requirements)).

<a id="lossless-passthrough"></a>

### 4.2 Lossless passthrough

When a chart is loaded, every MIDI track the charter doesn't edit (vocals, harmonies, keys and pro
keys, pro guitar, venue, beat, unknown tracks) is kept as raw bytes. On save they are written back
**byte for byte**, in their original order. The tempo track and `EVENTS` are rebuilt from the
model, and the `BEAT` track is regenerated from the tempo map, because the user may have edited
those. Events in `EVENTS` that the model doesn't understand are kept as free text events.

### 4.3 Writing (`MidiChartWriter`)

`MidiChartWriter` turns an `EditableChart` into a Format 1 SMF using the existing `MidiWriter` and
`MidiMessage`:

- **Division.** The division is the chart's resolution. New charts use 480.
- **Track order.**
  1. The tempo track (named after the song).
  2. Each part track, in `ChartInstrument` order.
  3. `EVENTS`, then `BEAT`.
  4. The passthrough tracks.
- **Layout.** The pitches, sysex and text events in [§7](#midi-layout).
- **Read-back check.** The bytes are read back by `ChartReader` (as YARG reads them) and compared
  with what the model says the games should see, reusing the comparison behind
  `ChartMergeWarning`. A difference blocks the save and is reported with the times it happens at.
  This is the same promise `ChartToMidi` already makes.

### 4.4 Audio analysis (`ChartPacker.Audio/Analysis/`)

| Component | Responsibility |
| --------- | -------------- |
| `SpectrogramBuilder` | STFT over a stem using the existing `Spectrum/Fft` and `Dsp/Windowing`, mapped to log-frequency bins and quantised to bytes. Runs off the UI thread, produces columns in chunks so the view can draw as they arrive, and is **held in memory only** ([C13](#c13-spectrogram)). |
| `OnsetDetector` | Spectral-flux onset detection with an adaptive threshold: one envelope per stem, plus one per band for drums ([C14](#c14-onset-and-beat-assist)) |
| `TempoEstimator` | Estimates the tempo from the onset envelope's autocorrelation, then tracks beats with dynamic programming, giving a proposed list of beat times and a tempo map with anchors |
| `Synth` | Makes the metronome click, the note hit sounds and the calibration beep as PCM, mixed in by `StemMixer` so they share the stems' clock and latency |

Decoding reuses `AudioDecoder`/`IPcmSource` and `StemReader`, and waveforms reuse `StemPeaks` and
`StemPeaksCache` as they are. Playback reuses `StemPlayer`, which already has speed, pitch
correction (`Dsp/TimeStretcher`) and a heard `Position`.

### 4.5 App (`ChartPacker/`)

- **`ChartEditorView` / `ChartEditorViewModel`**, added to the explicit mapping in `ViewLocator`
  (there is no reflection-based lookup).
- **Two custom-drawn canvases over one model**: `HighwayEditorCanvas` (vertical) and
  `TimelineEditorCanvas` (horizontal). They share one tool and hit-testing layer, `EditorTools`
  (place, select, erase, sustain, phrase, tempo), so behaviour is the same in both. Drawing builds
  on `NoteHighwayView`/`HighwayScene` (note shapes, lane colours, beat lines) and
  `LaneWaveformPainter`.
- **`CalibrationWindow`** ([C11](#c11-calibration-tool)) and **`NewChartWizard`**
  ([C1](#c1-new-chart-from-scratch)) are dialogs built with the shared `WindowChrome`.
- Strings go in `Strings.resx`. New icons come from game-icons.net and are credited in
  `THIRD-PARTY-NOTICES.md`. Styling follows the main window and `Styles/Theme.axaml`.

### 4.6 Saving

The charter saves through the existing draft machinery. A save builds a `MetadataDraft` that holds:

- the new `notes.mid` as a file change;
- the removal of an imported `notes.chart`, if there is one ([C2](#c2-open-and-import));
- the `song.ini` key edits ([C17](#c17-save-and-export)).

`DraftWriter` then writes the draft. Song folders and `.sng` files therefore both save atomically,
a `.sng` keeps its mask, and the library, inspector and packer see the result straight away with no
new write path.

### 4.7 Dependencies

**No new packages.** FFT, windowing, decoding, mixing, output, MIDI reading and writing, and the
settings are all in-house or already budgeted
([project-spec §4.3](project-spec-v1.md#43-native-aot-constraints-required)). The AOT constraints
apply unchanged: compiled bindings, source-generated MVVM and JSON, and no reflection.

`THIRD-PARTY-NOTICES.md`'s GuitarGame_ChartFormats entry should be extended to say it is also the
reference for the MIDI layout in [§7](#midi-layout), with no text copied.

---

## 5. Features

Every feature lists numbered acceptance criteria (**AC C*n*.*m***). "Findings" are `Finding`s
shown in the app's findings panel. Chart lint rules are listed in [§6](#lint-rules).

### C1. New chart from scratch

**Input:** nothing, or one or more audio files.
**Output:** a new song folder containing `notes.mid`, `song.ini`, its stems and (optionally) album
art. It is added to the library and opened in the editor.

**Entry points:**

- a **New chart** button on the toolbar;
- dropping audio files on the app, which offers **New chart** alongside its usual choices;
- a **New chart** button in an empty library.

**Behaviour:** a four-step wizard. **Back** and **Next** keep every step's values.

1. **Song.**
   - Fields: name, artist, album, genre, year and charter.
   - They are prefilled from the first audio file's tags where it has them (Core already reads
     audio with ATL), and from the last charter name used.
   - Optional album art, with the same types and limits as
     [project-spec F5](project-spec-v1.md#f5-metadata-editor).
2. **Audio.**
   - Drop or pick audio files (`.ogg`, `.opus`, `.mp3`, `.wav`). Each is assigned a stem from its
     file name using `AudioStems`, e.g. `Drums.ogg` → `drums`, `song.mp3` → `song`, and anything
     unrecognised → `song`.
   - The stem can be changed from a dropdown. Two files can't share a stem.
   - Each file is decoded briefly to check it is what its extension says, as in
     [F7](project-spec-v1.md#f7-audio-playback).
   - **No audio is allowed.** The user enters a length and charts against a silent timeline. The
     song then has finding SNG052 (no audio stem) until a stem is added.
3. **Timing.**
   - Starting BPM (default 120) and time signature (default 4/4).
   - **Detect from audio** runs [C14](#c14-onset-and-beat-assist)'s tempo estimate on the mix and
     fills in the starting BPM, plus a proposed tempo map to review once the editor opens.
   - Resolution is 480 by default and can be changed.
   - Optional audio lead-in, saved as `delay`.
4. **Parts.**
   - Tick the parts to create: 5-fret Lead, Bass, Rhythm and Co-op; GHL Guitar, Bass, Rhythm and
     Co-op; Drums with a choice of 4-lane, Pro or 5-lane.
   - Then tick the difficulties to create (all four by default).
   - Parts can be added later ([C2](#c2-open-and-import)).

**Create:**

- The **location** is a parent folder (by default the last one used, or the library root) plus
  a folder name (by default `<artist> - <name>`, with characters that aren't allowed in a path
  replaced, and the name checked against the `.sng` filename rules).
- An **existing folder is never used or overwritten**. If the name is taken, Create is disabled
  and says why.
- **What is written:**
  - each stem, copied as `<stem>.<ext>` in lowercase;
  - `album.<ext>`, if art was chosen;
  - `notes.mid`, with the tempo map, an empty track for each chosen part, `EVENTS` holding
    `[music_start]` and `[end]`, and `BEAT`;
  - `song.ini`, with a `[song]` section holding the metadata, `diff_<part> = -1` for each part,
    `pro_drums`/`five_lane_drums` as the drum variant needs, `song_length` from the longest stem
    (or the length entered), and `delay` if one was set.
- **How it is written.** Everything is written into a temp folder beside the target, which is then
  renamed into place. A failure or cancel leaves nothing behind.

Acceptance criteria:

- **AC C1.1** A chart created with one stem and no notes validates with no errors
  ([project-spec F3](project-spec-v1.md#f3-validate)), packs to a `.sng`, and reopens in the editor
  with the same tempo map, parts and metadata.
- **AC C1.2** Cancelling the wizard at any step, or failing to copy a stem, leaves no folder or
  temp folder behind.
- **AC C1.3** Create is unavailable while the target folder exists, and an existing folder's
  contents are never touched.
- **AC C1.4** Dropping `Guitar.OGG`, `Drums.ogg` and `mix.mp3` assigns `guitar`, `drums` and
  `song`, and the folder contains `guitar.ogg`, `drums.ogg` and `song.mp3`.
- **AC C1.5** Choosing Pro drums writes `pro_drums = True` and choosing 5-lane writes
  `five_lane_drums = True`. The reopened chart's drums are read with the same layout.

### C2. Open and import

**Input:** one song in the library, a folder or a `.sng`.

**Behaviour:**

1. **Edit chart** in the library's context menu and on the detail panel opens the charter on the
   selected song. The library is replaced by the editor in the main window, and **← Library**
   returns to it. Only one chart is open at a time.
2. **Which chart is opened.** `ChartReader.Pick` chooses: `notes.mid` if there is one, otherwise
   `notes.chart`. A song with neither is offered **New chart in this song**, which runs the
   wizard's Timing and Parts steps.
3. **`.chart` import.**
   - The chart is converted with `ChartToMidi`. Any `ChartMergeWarning`s are shown before the
     editor opens, with the times they happen at, and the user can go ahead or cancel.
   - The `.chart`'s `Offset` becomes `delay`, as the conversion already does.
   - On the first save, `notes.chart` is staged for removal after a confirmation, so the song
     doesn't carry two charts that the games might pick between differently.
4. **Drafts.** A song with unsaved metadata drafts can be opened. The chart is saved into the same
   draft ([§4.6](#46-saving)).
5. **Add part.** A menu adds any v1 part that isn't there yet, with the difficulties chosen. Adding
   drums asks for the variant if the song doesn't set one.
6. **Remove part** removes a part's track, after a confirmation. It can be undone.
7. **Recovery.** If an autosave exists that is newer than the chart on disk
   ([C15](#c15-editing-ergonomics)), the user is offered **Recover** or **Discard**.

Acceptance criteria:

- **AC C2.1** Opening a `notes.mid` and saving without edits writes a file that `ChartReader`
  reads to the same `ChartDocument`. Every passthrough track is byte-identical.
- **AC C2.2** Importing a `notes.chart` and saving produces a `notes.mid` that reads the same as
  the `.chart` did, apart from the `ChartMergeWarning`s shown at import.
- **AC C2.3** Opening a `.sng`, editing a note and saving keeps the `.sng`'s mask and every other
  file's bytes ([project-spec AC5.1](project-spec-v1.md#f5-metadata-editor)).

### C3. Editor layout

**Behaviour:**

1. **Two views of one model, switched with Tab:**
   - **Highway** (vertical). Time runs upwards, as in the games and Moonscraper. The lanes are in
     the middle, with the waveform or spectrogram drawn behind them or beside them.
   - **Timeline** (horizontal), in the style of a DAW. Time runs left to right and each lane is a
     row. A wide waveform or spectrogram strip sits above the lanes. It suits long-range editing
     and lining notes up with the audio.

   The selection, the cursor, zoom (as seconds on screen) and the tool all carry across a switch.
2. **Overview strip** along the edge. It shows the whole song: sections, note density, star power,
   solos, lint marks and the current view, and clicking or dragging it jumps there.
3. **Part and difficulty picker.** Every part with its difficulties. The letters are coloured as
   in the library (green, yellow, red, purple) and grey where the difficulty is empty.
   - Ctrl+1–4 switches difficulty.
   - **Ghost difficulty**: another difficulty of the same part can be shown faintly behind the
     current one, to check reductions against it.
4. **Properties panel** for the selection:
   - **Notes:** tick, time, measure and beat, lane, length, and flags.
   - **Phrases:** start, end and difficulties.
   - **Tempo markers:** BPM, time signature, and whether it's anchored.
   - With several items selected, a field shows `<<multiple>>` when the values differ, and
     editing it sets every item. This is the same model as the metadata editor's bulk editing.
5. **Findings panel**: the [§6](#lint-rules) lint findings, each linked to the place in the chart
   where it happens.
6. **Transport bar**: play and pause, position, speed, pitch correction, loop, metronome, hit
   sounds, stem mute and solo, and the calibration value ([C11](#c11-calibration-tool)).

Acceptance criteria:

- **AC C3.1** Switching views keeps the selection, the cursor time and the visible span.
- **AC C3.2** Every editing tool behaves the same in both views. The tool tests run against the
  shared `EditorTools` layer, not against either canvas.

### C4. Tempo map and grid

**Behaviour:**

1. **Tempo markers.**
   - Placing a marker sets the BPM from that tick on. BPMs can have up to three decimals.
   - **Time signature markers** have a numerator and a power-of-two denominator.
2. **Anchors.**
   - Anchoring a tempo marker locks it to its time in seconds.
   - Changing a BPM before an anchor recalculates the BPM of the segment just before that anchor,
     so the anchor stays where it is in the audio.
   - Dragging an anchored marker in time changes the BPMs on both sides of it. This is how a
     tempo map is fitted to a live recording, beat by beat.
3. **When a tempo changes, notes keep their ticks (the default) or their times.** Keeping ticks
   means the notes move in time with the grid, which is right while the tempo map is still being
   built. Keeping times re-ticks the notes after the change.
4. **Snap.**
   - From 1/1 to 1/192 of a whole note, including triplets (1/12, 1/24, 1/48…), plus a custom
     value.
   - `[` and `]` step through the common values. Snap can be switched off.
   - At resolution 480 every common snap lands on a whole tick. Where a custom snap doesn't, the
     nearest tick is used.
5. **Tap tempo.** Tapping a key in time with the playing song sets the BPM of the segment at the
   cursor from the median interval of the taps.
6. **Set BPM from selection.** Select a span and say how many beats it lasts, and the BPM is
   computed. This is the quickest way to fit one bar to the waveform.
7. **Proposed tempo maps** from [C14](#c14-onset-and-beat-assist) arrive as one undoable command.
8. **Beat lines** come from `TempoMap.BeatLines`. Measure lines are stronger, and measure numbers
   are shown.

Acceptance criteria:

- **AC C4.1** Changing the BPM of the first segment of a map with an anchor at 30.000 s leaves that
  anchor at 30.000 s ± 1 ms.
- **AC C4.2** With *keep times* on, a tempo change leaves every note's time within half a tick of
  where it was.
- **AC C4.3** Tick ↔ seconds round trips agree with the existing `TempoMap` to within half a tick
  for every tempo map in the test fixtures.

### C5. 5-fret notes

**Behaviour:**

1. **Lanes:** green, red, yellow, blue and orange, plus **open**, which is drawn across the whole
   highway.
2. **Chords:** several frets on one tick. An open note can't be in a chord with frets in 5-fret
   (CHR022).
3. **Sustains:**
   - A sustain is drawn by dragging the note's tail, or set with the sustain tool.
   - Sustains snap to the grid.
   - A sustain shorter than the song's sustain cutoff (`sustain_cutoff_threshold`, otherwise a
     third of a beat in MIDI) is drawn as a plain note with a dashed tail, because that's how the
     games will read it.
   - **Extended sustains** (one note held while others are played) and **disjoint chords**
     (notes of one chord with different lengths) are allowed.
4. **Forcing:**
   - **Natural HOPOs** are shown as they will be read, using `NoteBuilder`'s rules and the song's
     HOPO threshold (`hopo_frequency`, `eighthnote_hopo`, `hopofreq`, or the default).
   - **Force HOPO**, **force strum** and **tap** can be set per note, and are drawn as distinct
     markers.
   - A force that changes nothing is flagged (CHR023), with a quick fix that removes it.
5. **Star power** shows on notes as the games colour them. The phrases themselves are edited in
   [C8](#c8-phrases-and-events).

Acceptance criteria:

- **AC C5.1** A note forced HOPO, one forced strum, a tap, an open note and a disjoint chord each
  save and read back with the same type, as `ChartReader` and YARG read them.
- **AC C5.2** The natural HOPOs shown in the editor match the `Hopo` flags `ChartReader` gives
  after saving, for every HOPO-threshold setting.

### C6. Drums: 4-lane, Pro and 5-lane

**Behaviour:**

1. **Lanes:**
   - **4-lane and Pro:** kick, red, yellow, blue and green.
   - **5-lane:** kick, red, yellow, blue, orange and green.
   - The kick is drawn across the highway, as in the games.
2. **2× kick** (Expert+) is a separate kind of kick, on Expert only.
3. **Cymbals and toms** (Pro).
   - Yellow, blue and green notes are cymbals unless a tom marker covers them.
   - The editor works per note: toggling a note between cymbal and tom adds, splits or removes the
     tom markers (pitches 110–112) it needs. The author never manages the markers by hand.
   - Red is always a snare. 5-lane has no markers: yellow and orange are cymbals, and blue and
     green are toms.
4. **Accents and ghosts.**
   - Per note, on any pad.
   - Saved as velocity 127 (accent) or 1 (ghost), with `[ENABLE_CHART_DYNAMICS]` added to the
     track automatically while the track has any.
5. **Drum fills (activation lanes).**
   - A phrase on every difficulty. Its last note is where star power is activated.
   - **Generate fills** proposes fills over the selection or the whole part. The author picks
     the spacing for each run:
     - **at least every** *n* measures (default 4), only once star power has been earned;
     - the **fill length** (default 1 measure);
     - **what a fill must end on** (default a crash cymbal, or any note).

     Where no note fits at a spot, the fill is left out. The proposals are shown as ghosts, and
     the author keeps or drops each one. Fills are never added silently, and the accepted ones
     are added as one undoable command.
6. **Rolls and trills.**
   - Single-lane roll and two-lane trill phrases.
   - They can be limited to the harder difficulties, which is saved as the velocity convention
     `ChartToMidi` already writes.
7. **Disco flip.** `[mix <d> drums<n>d]` sections, which swap red and yellow in the games, are
   edited as a phrase per difficulty. Inside one, notes are drawn on the pads the player hits.
8. **4-lane ↔ 5-lane conversion helper.**
   - Converts a whole drum part using the mapping Clone Hero and YARG apply when they play one
     layout on the other, so the result is what players of the other layout already got.
   - It is one undoable command. Pad clashes it can't settle are listed as findings.
9. **Drum variant.** Switching between 4-lane and Pro is a song setting (`pro_drums`), not a
   conversion: a 4-lane chart's cymbal markers are kept and ignored. Switching to or from 5-lane
   runs the conversion helper.

Acceptance criteria:

- **AC C6.1** A Pro chart with every pad as cymbal and as tom, accents, ghosts, a 2× kick, a fill, a
  roll, a trill and a disco flip saves and reads back with all of them.
- **AC C6.2** Toggling the middle note of three blue cymbals to a tom leaves exactly one tom marker,
  covering only that note.
- **AC C6.3** Converting 5-lane to 4-lane and back on a chart with no orange or green clashes
  gives the original notes back.

### C7. 6-fret (GHL) notes

**Behaviour:**

1. **Lanes:** white 1–3 and black 1–3 (drawn stacked per column, as in the game), plus open.
2. **Chords**, including the "barre" chords where a white and a black share a column.
3. **Sustains, forced HOPO, forced strum and tap**, as for 5-fret ([C5](#c5-5-fret-notes)).
4. Star power, solos and sections work as for 5-fret.

Acceptance criteria:

- **AC C7.1** Every one of the 6 lanes plus open, a barre chord, and each forcing kind save and
  read back unchanged.

### C8. Phrases and events

**Behaviour:**

1. **Star power** phrases. Every difficulty shares them, as MIDI stores them (pitch 116).
2. **Solos** (pitch 103), drawn as a shaded band with a note count.
3. **Sections** in `EVENTS`. They are named freely and shown in the overview strip and the
   practice-section list. Sections can be renamed in a list, and **Insert section** takes the name
   from a menu of common ones (Intro, Verse 1, Chorus 1, Bridge, Solo, Outro…).
4. **Big rock ending** (all five pitches 120–124 on a guitar or bass track) and `[coda]`.
5. **Song events**: `[music_start]`, `[music_end]`, `[end]`. `[end]` is kept after the last note
   and moved on save if notes are added after it (CHR085).
6. **Free text events**, in `EVENTS` or on a part track, for anything else. Events the model
   doesn't know are shown and kept, not deleted.

Acceptance criteria:

- **AC C8.1** Every phrase and event kind round trips. A free text event the model doesn't know
  survives an unrelated edit and a save.

### C9. Difficulty authoring

**Behaviour:**

1. **Copy difficulty** copies one difficulty's notes and difficulty-specific phrases to another,
   replacing it after a confirmation. It can be undone.
2. **Reduce** makes a draft of a lower difficulty from a higher one, as a starting point to edit.
   - The rules are configurable per part, with defaults:

     | Rule | Hard | Medium | Easy |
     | ---- | ---- | ------ | ---- |
     | Max chord size (5-fret / GHL) | 2 | 2 | 1 |
     | Frets used (5-fret) | all | G–B | G–Y |
     | Thinning grid (no closer than) | 1/16 | 1/8 | 1/4 |
     | Forced HOPOs and taps | kept | removed | removed |
     | Sustains shorter than a beat | kept | kept | removed |
     | Drums: pads per hit (plus kick) | 2 | 2 | 1 |
     | Drums: kick | kept | thinned to 1/8 | thinned to 1/4 |
     | Drums: 2× kick | removed | removed | removed |

   - Chord reduction keeps the outermost frets. Frets outside the allowed range are moved
     inward, with their shape kept as far as possible.
   - Thinning keeps notes on strong beats first.
   - The result is shown as a ghost over the target difficulty, then applied as one undoable
     command.
3. **Difficulty letters** in the part picker show which difficulties have notes, as in the
   library.

Acceptance criteria:

- **AC C9.1** Reducing Expert to Easy never makes a chord or a note closer than the Easy thinning
  grid, and is undone in one step.

### C10. Playback

**Behaviour:**

1. Playback uses `StemPlayer`. It plays every stem sample-aligned, and has mute and solo per stem,
   volume, speed from 25% to 200% with **pitch correction** (on by default, as in the inspector),
   and sample-exact seeking.
2. **Play from cursor** (Space), **play selection**, and a **loop region** for repeating a hard
   passage at a lower speed.
3. **Metronome**: a click on each beat, with an accented click on each measure, from the tempo
   map, mixed in by `StemMixer` so it is exactly in time with the stems.
4. **Hit sounds**: a short sound on each note as it reaches the strike line. There is one sound per
   part, and a pad sound for each drum pad. The volume is separate from the stems'.
5. **Latency compensation.** Everything drawn against the audio (the cursor, highway, waveform,
   spectrogram and overview) is drawn at **`StemPlayer.Position` − the calibrated latency**
   ([C11](#c11-calibration-tool)). `Position` already allows for the audio the app has queued; the
   calibration covers the output device's own latency, which the app can't see.
6. **Following.** While playing, the view keeps the cursor at a fixed place, a quarter of the way
   from the strike end. Scrolling pauses following until playback is restarted.

Acceptance criteria:

- **AC C10.1** At 50% speed with pitch correction, metronome clicks stay within 2 ms of the beat
  lines over a 5-minute song.
- **AC C10.2** Changing the calibrated latency moves what is drawn against the audio by exactly
  that amount, and doesn't move the audio.

### C11. Calibration tool

A tool for measuring the audio/visual latency of the user's setup, so that what is seen and what is
heard line up while charting. **The display is the ground truth.** The audio is adjusted (in
practice, everything drawn is shifted against the audio, [C10](#c10-playback)) until it matches
the display.

**Input:** none.
**Output:** a latency in milliseconds for the current audio output device.

**Opening:**

- from **Settings**;
- from the charter's transport bar;
- **automatically, once**, the first time the charter opens on a device that has no calibration.
  This can be skipped.

**Behaviour:**

1. **Manual test.** This is the main test.
   - A loop at 100 BPM. On every beat:
     - a **light flashes**: a large tile goes bright for 60 ms;
     - a **note scrolls down a small highway** and reaches the strike line;
     - a **beep plays**: a 1 kHz tone of 30 ms with 2 ms fades, which has a sharp start.
   - The beep is made by `Synth` and played through the same `StemPlayer` → `StemMixer` →
     `IAudioOutput` path as a chart's stems, so it has exactly their queueing and device latency.
   - The flash and the note are timed from the display. The beep is timed from them, moved by the
     current latency. Changing the latency therefore **moves the beep, never the flash**.
   - The user adjusts the latency until the beep and the flash (or the note reaching the line)
     happen together. Controls:
     - a slider from −500 to +500 ms (Bluetooth output can be well over 200 ms late);
     - buttons for −10, −5, −1, +1, +5 and +10 ms;
     - the arrow keys (Shift for ×5);
     - a number box.

     The change is heard from the next beat.
   - **Why the highway note as well as the light.** A moving object reaching a line is easier to
     judge than two instants. It is also what the user will be judging against while charting.
     The light is kept as the user asked, and either can be hidden.
2. **Tap test (optional).** This gives a measured starting value, which the manual test then fine
   tunes.
   - **Visual phase.** The user taps any key in time with 16 flashes, with no sound.
   - **Audio phase.** The user taps in time with 16 beeps, with the screen blank.
   - The first 4 taps of each phase are warm-up and are ignored.
   - Each tap's error is measured against its nearest beat. Taps more than 1.5 interquartile
     ranges from the median are dropped.
   - **Latency = median audio error − median visual error.** The user's reaction time and the
     keyboard's latency are the same in both phases, so they cancel out. What's left is how much
     later the audio arrives than the display.
   - The result shows the estimate, its spread (the interquartile range), and how many taps were
     kept. **Use this value** puts it into the manual test to be checked. A spread over 30 ms
     suggests trying again.
   - Tap times come from the input events' own timestamps, not from when the UI thread handles
     them.
3. **Per device.**
   - Latency is stored **per audio output device**, because headphones, Bluetooth and speakers
     differ by hundreds of milliseconds.
   - **Device key.** A device is identified by a stable OS device ID where the platform gives one
     (for example, a Windows endpoint ID or a Core Audio device UID), and otherwise by its name.
     SDL's device names may change across reboots or docks, and its device handles only last for
     one session, so the ID is preferred when there is one.
   - `IAudioOutput` gains a `DeviceName` and an optional `DeviceId`, which `SdlAudioOutput` fills
     in. Getting a stable ID may need a small platform call beside SDL. A platform without one
     uses the name alone.
   - **Lookup order:** the stored value for the device's ID, then for its name, then a **global
     fallback**. When a device is found by name, its value is also saved under its ID if it has
     one, so a later rename doesn't lose it.
   - The tool always says which device it is calibrating.
4. **Where it applies.**
   - the charter ([C10](#c10-playback));
   - **record mode**, where key presses are shifted by the latency before they're quantised
     ([C15](#c15-editing-ergonomics));
   - the existing inspector highway (`NoteHighwayView`), lyrics lane and waveform playhead.
5. **Saving.** **Save** stores the value. **Cancel** restores the previous value. **Reset** sets 0.

Acceptance criteria:

- **AC C11.1** With the latency set to +100 ms, a recording of the output shows the beep 100 ms
  ± 2 ms (one audio buffer) earlier relative to the flash schedule than at 0 ms, and the flash
  schedule is unchanged.
- **AC C11.2** The tap-test estimator, given simulated taps with a known audio offset, a ±20 ms
  normal jitter and 2 outliers per phase, recovers the offset to within ±5 ms.
- **AC C11.3** Changing the output device loads that device's stored latency, or the fallback.
- **AC C11.4** Cancel leaves the stored latency as it was before the tool opened.

### C12. Waveform

**Behaviour:**

1. **Which audio.** The waveform is the mix, or any one stem. By default it is the stem that
   matches the part being edited (`LaneStems`, e.g. drums → `drums`/`drums_1`–`drums_4`), and the
   mix when there's no such stem.
2. **Resolution.** It follows the zoom, using `StemPeaks` and `WaveformResolution`, and is
   computed off the UI thread through `StemPeaksCache`, as the inspector does now.
3. **Placement.** Behind the lanes (faint) or beside them (full), in either view. In the Timeline
   view it is also drawn as a full-width strip above the lanes.
4. **Display.** Mono (the stereo channels combined) or stereo (split). The gain can be adjusted for
   quiet stems.

Acceptance criteria:

- **AC C12.1** Zooming and scrolling a 10-minute song never blocks the UI thread for more than
  16 ms.

### C13. Spectrogram

**Behaviour:**

1. **What is drawn.** A short-time Fourier transform of the chosen stem or the mix:
   - a 2048-sample Hann window with a 256-sample hop (about 5.8 ms at 44.1 kHz);
   - mapped to 256 logarithmic frequency bins from 30 Hz to 16 kHz;
   - shown in dB through a perceptually even colour map.

   The contrast (dB floor and ceiling) can be adjusted.
2. **Computed off the UI thread**, at low priority, starting near the view and spreading outwards.
   Columns that aren't ready yet are drawn as blank, and the view fills in as they arrive.
3. **Held in memory only.**
   - Nothing is written to disk, and closing the chart releases it.
   - Reopening a song computes it again.
   - Columns are stored as bytes, about 26 MB per stem for a 10-minute song at the settings above.
   - A **memory budget** (Settings, 256 MB by default) caps every stem's spectrogram together. The
     stems used least recently are released first.
4. **Band presets** emphasise a range and dim the rest, to help pick out drum hits and bass notes:
   - **Kick:** 30–120 Hz
   - **Snare:** 150–300 Hz plus 2–5 kHz
   - **Cymbals:** 6–16 kHz
   - **Bass:** 40–400 Hz
   - **Full**
5. **Placement** is the same as for the waveform: behind the lanes, beside them, or as the strip
   in the Timeline view. The waveform and the spectrogram can be switched with one key.

Acceptance criteria:

- **AC C13.1** Opening a 10-minute song shows the first screen of spectrogram within 1 s on a
  4-core machine, and the UI thread is never blocked for more than 16 ms while the rest computes.
- **AC C13.2** The spectrogram's memory stays within the budget, and nothing is written outside
  the song's own save.

### C14. Onset and beat assist

**Behaviour:**

1. **Onset ticks.**
   - Onsets found by `OnsetDetector` are drawn as small ticks beside the lanes.
   - For guitar and bass they come from the part's stem. For drums they come from the drum stem,
     split by band (kick, snare, cymbals), so each tick sits near the pad it most likely belongs to.
   - A **sensitivity** slider changes the threshold live.
2. **Snap to onset.** When on, placing or moving a note snaps to the nearest onset within half the
   current grid step. Otherwise it snaps to the grid as normal. The grid always wins when no onset
   is in range.
3. **Nudge to onset** moves the selected notes to their nearest onsets, as one undoable command.
   It shows how far each note moves, and notes with no onset nearby stay where they are.
4. **Proposed tempo map.**
   - `TempoEstimator` proposes beat times for the whole song, or for a selection, with a
     confidence.
   - They are shown as a ghost tempo map over the real one.
   - The user can **accept** it (one undoable command that writes tempo markers and anchors every
     *n* measures), **adjust** it (drag a proposed beat; the proposal updates around it), or
     **discard** it.
   - A low-confidence region (rubato, a breakdown) is marked so the author maps it by hand.
5. **First beat.** **Find first downbeat** proposes where measure 1 starts, and sets `delay` or the
   first tempo marker's position. This is often the hardest part of starting a chart.
6. Everything here is a hint. Nothing changes the chart without an explicit action, and every
   action can be undone.

Acceptance criteria:

- **AC C14.1** On synthetic click tracks at constant tempos from 60 to 240 BPM, the proposed tempo
  is within 0.1 BPM, and every proposed beat is within 10 ms of a click.
- **AC C14.2** On a synthetic track whose tempo changes in steps, the accepted map's beats are each
  within 10 ms of the clicks.
- **AC C14.3** With *snap to onset* on, a note placed 15 ms from an onset at a 1/16 grid at
  120 BPM lands on the onset.

### C15. Editing ergonomics

**Behaviour:**

1. **Undo and redo** (Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z) for every change, tempo map included.
2. **Selection**:
   - click, Shift+click, and box select;
   - select a time range across lanes;
   - select a whole lane;
   - select everything in a section.
3. **Clipboard**: cut, copy and paste. Pasting goes at the cursor and keeps relative timing. It
   works across difficulties and parts of the same family (5-fret ↔ 5-fret), and between 5-fret and
   GHL by a lane mapping the user confirms.
4. **Transforms** on the selection:
   - move by one grid step or one lane;
   - **lefty flip** (mirror the lanes);
   - shift all notes up or down one lane;
   - set or clear sustains, or set them to "until the next note";
   - change any flag;
   - stretch or compress the timing between two points (for fixing the double- or half-time
     sections common in imported charts).
5. **Record mode.**
   - While the song plays, number keys place notes on lanes at the moment they're pressed. Holding
     a key holds a sustain.
   - The press times are shifted by the calibrated latency ([C11](#c11-calibration-tool)), then
     quantised to the grid (or to onsets, if snap to onset is on).
   - Each pass is one undoable command. Recording can overwrite the selected range or merge with
     what's there.
   - This works at any speed, so a fast passage can be recorded at 50%.
6. **Autosave and recovery.**
   - Every 2 minutes (configurable) and on each section change, the model is written to a
     recovery file in the per-user config folder (`Recovery/`), never into the song.
   - Saving deletes it. Opening a song with a newer recovery file offers to restore it
     ([C2](#c2-open-and-import)).
7. **Closing** the charter, or the app, with unsaved changes asks: Save, Discard or Cancel.
8. **Keyboard shortcuts** are listed in [§9](#9-keyboard-shortcuts).

Acceptance criteria:

- **AC C15.1** Any sequence of commands followed by the same number of undos gives a model equal to
  the start. Redoing them all gives a model equal to the end.
- **AC C15.2** Killing the app mid-edit and reopening the song offers the recovered chart, which
  includes every change up to the last autosave.
- **AC C15.3** In record mode with a latency of 40 ms, a key pressed when `StemPlayer.Position` is
  40 ms past a beat (that is, exactly on the beat as the display shows it and the user hears it)
  is placed on that beat.

### C16. Chart lint

**Behaviour:**

1. **When it runs.** The rules in [§6](#lint-rules) run on the open chart in the background after
   each change. Only the parts and ranges the change touched are re-checked, so it keeps up while
   typing.
2. **How findings are shown.** Findings use the existing `Finding` type, with rule IDs `CHRnnn`
   and messages in `FindingMessages.resx`. They appear in the findings panel and as marks on the
   overview strip, and each one links to its place in the chart.
3. **Quick fixes**, through the existing `QuickFixRegistry`/`IQuickFixStrategy`. Each fix is one
   undoable command (see the table for which rules have one).
4. **Severity.**
   - Lint **never blocks saving**. A chart in progress must always be saveable.
   - **Errors** (the games would read the chart differently from how the editor shows it) block
     **Save and pack**, the same way SNG errors block packing.
5. **In the library.** Packing and validating a song from the library also run chart lint, so
   charts made outside the charter get the same checks
   ([project-spec F3](project-spec-v1.md#f3-validate)).

Acceptance criteria:

- **AC C16.1** Every CHR rule has a fixture that triggers it, and a clean fixture triggers none.
- **AC C16.2** Lint after a single-note edit on a 20,000-note chart finishes within 50 ms.

### C17. Save and export

**Behaviour:**

1. **Save** (Ctrl+S):
   1. writes `notes.mid` with `MidiChartWriter` ([§4.3](#43-writing-midichartwriter));
   2. reads it back and compares it with the model. **A difference stops the save.**
   3. saves through `MetadataDraft`/`DraftWriter` ([§4.6](#46-saving)).
2. **`song.ini` updates** made by a save. Every one is shown as a change in the metadata editor,
   like any other edit.
   - `song_length`, when it is unset or no longer matches the audio;
   - `pro_drums` and `five_lane_drums`, for the drum variant;
   - `charter`, if empty, from the Settings value;
   - `diff_<part> = -1` for new parts. The suggested rating
     ([C18](#c18-preview)) is offered, but is never written without the user's say-so;
   - `sustain_cutoff_threshold` and the HOPO threshold are never changed by the charter. Its
     display follows them.
3. **Save and pack** saves, then packs the song to a `.sng`
   ([project-spec F1](project-spec-v1.md#f1-pack-song-folder--sng)), using the usual output and
   overwrite settings.
4. **Export copy** writes the song with its draft applied to another folder, using the existing
   `SongExport`.

Acceptance criteria:

- **AC C17.1** A save that is interrupted (killed mid-write) leaves the previous `notes.mid` intact.
- **AC C17.2** For every fixture chart, reading the saved `notes.mid` with `ChartReader` gives the
  notes, flags and star power the editor shows.
- **AC C17.3** Saving never changes a `song.ini` key the user hasn't seen as a pending change.

### C18. Preview

**Behaviour:**

1. **Highway preview.** A game-style view of the part and difficulty, using the existing
   `NoteHighwayView`, played in sync with the audio and with notes "hit" automatically. It can run
   docked beside the editor while editing, or full-screen.
2. **Practice preview**: the preview over the loop region at the chosen speed.
3. **Suggested difficulty.**
   - For each part and difficulty, a suggested `diff_*` tier from 0 to 6.
   - It is based on peak and average notes per second, chord density, and HOPO/tap density
     (and, for drums, independence between hands and kick).
   - It is shown with what it was based on, and is only a suggestion.
   - The weights are fixed in v1 and will be tuned against rated charts.

Acceptance criteria:

- **AC C18.1** Preview follows the latency calibration and stays within one frame of the editor
  view while both are shown.

---

<a id="lint-rules"></a>

## 6. Chart lint rule catalogue

Severity meanings:

- **Error:** the games would read the chart differently from how the editor shows it, or the file
  can't be written faithfully. Blocks **Save and pack**, not Save.
- **Warning:** likely a mistake, or likely to play badly.
- **Info:** for the author's awareness.

**Fix** means a quick fix exists. Rule IDs are **stable**. A rule may be retired, but its ID is
never reused.

### Timing

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR001 | Warning | No time signature at tick 0; the games assume 4/4 | Add 4/4 at 0 |
| CHR002 | Warning | A tempo below 20 or above 400 BPM (probably a typo or a half/double-time mistake) | — |
| CHR003 | Warning | A note, phrase or event after the end of the audio | — |
| CHR004 | Info | A note isn't on the current snap grid or on any finer common grid (1/192 or triplet) | Snap to grid |
| CHR005 | Warning | Two tempo markers or two time signatures on the same tick | Keep the later one |

### 5-fret and GHL

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR020 | Warning | A sustain runs into the next note on its lane, so the games will cut it short | Trim sustain |
| CHR021 | Info | A sustain is shorter than the sustain cutoff, so the games read it as a plain note | Remove sustain |
| CHR022 | Error | An open note in a chord with frets | Remove open |
| CHR023 | Info | A force HOPO or force strum that doesn't change the note | Remove force |
| CHR024 | Warning | A forced HOPO on a note that repeats the previous note or chord exactly; the games read it as a strum | Remove force |
| CHR025 | Warning | The first note of a part is a HOPO (forced or tap) | Remove force |
| CHR026 | Warning | Notes closer together than 1/64 of a whole note (likely a stray duplicate) | Delete the later one |

### Drums

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR040 | Info | A tom marker that covers no note | Remove marker |
| CHR041 | Warning | More than two pads at once (not counting the kick): more hands than a drummer has | — |
| CHR042 | Warning | A drum fill overlaps a star power phrase | — |
| CHR043 | Warning | A drum fill whose last tick has no note, so there is nothing to activate on | Move end to last note |
| CHR044 | Warning | A roll or trill phrase with fewer than two notes, or a trill covering only one pad | — |
| CHR045 | Warning | Cymbal or tom markers on a song whose `pro_drums` is `False`, so they will be ignored | Set `pro_drums = True` |
| CHR046 | Warning | Orange notes on a 4-lane song, or notes the 4-lane ↔ 5-lane conversion couldn't place | — |
| CHR047 | Info | An accent or ghost on a kick, which some games ignore | — |

### Phrases and events

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR060 | Warning | A star power phrase with no notes in it | Delete phrase |
| CHR061 | Warning | A solo with no notes in it | Delete phrase |
| CHR062 | Warning | Two star power phrases, or two solos, overlap | Merge |
| CHR063 | Warning | Notes inside a big rock ending, which the games skip | — |
| CHR064 | Info | A disco flip section that starts or ends between notes of a chord | — |

### Song

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR080 | Warning | A part has notes on some difficulties and none on others | Reduce ([C9](#c9-difficulty-authoring)) |
| CHR081 | Info | A lower difficulty has more notes per second than the one above it | — |
| CHR082 | Info | The chart has no sections | — |
| CHR083 | Warning | A part with notes has no `diff_*` value, or `-1` | Use suggested rating |
| CHR084 | Warning | `song_length` doesn't match the audio's length within 1 s | Update `song_length` |
| CHR085 | Info | `[end]` is missing or before the last note; it is moved on save | — |

---

<a id="midi-layout"></a>

## 7. `notes.mid` layout

This section records the layout the writer uses. It is written from the format documentation in
[TheNathannator/GuitarGame_ChartFormats](https://github.com/TheNathannator/GuitarGame_ChartFormats)
(CC0) and from what YARG's MIDI reader does, which `MidiChartReader` and `MidiLayout` already
follow. Where this table and `MidiLayout` disagree, **`MidiLayout` is right** and this section is
the bug.

### 7.1 Notes

Each difficulty's notes start at a base pitch: **60, 72, 84 and 96** for Easy to Expert for
5-fret and drums, and **58, 70, 82 and 94** for GHL (`MidiLayout.BaseOf`). Offsets from the base:

| Offset | 5-fret | GHL | 4-lane / Pro drums | 5-lane drums |
| -----: | ------ | --- | ------------------ | ------------ |
| −1 | Open (needs `[ENHANCED_OPENS]`) | — | 2× kick (Expert only, pitch 95) | 2× kick (Expert only, pitch 95) |
| 0 | Green | Open | Kick | Kick |
| 1 | Red | White 1 | Red | Red |
| 2 | Yellow | White 2 | Yellow | Yellow |
| 3 | Blue | White 3 | Blue | Blue |
| 4 | Orange | Black 1 | Green | Orange |
| 5 | Force HOPO | Black 2 | — | Green |
| 6 | Force strum | Black 3 | — | — |
| 7 | — | Force HOPO | — | — |
| 8 | — | Force strum | — | — |

Opens and taps can also be written as Phase Shift sysex events (`'P' 'S' 0 0 <difficulty> <kind>
<on/off>`: kind 1 open, 4 tap, difficulty `0xFF` for all). The writer uses `[ENHANCED_OPENS]` for
opens and per-difficulty sysex for taps, as `ChartToMidi` already does. Both are read.

### 7.2 Shared markers (every difficulty)

| Pitch | Meaning |
| ----: | ------- |
| 103 | Solo |
| 104 | Tap (5-fret and GHL) |
| 110, 111, 112 | Pro drums tom markers: yellow, blue, green |
| 116 | Star power |
| 120–124 | Drums: fill / activation lane. Guitar and bass: big rock ending, when all five are held together |
| 126 | Drums: roll (single lane) |
| 127 | Drums: trill (two lanes) |

A song whose `multiplier_note` is 103 keeps its star power on 103 instead of 116, and has no solos
(`ChartReadOptions.MultiplierNote`). The charter reads such charts as they are, and always writes
star power on 116 and solos on 103, clearing `multiplier_note` (shown as a pending change).

A roll or trill's velocity can limit it to harder difficulties: 21–30 down to Easy, 31–40 down to
Medium, 41–50 down to Hard, anything else Expert only. `ChartToMidi` already writes this.

### 7.3 Text events

| Event | Where | Meaning |
| ----- | ----- | ------- |
| `[ENHANCED_OPENS]` | part track, tick 0 | Offset −1 notes are opens |
| `[ENABLE_CHART_DYNAMICS]` | drums track, tick 0 | Velocity 127 = accent, 1 = ghost |
| `[mix <d> drums<n>]`, `[mix <d> drums<n>d]` | drums track | Drum mix per difficulty; the `d` suffix is disco flip (parsed by `ChartTextEvents.TryParseMix`) |
| `[section <name>]` / `[prc_<name>]` | `EVENTS` | Practice sections |
| `[music_start]`, `[music_end]`, `[coda]`, `[end]` | `EVENTS` | Song events |

### 7.4 Tracks the writer creates

The tempo track, each part (`PART GUITAR`, `PART BASS`, `PART RHYTHM`, `PART GUITAR COOP`,
`PART DRUMS`, `PART GUITAR GHL`, `PART BASS GHL`, `PART RHYTHM GHL`, `PART GUITAR COOP GHL`),
`EVENTS` and `BEAT` (pitch 12 on each measure's downbeat and 13 on other beats). Everything else is
passed through ([§4.2](#lossless-passthrough)). A legacy `T1 GEMS` or `PART REAL_DRUMS_PS` track
is read as its part, and saved under the current name.

---

## 8. UI outline

The charter replaces the library and detail panes in the main window. The toolbar stays, with the
charter's own tools.

**Highway view:**

```txt
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ← Library   Artist – Title *    [Highway|Timeline]  Snap [1/16 ▾]  ⟲ ⟳   [Save ▾]    │
├──────────────┬────────────────────────────────────────────┬──────────────────────────┤
│ PARTS        │ ▕▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▕ │ PROPERTIES               │
│ ● Guitar     │   m42 ┼──┼──┼──┼──┼──┼  ·    [Chorus 2]  │ Note     m41 · beat 3    │
│   E M H X    │       │  │  │◆ │  │  │  ·onset          │ Tick     78,960          │
│ ○ Bass       │       │● │  │  │  │  │                   │ Time     1:38.412        │
│   E M H X    │   m41 ┼──┼──┼──┼──┼──┼  ·                │ Lane     Yellow          │
│ ○ Drums Pro  │       │● │● │  │  │  │  ░▒▓ waveform     │ Length   1/8             │
│   · · H X    │       │  │  ┃  │  │  │                   │ Force    [HOPO][Strum][Tap]│
│              │   m40 ┼──┼──┼──┼──┼──┼  ★ star power     │                          │
│ [+ Add part] │ ══════╪══╪══╪══╪══╪══╪═ strike ═══════   │ FINDINGS  0 ⨯ · 2 ⚠ · 1 ℹ │
│              │ ▁▂▅▇█▇▅▃▂▁▂▃▅▇▅▃▁▂▃▅▇█▇▅  overview  ▕▮▕   │ ⚠ CHR020 sustain cut  →  │
├──────────────┴────────────────────────────────────────────┴──────────────────────────┤
│ ▶ 1:38.4 / 4:12.0   Speed [75%] ♪PC   ⟲Loop  ♩Metro  🔊Hits   Stems ▾   Latency 42 ms │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

**Timeline view** (same panels; the canvas changes):

```txt
├──────────────┬───────────────────────────────────────────────────────────────────────┤
│              │ m40        m41        m42        m43        [Chorus 2]                 │
│              │ ▂▃▅▇█▇▅▃▂▁▂▃▅▇▅▃▁▂▃▅▇█▇▅▃▂▁▂▃▅▇▅▃▂▁   spectrogram / waveform strip   │
│   Green      │ ●──────    ●                 ●                                         │
│   Red        │       ●         ●    ●                                                 │
│   Yellow     │                   ◆     ●───────                                      │
│   Blue       │   ●                                ●                                  │
│   Orange     │                                                                        │
│   Open       │ ═══════════                                                            │
│   Onsets     │ ·  ·  ·   ·  · · ·   ·  ·   ·                                          │
```

**New-chart wizard:**

```txt
┌ New chart ─────────────────────────────────────────────────────────────┐
│  ① Song  ─  ② Audio  ─  ③ Timing  ─  ④ Parts                          │
│                                                                         │
│  Audio files                                                            │
│  ┌───────────────────────────────────────────────────────────────────┐ │
│  │ Guitar.OGG     → [guitar ▾]   3:58.2   Vorbis 44.1 kHz   ✓        │ │
│  │ Drums.ogg      → [drums  ▾]   3:58.2   Vorbis 44.1 kHz   ✓        │ │
│  │ mix.mp3        → [song   ▾]   3:58.4   MP3 44.1 kHz      ✓        │ │
│  └───────────────────────────────────────────────────────────────────┘ │
│  [Add files…]   [ ] No audio — length [ 3:00 ]                          │
│                                                                         │
│  Location  [ D:\Songs ] [Browse]   Folder  [ Artist - Title ]           │
│                                          [Cancel]  [Back]  [Next →]    │
└─────────────────────────────────────────────────────────────────────────┘
```

**Calibration tool:**

```txt
┌ Audio/visual calibration ─────────────────────── Device: Headphones (USB) ┐
│                                                                            │
│      ┌────────────┐            │  │  │  │                                  │
│      │            │            │  │▼ │  │     ← note scrolls down          │
│      │   FLASH    │            │  │  │  │                                  │
│      │            │          ══╪══╪══╪══╪══  strike line                    │
│      └────────────┘                                                        │
│                                                                            │
│  Adjust until the beep lands on the flash.                                 │
│  Latency  [−10][−5][−1] ━━━━━━━━━━━●━━━━━━━━━━ [+1][+5][+10]   [ 42 ] ms   │
│                                                                            │
│  [Tap test…]   Last tap test: 39 ms (spread 11 ms, 24/24 taps)             │
│                                   [Reset]   [Cancel]   [Save]              │
└────────────────────────────────────────────────────────────────────────────┘
```

## 9. Keyboard shortcuts

These are the defaults. Rebinding is future work. Shortcuts act on the focused editor canvas.

| Keys | Action |
| ---- | ------ |
| `1`–`5` (`1`–`6` GHL) | Place or remove a note on that lane at the cursor; in record mode, play a note |
| `0` | Open note, or drum kick |
| `Shift`+`0` | 2× kick (Expert drums) |
| `Space` | Play or pause from the cursor |
| `↑` / `↓` | Move the cursor one grid step |
| `PgUp` / `PgDn` | Move the cursor one measure |
| `Ctrl`+`↑` / `Ctrl`+`↓` | Previous or next section |
| `[` / `]` | Coarser or finer snap |
| `Ctrl`+wheel, `+` / `−` | Zoom |
| `Ctrl`+`1`–`4` | Easy to Expert |
| `Tab` | Switch between Highway and Timeline |
| `H` / `G` / `T` | Toggle force HOPO, force strum, tap on the selection |
| `C` | Toggle cymbal or tom (Pro drums) |
| `A` / `Shift`+`A` | Toggle accent or ghost (drums) |
| `S` | Star power phrase over the selection |
| `L` | Set the loop region to the selection |
| `R` | Start or stop record mode |
| `W` | Switch between waveform and spectrogram |
| `Ctrl`+`Z`, `Ctrl`+`Y` | Undo, redo |
| `Ctrl`+`X` / `C` / `V` | Cut, copy, paste |
| `Delete` | Delete the selection |
| `Ctrl`+`S` | Save |

## 10. Settings

New rows in the settings window, built through `AppSettingsCatalog` like the existing ones and
stored in `AppSettings`:

| Setting | Default |
| ------- | ------- |
| Default resolution for new charts | 480 |
| Default snap | 1/16 |
| Default editor view | Highway |
| Editor highway speed | The same as the inspector's `HighwayNoteSpeed` |
| Notes keep ticks or times when the tempo changes | Keep ticks |
| Waveform or spectrogram behind the lanes | Waveform |
| Spectrogram memory budget | 256 MB |
| Metronome volume / hit-sound volume | 50% / 50% |
| Audio/visual latency | Per output device, plus a global fallback of 0 ms ([C11](#c11-calibration-tool)) |
| Prompt for calibration on first charter use | On |
| Autosave interval | 2 minutes |
| Default charter name | Empty (used for `charter` on new charts) |
| Difficulty reduction rules | As in [C9](#c9-difficulty-authoring) |

There is no existing playback-offset setting in `AppSettings` to extend. The latency is new, and is
stored as a map from device name to milliseconds plus the fallback.

## 11. Non-functional requirements

| Area | Requirement |
| ---- | ----------- |
| Rendering | 60 fps while scrolling and playing a 10-minute chart with 20,000 notes per difficulty. Only the visible range is drawn. |
| Responsiveness | Spectrogram, onsets, tempo estimation, lint and saving run off the UI thread. The UI thread is never blocked for more than 16 ms while editing, nor 100 ms at any time ([project-spec §9](project-spec-v1.md#9-non-functional-requirements)). |
| Memory | The spectrogram stays within its budget and is never written to disk. The undo stack keeps every step but stores changes, not copies of the chart, and stays under 64 MB for a session of 10,000 edits. |
| Timing accuracy | What is drawn against the audio is within 1 ms of `StemPlayer.Position` − latency, before display refresh. Metronome clicks and hit sounds are sample-accurate in the mix. |
| Durability | Every save is atomic (temp file and rename, or the `.sng` rewrite). Autosave never writes into the song. |
| Robustness | A malformed `notes.mid` opens as far as it can, with findings. It never crashes the app. Unknown tracks and events are kept. |
| AOT | Zero trim and AOT warnings. No reflection, and no new packages. |
| Privacy | No network access, as now. |

## 12. Testing strategy

| Layer | Tests |
| ----- | ----- |
| Model ↔ MIDI | Round trips (load → save → load gives an equal model) for golden `.mid` fixtures covering every part, variant and note kind in [§7](#midi-layout). Passthrough tracks stay byte-identical. |
| Writer vs reader | Every saved chart is read by `ChartReader` and compared with the model's expected view (AC C17.2), over all the fixtures and every HOPO-threshold setting |
| `.chart` import | Every `.chart` fixture in the existing `ChartToMidi` tests imports with the same warnings |
| Commands | Each command reverses exactly; random command sequences, undone, give back the start (AC C15.1) |
| Tempo map | Tick ↔ seconds property tests against `TempoMap`; anchors stay put under edits (AC C4.1) |
| Lint | At least one fixture per CHR rule, and a clean fixture triggers none (AC C16.1) |
| Analysis | Synthetic click tracks at fixed and changing tempos for `TempoEstimator`; synthetic drum hits in separate bands for `OnsetDetector`; spectrogram bins checked against known sine frequencies |
| Calibration | The tap-test estimator with simulated taps that have a known offset, jitter and outliers (AC C11.2); the beep's position in the mixed output for a given latency (AC C11.1) |
| Reduction | Each rule in [C9](#c9-difficulty-authoring), on fixtures |
| View models | `ChartEditorViewModel`, the wizard and calibration view models against fake services. There are no automated UI tests, as in v1. |

**Test isolation** is as in [project-spec §10](project-spec-v1.md#10-testing-strategy). Tests use
temp folders only. The recovery folder, the latency settings and the new-chart wizard's last-used
values take explicit paths, so `UserConfigGuard` keeps tests away from the real config folder.

## 13. Milestones

| Milestone | Contents | Exit criteria |
| --------- | -------- | ------------- |
| **CM1: Model and writer** | `EditableChart` and friends, `MidiChartLoader`, `MidiChartWriter`, passthrough, commands and undo, and the tests | Every round-trip and writer-vs-reader test passes; zero AOT warnings |
| **CM2: Calibration, new chart, 5-fret editing** | C11 calibration, C1 wizard, C2 open and import, C3 both views, C4 tempo map and grid, C5 5-fret, C10 playback, C17 save. Calibration comes first because every timing judgement after it depends on it. | AC C1, C2, C3, C4, C5, C10, C11 and C17 pass |
| **CM3: Drums and GHL** | C6 all three drum variants and conversion, C7 GHL, C8 phrases and events | AC C6, C7 and C8 pass |
| **CM4: Audio assist** | C12 waveform, C13 spectrogram, C14 onsets and tempo estimation | AC C12, C13 and C14 pass |
| **CM5: Authoring tools** | C9 reduction, C15 record mode, clipboard, transforms and autosave, C16 lint, C18 preview and suggested difficulty | AC C9, C15, C16 and C18 pass |

## 14. Future (after v1)

- Vocals and harmonies editing (lyrics, pitches, phrases), building on `VocalChart` and
  `VocalsEditor`.
- Keys, pro keys, pro guitar/bass and Elite drums.
- Venue, lighting and camera events.
- `.chart` export.
- Auto-charting drafts from audio. This would build on C14, and the result would always be
  labelled as a draft.
- Playtesting with a guitar or drum controller, with hit windows and scoring.
- A MIDI drum kit or controller as the input for record mode.
- Rebindable shortcuts.
- Suggested difficulty weights fitted to a set of rated charts, and the suggestion shown in the
  library and inspector as well as the charter.

## 15. Open questions

Decisions already made for v1, recorded here so they aren't reopened by accident:

- **Format:** the charter saves `notes.mid` only, and `.chart` is import only.
- **Placement:** a screen in the main app, not a separate window or program.
- **Views:** Highway and Timeline, both in v1.
- **Spectrogram:** held in memory only, within a budget.
- **4-lane ↔ 5-lane:** uses the mapping Clone Hero and YARG use.
- **Resolution:** 480 for new charts.
- **Calibration device key:** a stable OS device ID where there is one, then the device name, then
  the global fallback ([C11](#c11-calibration-tool)).
- **`.chart` after import:** removed on the first save, after a confirmation. No backup is kept
  ([C2](#c2-open-and-import)).
- **Suggested difficulty:** shown in the charter only, with fixed hand-set weights in v1. Tuning and
  showing it in the library are future work ([C18](#c18-preview)).
- **Generated drum fills:** the author picks the spacing, length and ending note on each run
  ([C6](#c6-drums-4-lane-pro-and-5-lane)).

There are no open questions at present.
