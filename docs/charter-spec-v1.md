# Chart Builder — Specification v1

| | |
| --- | --- |
| **Status** | Draft |
| **Scope** | Chart Builder v1: a separate app, installed with Chart Packer, for creating, editing and exporting charts |
| **Companion docs** | [project-spec-v1.md](project-spec-v1.md) (Chart Packer, whose code this shares) and [sng-spec.md](sng-spec.md) (the container charts are packed into) |
| **Output format** | `notes.mid`, in the Rock Band / Clone Hero layout that YARG reads ([§7](#midi-layout)) |

---

## 1. Overview and goals

**Chart Builder** is a chart editor. It creates charts from scratch, edits the charts in a user's
library, and saves them as `notes.mid`, ready for Clone Hero, YARG and other games that read the
format. v1 covers guitar, bass and drums.

Chart Builder is a **separate application** from Chart Packer. The two share code (Core, Audio,
Video and a shared UI library, [§4](#4-architecture)) but no process, and one installer installs
both. Chart Packer's library opens a song in Chart Builder, and Chart Builder also runs on its own
([§4.8](#48-launching-and-coordination)). Chart Builder saves through the same draft and save code
the metadata editor uses ([project-spec F5](project-spec-v1.md#f5-metadata-editor)). A chart can go
from an empty folder to a packed `.sng` without leaving Chart Builder.

Most of what Chart Builder needs already exists in some form. Chart Packer's code reads `.chart` and
`.mid` as YARG does, writes MIDI, converts `.chart` to MIDI and checks the result, plays stems
sample-aligned at any speed with pitch correction, draws waveforms and a spectrum, and draws a
read-only note highway. Chart Builder adds an **editable** model on top of these, plus the tools
that make charting fast.

Goals:

1. **Correct.** A chart plays in YARG as it looks in the editor, and in Clone Hero wherever Clone
   Hero reads the format the same way. Every save is read back twice: by Chart Builder's own loader,
   which must give back the same model, and by the app's YARG-compatible reader, which must give
   what the editor shows ([§4.3](#43-writing-midichartwriter)).
2. **Fast to chart.** Keyboard first. Common work (placing notes, chords, sustains, changing the
   snap, moving around the song) never needs the mouse.
3. **Audio-aware.** Waveforms, a spectrogram, onset markers and beat detection help place notes
   and build the tempo map. They suggest; the author decides.
4. **Integrated.** Chart Packer's library → **Edit chart** → Chart Builder → save → Chart Packer
   shows the change. Both apps use the same drafts, findings and atomic writes, and come in one
   install ([§4.8](#48-launching-and-coordination)).
5. **Lightweight.** Native AOT, the existing dependency budget, and **no new packages** beyond
   Serilog for logging ([§4.7](#47-dependencies)).

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
├── ChartPacker/                  Chart Packer app: library, inspector, packing. Launches Chart Builder (§4.8)
├── ChartBuilder/                 Chart Builder app (new): launcher (home screen), Editor window, canvases, calibration tool
├── ChartPacker.UI/               Shared Avalonia library (new): theme, icons, WindowChrome, dialogs, pickers,
│                                 settings window, shared controls, and later the player and highway view models
├── ChartPacker.Audio/            + Analysis/ (spectrogram, onsets, tempo estimation), + Synthesis/ (`Synth`: click, beep, hit sounds)
├── ChartPacker.Core/             + Charting/ (editable model, commands, MIDI chart writer, lint, reductions), + SongLock
└── ChartPacker.Core.Tests/       + Charting/, Audio/Analysis/ tests
```

Chart Packer and Chart Builder are two executables. They are built against the same package
versions (central package management in `Directory.Packages.props`), so they can share one install
folder and one copy of each native library (SDL3, Skia, HarfBuzz, ANGLE and FFmpeg). One installer
installs both ([project-spec §4](project-spec-v1.md#4-architecture)).

Core stays UI-free and Avalonia-free, as now
([project-spec §4.1](project-spec-v1.md#41-chartpackercore)). Chart Builder's model, commands, writer
and lint all live in Core, so Chart Packer (for lint in the library, [C16](#c16-chart-lint)) and a
later CLI can use them. UI code that both apps need lives in `ChartPacker.UI`, and moves there only
when the second app first needs it.

<a id="editable-model"></a>

### 4.1 Editable model (`ChartPacker.Core/Charting/`)

The existing `ChartDocument` (`Charts/ChartModel.cs`) is **immutable and lossy by design**. It
holds what the library, the highway and the merger need: resolved notes, with HOPOs worked out
and forcing folded into a `Hopo` flag. Chart Builder needs what the author wrote, so it gets its
own **mutable** model, kept separate:

| Type | Holds |
| ---- | ----- |
| `EditableChart` | The resolution, the tempo map, global events, the parts, and the passthrough tracks ([§4.2](#lossless-passthrough)) |
| `EditableTempoMap` | Tempo markers (stored as microseconds per quarter note, as MIDI and `TempoMap` store them, and shown as BPM) and time signatures in ticks, plus **anchors** (a tempo marker pinned to a time in the audio, [C4](#c4-tempo-map-and-grid)). It converts to the existing `TempoMap` for tick ↔ seconds and beat lines, so `TempoMap.SecondsAt`, `TickAt` and `BeatLines` are reused rather than rewritten. |
| `EditablePart` | One instrument (and drum variant): four `EditableTrack`s, the phrases, and for Pro drums the tom markers, which MIDI shares across every difficulty |
| `EditableTrack` | One difficulty's notes, ordered by tick, then lane, plus its per-difficulty markers: forcing and tap per chord, and disco flip ranges |
| `EditableNote` | Tick, length, lane, and what the author set per note: accent or ghost, 2× kick. Forcing, tap and cymbal/tom are read from the chord and the part (see below). |
| `Phrase` | Kind (star power, solo, drum fill / activation, roll, trill, big rock ending), tick and length, and, for rolls and trills only, the lowest difficulty it reaches |
| `TextEvent` | Sections (`[section …]` / `[prc_…]`), `[music_start]`, `[music_end]`, `[coda]`, `[end]` and free text |
| `RawEvent` | An event on an edited track that the model doesn't understand (an animation note, a face-off marker, an unknown sysex), kept with its tick and written back unchanged |

**Where flags live.** The model stores each flag at the level MIDI stores it, so that what is edited
is what is saved:

- **Forcing and tap** (force HOPO, force strum, tap) apply to every note on a tick, per difficulty,
  because the markers cover the whole chord. Toggling one on a note sets it for the note's chord.
- **Cymbal or tom** (Pro drums) is held per tick and pad for the whole part, because the tom
  markers (pitches 110–112) apply to every difficulty. Toggling a note changes the same pad at
  that tick on every difficulty, and the editor says so when another difficulty has a note there.
- **Star power, solos, drum fills and big rock endings** apply to every difficulty.
- **Rolls and trills** reach from Expert down to a chosen difficulty, as their velocity says
  ([§7.2](#72-shared-markers-every-difficulty)).
- **Disco flip** is per difficulty, as the `[mix <d> drums<n>d]` events are.

What the model keeps that `ChartDocument` doesn't:

- forced HOPO and forced strum markers, kept apart from natural HOPOs, which are worked out only
  for display, by `NoteBuilder`'s rules;
- solos, drum fills and activation lanes, rolls and trills, and big rock endings as phrases
  (today they are only passed through `ChartToMidi` as pitch constants);
- accents and ghosts, which are note velocities when `[ENABLE_CHART_DYNAMICS]` is set;
- sections and other text events;
- each note's exact sustain length. The sustain cutoff is applied only when the note is displayed;
- tempo anchors ([C4](#c4-tempo-map-and-grid)), saved as text events on the tempo track
  ([§7.3](#73-text-events));
- everything else on an edited track, as `RawEvent`s.

**Loading.** A `notes.mid` is read into the editable model by a new `MidiChartLoader`, which uses
the existing low-level `MidiFile`/`MidiEvent` reader and the layout in `MidiLayout`, and follows
`MidiChartReader`'s YARG rules for track naming, note pairing and duplicates. The note pairing is
shared with `MidiChartReader` rather than copied, so the two can't drift apart. A `notes.chart` is
first converted with the existing `ChartToMidi`, so `.chart` import gets the conversion's checks
and `ChartMergeWarning`s for free, and there is one load path.

While loading:

- **Velocities** on a drum track are read as accents and ghosts only when the track has
  `[ENABLE_CHART_DYNAMICS]`. Otherwise every note is normal, whatever its velocity, so a chart that
  used velocity 127 for other reasons doesn't gain accents when dynamics are later turned on.
  *Settled in CM1:* as YARG reads it, the event counts from where it is, so only notes let go
  after it are read this way, and the games never read an accent or ghost on the kick or the 2×
  kick. The writer always puts the event at tick 0 ([§7.3](#73-text-events)).
- **The drum variant** comes from `pro_drums` and `five_lane_drums` when `song.ini` sets them, and
  otherwise from `DrumsLayouts`' detection (a fifth-pad note means 5-lane; anything else is Pro).
  A detected variant is written on the next save, as a pending change
  ([C17](#c17-save-and-export)).
- **Tempo** is kept as microseconds per quarter note, so a load and save without edits gives the
  same tempo track.
- **What can't be loaded as written** (a duplicate note, a note that never ends, a marker that
  covers no note…) is left out or kept as a `RawEvent`, so the chart still plays as it did. The
  loader reports each one as a `ChartLoadIssue` with its part, difficulty and tick. *Settled in
  CM1:* these are not `Finding`s yet; CM2 shows them when a chart is opened.

**Commands and undo.** Every change is an `IChartCommand` with `Apply` and `Revert` (add notes,
delete notes, move, change sustain, toggle flags, add phrase, edit tempo…). A composite command
groups the changes of one gesture, so a drag or a paste undoes in one step. Commands store changes,
not copies of the chart. The undo stack keeps every step of the session; the budget in
[§11](#11-non-functional-requirements) is a size target for that, not a limit that drops steps.

**Snapshots.** Work off the UI thread (save, lint, autosave) runs on an immutable snapshot of the
model taken when the work starts, so editing never waits for it and it never sees a half-applied
command.

<a id="lossless-passthrough"></a>

### 4.2 Lossless passthrough

When a chart is loaded, every MIDI track Chart Builder doesn't edit (vocals, harmonies, keys and pro
keys, pro guitar, venue, unknown tracks) is kept as raw bytes, sliced from the file as
`MidiMerger` already does. On save they are written back **byte for byte**, in their original
order.

The tracks Chart Builder does edit are rebuilt from the model:

- **The tempo track** is rebuilt from the tempo map. Its other events (key signatures, markers,
  text) are kept as `RawEvent`s.
- **`EVENTS`** is rebuilt from the model. Text events the model doesn't understand are kept as free
  text events, and anything else (such as notes) as `RawEvent`s.
- **The part tracks** are rebuilt from the model, with their `RawEvent`s written back at their
  ticks.
- **`BEAT`** is not kept. It is regenerated from the tempo map on every save.

**When the tempo map changes in a way that keeps times.** Passthrough tracks hold ticks, so a tempo
edit with *keep times* on ([C4](#c4-tempo-map-and-grid)), or a stretch
([C15](#c15-editing-ergonomics)), would leave vocals, venue and keys at their old ticks while the
charted notes move. Chart Builder therefore re-ticks the passthrough tracks too. It parses their
events, moves each one to the tick that keeps its time, and writes them back with the same events
in the same order. Byte-for-byte passthrough is promised only while the tempo map hasn't been
changed in this way. Keeping ticks (the default) never touches passthrough tracks.

### 4.3 Writing (`MidiChartWriter`)

`MidiChartWriter` turns an `EditableChart` into a Format 1 SMF using the existing `MidiWriter` and
`MidiMessage`:

- **Division.** The division is the chart's resolution. New charts use 480.
- **Track order.**
  1. The tempo track (named after the song).
  2. Each part track, in `ChartInstrument` order.
  3. `EVENTS`, then `BEAT`.
  4. The passthrough tracks.
- **Layout.** The pitches, sysex and text events in [§7](#midi-layout). Every normal note is
  written with velocity 100. Accents and ghosts are written as 127 and 1, and only on tracks with
  `[ENABLE_CHART_DYNAMICS]`.
- **Read-back check.** The bytes are read back in two ways, and a difference in either blocks the
  save and is reported with the times it happens at:
  1. **Model round trip.** `MidiChartLoader` reads them back, and the result must equal the model:
     every note, flag, phrase, event, anchor and `RawEvent`.
  2. **Game view.** `ChartReader` reads them as YARG does, and the result must equal the model's
     **game view**. The game view is the `ChartDocument` the model projects to, using
     `NoteBuilder`'s rules and the song's `song.ini` settings. It is the same projection the
     editor draws natural HOPOs and sustain cutoffs from, and the one preview uses
     ([C18](#c18-preview)). The comparison extends `NoteCheck` (behind `ChartMergeWarning`) to
     compare star-power phrases, exact sustain lengths, accents, ghosts and the tempo map as well.
     `MidiChartReader` is extended to read `[ENABLE_CHART_DYNAMICS]` and velocities, as YARG does.

  The game view shows what the games will read even when the author wrote something they read
  differently (for example an open note in a chord, CHR022). So a chart with lint errors still
  passes the check and can be saved.

### 4.4 Audio analysis (`ChartPacker.Audio/Analysis/`)

| Component | Responsibility |
| --------- | -------------- |
| `SpectrogramBuilder` | STFT over a stem using the existing `Spectrum/Fft` and `Dsp/Windowing`, mapped to log-frequency bins and quantised to bytes. Runs off the UI thread, produces columns in chunks so the view can draw as they arrive, and is **held in memory only** ([C13](#c13-spectrogram)). |
| `OnsetDetector` | Spectral-flux onset detection with an adaptive threshold: one envelope per stem, plus one per band for drums ([C14](#c14-onset-and-beat-assist)) |
| `TempoEstimator` | Estimates a local tempo through the song from the onset envelope's windowed autocorrelation (a tempogram), with a preference for 60–200 BPM, then tracks beats with dynamic programming that follows that local tempo, so step changes are tracked rather than averaged. Gives a proposed list of beat times, a confidence per region, and a tempo map with anchors. |
| `Synth` | Makes the metronome click, the note hit sounds and the calibration beep as PCM. They are mixed in **after** the time-stretcher, so they are never stretched, and share the stems' output clock and latency ([§4.4.1](#441-playback-changes)). |

Decoding reuses `AudioDecoder`/`IPcmSource` and `StemReader`, and waveforms reuse `StemPeaks` and
`StemPeaksCache`, adding a stereo option for [C12](#c12-waveform). Playback builds on `StemPlayer`,
which already has speed, pitch correction (`Dsp/TimeStretcher`) and a heard `Position`.

#### 4.4.1 Playback changes

`StemPlayer` needs three changes for Chart Builder's timing promises:

- **Synthesized sounds after the stretcher.** `StemMixer` mixes the stems before `TimeStretcher`.
  Clicks and hit sounds mixed there would be stretched and smeared at any speed other than 100%.
  `StemPlayer` therefore gets a second, post-stretch mix stage. Synthesized events are scheduled on
  it by song time, which `PositionMap` converts to output frames.
- **The true source position.** While stretching, `TimeStretcher.SourcePosition` is the nominal
  position, and the grain actually played can be up to ±12 ms away. The stretcher reports the
  source position of the grain it actually plays, and `PositionMap` records that.
- **A smooth clock.** `Position` only moves when SDL takes a buffer. Drawing uses
  `SmoothPosition`, which interpolates between those updates with a monotonic clock, scaled by the
  speed, and is pulled back onto `Position` when they differ.

*Settled in CM2:*

- **The grains played.** An output frame of a stretched hop is the last grain carrying on and the
  next fading in, weighted by the window. The position it stands for is the same blend of the two
  grains' positions (`TimeStretcher.PlayedPosition`). `StemPlayer` maps each grain in eight pieces,
  so the map follows the fade. Where two grains are nearly as loud as each other, a click in the
  stems is heard twice, a few milliseconds apart, which no one position can match.
- **Laying sounds over the mix.** `StemPlayer.Sounds` is an `ISoundSchedule` the playback thread
  asks for the sounds starting in each chunk's stretch of the mix. A sound starts where the frames
  played first reach it. A new schedule is handed over whole, never edited, and a seek drops the
  sounds playing. Played as mixed at another speed, sounds are read at that speed, so the device's
  resampling leaves them at their own pitch.
- **`SmoothPosition`** runs on a `TimeProvider` (the system's monotonic clock), takes up half of how
  far it is from `Position` each time that moves, and all of it past 50 ms or after a seek, a change
  of speed, playing or pausing.

**Latency and speed.** The calibrated latency ([C11](#c11-calibration-tool)) is in real time, and
`Position` is in song time. Everything drawn against the audio is drawn at
**`SmoothPosition` − latency × speed**, so at 50% speed a 40 ms latency is 20 ms of song.

**Audio time and chart time.** The song's delay is how much later than its ticks say the chart plays
against the audio. It comes from `SongChartSettings.DelayOf` (`delay`, then `delay_seconds`, then
the chart's own offset), as the inspector's highway already uses it. Everything measured from the
audio (`Position`, anchors, onsets, waveforms, the spectrogram) is in **audio time**. Everything in
the chart is in **chart time**, from `TempoMap.SecondsAt`. They are related by
`audio = chart + delay`, applied in one place in the editor.

### 4.5 App (`ChartBuilder/`)

- **Its own composition root, `MainWindow` and `ViewLocator`**, wired by hand as Chart Packer's
  are. Settings are Chart Builder's own ([§10](#10-settings)).
- **Launcher** (the home screen), shown when no song is given on the command line: icon tiles for
  **New chart**, **Open chart** (a folder or `.sng`) and **Settings**, and recent songs. It is a
  window of its own. **New chart** opens **Chart settings** over it ([C1](#c1-new-chart-and-chart-settings)).
  Creating the chart there, or opening a song, closes the launcher and opens an **Editor window**
  in its place. Closing the Editor brings the launcher back.
- **Editor window** ([§8](#8-ui-outline)):
  - **Toolbar.** It shows the program's icon and version, a **Settings** menu with **Chart
    settings** and **Program settings**, the editing tools, and **Undo** and **Redo**. Its groups
    wrap onto more rows as the window narrows rather than scrolling.
  - **Side panels, tabbed and docked like Visual Studio's tool windows.** The left one has the
    **Chart Inspector** tab: Chart Packer's inspector without the background video or image, the
    note highway or the lyric lane, which the editor itself shows. The right one has the
    **Properties** tab, which shows the selection ([C3](#c3-editor-layout)).
  - Each panel can be resized, and collapsed to a strip along the window's edge that brings it back
    when clicked. Their widths and whether each is collapsed are saved
    ([§10](#10-settings)).
  - **Timeline**, along the bottom at the window's full width. It shows the chart's notes over the
    waveform of the whole mix. It can be resized (the height is saved) and scrubbed.
  - The chart's canvas (below) fills the middle.
- **`ChartEditorView` / `ChartEditorViewModel`**, added to the explicit mapping in `ViewLocator`
  (there is no reflection-based lookup).
- **Two custom-drawn canvases over one model**: `HighwayEditorCanvas` (vertical) and
  `TimelineEditorCanvas` (horizontal). They share one tool and hit-testing layer, `EditorTools`
  (place, select, erase, sustain, phrase, tempo), so behaviour is the same in both. Drawing builds
  on `NoteHighwayView`/`HighwayScene` (note shapes, lane colours, beat lines) and
  `LaneWaveformPainter`.
- **`ChartSettingsWindow`** ([C1](#c1-new-chart-and-chart-settings)) and **`CalibrationWindow`**
  ([C11](#c11-calibration-tool)) are dialogs. They use the shared `WindowChrome` for the dark title
  bar and icon, as the other dialogs do.
- **Drag and drop.** Chart Builder's launcher takes dropped audio files, which start a new chart
  with them in Chart settings ([C1](#c1-new-chart-and-chart-settings)), and a dropped song folder or
  `.sng`, which opens it
  ([C2](#c2-open-and-import)). Chart Packer's main window takes no drops for Chart Builder.
- Strings go in Chart Builder's own `Strings.resx`, and strings both apps use go in
  `ChartPacker.UI`'s `SharedStrings.resx`. New icons come from game-icons.net and are credited in
  `THIRD-PARTY-NOTICES.md`. Styling follows the shared theme in `ChartPacker.UI/Styles/`.

### 4.6 Saving

Chart Builder saves through the existing draft machinery. A save builds a `MetadataDraft` that holds:

- the new `notes.mid` as a file change. `MetadataDraft.AddFile` takes a source path, so the bytes
  are first written to a temp file in the per-user cache, which is deleted once the draft is
  written or dropped;
- the removal of an imported `notes.chart`, if there is one ([C2](#c2-open-and-import));
- the `song.ini` key edits ([C17](#c17-save-and-export)).

`DraftWriter` then writes the draft, so a `.sng` keeps its mask, with no new write path. Chart
Packer's library, inspector and packer see the result when Chart Packer next re-reads the song
([§4.8](#48-launching-and-coordination)).

**Drafts belong to one app.** Drafts live in memory, in the process that made them, so the two apps
can't share them. Chart Builder keeps its own draft for the one song it has open. Its pending
`song.ini` changes are listed in Chart Builder ([C17](#c17-save-and-export)), not in Chart Packer's
metadata editor. Chart Packer's unsaved drafts for a song are never written by Chart Builder:
**Edit chart** on a song with unsaved drafts first asks the user to save or discard them
([C2](#c2-open-and-import)).

**Atomicity.**

- A `.sng` save is atomic, because `SngWriter.RewriteAsync` writes a temp file and renames it.
- A song folder save is atomic **per file**: each file is written to a temp file and renamed.
- `SongFolderFiles.ApplyAsync` today deletes removed files before it moves the new ones in. Chart
  Builder changes that order to **move in first, then delete**. Otherwise a crash during a
  `.chart` import's first save could leave the folder with neither `notes.chart` nor `notes.mid`.
  With the new order, the worst case is a folder holding both, and `ChartReader.Pick` then reads
  the new `notes.mid`.

### 4.7 Dependencies

**No new packages, except logging.** FFT, windowing, decoding, mixing, output, MIDI reading and
writing, and the settings are all in-house or already budgeted
([project-spec §4.3](project-spec-v1.md#43-native-aot-constraints-required)). The AOT constraints
apply unchanged: compiled bindings, source-generated MVVM and JSON, and no reflection.

The one addition is **Serilog** (`Serilog` and `Serilog.Sinks.File`, Apache 2.0). Both apps log
through it to daily files in the per-user log folder (`UserConfigPaths.LogFolder`), which is kept
outside the config folder. It is configured in code (`ChartPacker.UI/AppLog.cs`), never from a
settings file, and nothing is logged with `{@…}` destructuring, so no reflection is needed. Core
logs through Serilog's static `Log`, which is silent until an app configures it, so tests write no
logs.

`THIRD-PARTY-NOTICES.md` credits GuitarGame_ChartFormats as the reference for the MIDI layout in
[§7](#midi-layout), and YARG.Core for the reading behaviour the reader, loader and game view
follow. No text or code is copied from either.

### 4.8 Launching and coordination

Chart Packer and Chart Builder share no process and don't talk to each other while they run. Chart
Packer starts Chart Builder with command-line arguments, a lock file keeps them from writing the
same song at once, and Chart Packer re-reads the songs Chart Builder may have changed.

**Command line.**

| Arguments | Opens |
| --------- | ----- |
| *(none)* | The home screen ([§4.5](#45-app-chartbuilder)) |
| `[--] <song path>` | That song folder or `.sng` in the editor ([C2](#c2-open-and-import)) |
| `--new [--] [<audio file>…]` | Chart settings for a new chart, with the audio files ([C1](#c1-new-chart-and-chart-settings)) |
| `--calibrate` | The calibration tool ([C11](#c11-calibration-tool)) |

`--` ends the options, so a path that starts with `-` is still a path. Paths are made absolute against
the current folder as they're read (`BuilderArgs.Parse`). Arguments that can't be used (an unknown
option, a second song path, an empty argument, a missing path) open the home screen and say why.

**Finding Chart Builder.** Chart Packer runs the `ChartBuilder` executable that sits next to its
own (`Environment.ProcessPath`). If it isn't there, Chart Packer says Chart Builder isn't installed
and goes on without it. In Debug builds each app keeps its own output folder. Building Chart Packer
builds Chart Builder too, and records where its output folder is (an `AssemblyMetadata` attribute),
which Chart Packer uses when Chart Builder isn't beside it. Release builds have no such fallback.

**One song per Chart Builder process.** Each Chart Builder window has at most one song open, and
several can run at once on different songs.

**Song lock (`ChartPacker.Core/Scanning/SongLock`).**

- While a song is open, Chart Builder holds a lock file for it in the per-user config folder
  (`locks/<hash of the song's full path>`), opened with `FileShare.None`. .NET maps `FileShare` to
  `flock` on Linux and macOS, so the lock works on every platform, and the OS releases it if the
  process dies.
- Opening a song that another Chart Builder has open says so, and doesn't open it a second time.
- Before Chart Packer writes to a song (a metadata save, delete, dedup, merge into it, or replacing
  it with a download), it checks the lock. When Chart Builder has the song open, it warns that
  Chart Builder may overwrite the change, and the user can go ahead or cancel.
- `SongFileGate` stays the gate between reads and writes within each process.

**Refreshing Chart Packer.**

Chart Packer doesn't keep the Chart Builder processes it starts. It checks for changes when its
window is activated, as it is when the user comes back from Chart Builder.

- Chart Packer remembers the songs it opened in Chart Builder, with each one's chart and
  `song.ini` sizes and modified times. When its window is activated, it re-reads the songs that
  changed, so the library, inspector and packer show them.
- Chart Packer never starts a new chart, so a song Chart Builder creates appears in its
  library on the next scan.
- Chart Packer re-reads the shared latency file ([§10](#10-settings)) when its window is
  activated.

---

## 5. Features

Every feature lists numbered acceptance criteria (**AC C*n*.*m***). "Findings" are `Finding`s
shown in the app's findings panel. Chart lint rules are listed in [§6](#lint-rules).

### C1. New chart and Chart settings

**Chart settings** is the dialog that sets up a chart: its timing and its parts. For a new chart it
also asks for what the song folder needs before it can be written: the song's name, its audio and
where it goes. Everything else about the song (album, genre, year, album art, more stems, `delay`,
the difficulty ratings and any other `song.ini` key) is edited in the **Chart Inspector**, as in
Chart Packer's inspector ([§4.5](#45-app-chartbuilder)), and Chart settings doesn't repeat it.

**Input:** nothing, or one or more audio files.
**Output:** a new song folder containing `notes.mid`, `song.ini` and its stems. It is opened in the
editor, and appears in Chart Packer's library ([§4.8](#48-launching-and-coordination)).

**Entry points.**

- A new chart is started from **New chart** on Chart Builder's launcher, from audio files dropped on
  the launcher ([§4.5](#45-app-chartbuilder)), or from `ChartBuilder --new` on the command line
  ([§4.8](#48-launching-and-coordination)). Each opens Chart settings in **New chart** mode, over
  the launcher, with the audio files it was given.
  - **Create** writes the song folder, closes the launcher and opens the song in the Editor window,
    as opening a song does ([C2](#c2-open-and-import)): it takes the song lock and is added to the
    recent songs.
  - **Cancel** leaves the launcher as it was. Nothing is written.
- On an open song, **Chart settings** on the Editor's ⚙ menu opens it in **Song** mode.
- A song with neither chart file is offered **New chart in this song** when it is opened
  ([C2](#c2-open-and-import)). That opens New chart mode without the Song, Audio and Location
  sections, and Create adds `notes.mid` and the `song.ini` keys to the song through its draft
  ([§4.6](#46-saving)).

Chart Packer has no **New chart**. Charts are created in Chart Builder, and Chart Packer's only way
into it is **Edit chart** ([C2](#c2-open-and-import)).

**The dialog.** A modal dialog with the shared `WindowChrome`. Its sections are listed on the left,
and the chosen one is shown on the right (mock-up in [§8](#8-ui-outline)).

| Section | New chart | Song | Contents |
| ------- | :-------: | :--: | -------- |
| **Song** | ✓ | — | Name, artist and charter. They are prefilled from the first audio file's tags where it has them (`AudioFileInfo.Read`), and the charter from the **Default charter name** setting ([§10](#10-settings)). |
| **Audio** | ✓ | — | The audio files given, plus **Add files…** and a remove button per file (below). |
| **Timing** | ✓ | ✓ | Resolution, starting BPM and starting time signature (below). |
| **Parts** | ✓ | ✓ | The parts and the drum variant (below). |
| **Location** | ✓ | — | Where the song folder is created (below). |

**Audio** (New chart).

- Files can be `.ogg`, `.opus`, `.mp3` or `.wav`. Each is assigned a stem from its file name using
  `AudioStems`, e.g. `Drums.ogg` → `drums`, `song.mp3` → `song`. `AudioStems` gives no stem for a
  name it doesn't recognise. The first such file is then assigned `song` if no other file has it,
  and any other is left unassigned (`NewChartStems`).
- The stem can be changed from a dropdown. Two files can't share a stem, and **Create** stays
  disabled, saying why, while a file is unassigned or two files clash.
- Each file is decoded briefly to check it is what its extension says, as in
  [F7](project-spec-v1.md#f7-audio-playback).
- **No audio is allowed.** The user enters a length (`NewChartLength`, 3:00 by default) and charts
  against a silent timeline. The song then has finding SNG052 (no audio stem) until a stem is added
  in the Chart Inspector.

**Timing** (both modes).

- **Resolution**: 192, 480 or 960, the values at which every common snap lands on a whole tick. A new
  chart starts on the **Default resolution** setting ([§10](#10-settings)). On an open song it shows
  the chart's own resolution, which may be another value, and changing it re-ticks the chart (below).
- **Starting BPM**: 10 to 1000, with up to three decimals (default 120).
- **Starting time signature**: any that [C4](#c4-tempo-map-and-grid) allows (default 4/4).
- On an open song, the starting BPM and time signature are the tempo and time signature markers at
  tick 0. Changing them is a tempo edit ([C4](#c4-tempo-map-and-grid)): it follows the *keep ticks*
  or *keep times* setting, and keeps anchors where they are.
- **Detect from audio** comes with [C14](#c14-onset-and-beat-assist) (CM4). It runs the tempo
  estimate on the mix, fills in the starting BPM, and proposes a tempo map to review in the editor.

**Parts** (both modes).

- A tick box for each v1 part: 5-fret Lead, Bass, Rhythm and Co-op; GHL Guitar, Bass, Rhythm and
  Co-op; Drums, with a choice of 4-lane, Pro or 5-lane.
- MIDI has no empty difficulties, so a new part starts with all four difficulties empty. Each one
  exists as soon as it has a note.
- On an open song, unticking a part that has notes asks for confirmation, as **Remove part** does
  ([C2](#c2-open-and-import)).
- On an open song, the drum variant of a drums part that has notes is changed with the conversion in
  [C6](#c6-drums-4-lane-pro-and-5-lane) (CM3). Until then the variant can be changed only while the
  part has no notes.

**Location** (New chart).

- A parent folder, by default the last one used for a new chart, otherwise Chart Packer's library
  root (read from Chart Packer's settings, never written), plus a folder name.
- The folder name follows `<artist> - <name>` until it is edited, with characters that aren't
  allowed in a path replaced. It is checked against the `.sng` filename rules
  (`NewChartFolder.ProblemOf`).
- An **existing folder is never used or overwritten**. If the name is taken, Create is disabled and
  says why.

**Create** (New chart).

- **What is written** (`NewChartCreator`):
  - each stem, copied as `<stem>.<ext>` in lowercase;
  - `notes.mid`, with the tempo map, an empty track for each chosen part, `EVENTS` holding
    `[music_start]` and `[end]`, and `BEAT`;
  - `song.ini`, with a `[song]` section holding the name, artist and charter, `diff_<part> = -1` for
    each part (the keys are in [§7.5](#75-songini-keys-chart-builder-writes)),
    `pro_drums`/`five_lane_drums` as the drum variant needs, and `song_length` from the longest stem
    (or the length entered).
- **How it is written.** Everything is written into a temp folder beside the target, which is then
  renamed into place. A failure or cancel leaves nothing behind.

**OK** (Song mode).

- The chart edits (resolution, tempo, time signature, parts and drum variant) are applied as **one
  undoable command**.
- Their `song.ini` effects are added to Chart Builder's pending changes
  ([C17](#c17-save-and-export)): `diff_<part> = -1` for each added part, and
  `pro_drums`/`five_lane_drums` for the drum variant.
- OK with nothing changed adds no undo step and no pending change. **Cancel** changes nothing.

**Changing the resolution** (Song mode).

- The chart is re-ticked so that everything keeps its time (`ChartRetick` with
  `TickMaps.KeepTimes`). The tempo and time signature markers' ticks are scaled by the new resolution
  over the old, their tempos (microseconds per quarter note) are unchanged, and each anchor moves with
  its marker. Everything else is re-ticked as a *keep times* tempo edit is
  ([C4](#c4-tempo-map-and-grid)), the passthrough tracks included
  ([§4.2](#lossless-passthrough)).
- **Warning.** Before anything is changed, the re-tick is worked out on a copy, and finds:
  - every item whose tick doesn't scale to a whole tick, so that its time would move: a tempo or time
    signature marker, a note's start or sustain end, a chord marking, a phrase, an event, a
    `RawEvent` or a passthrough event. A move of more than a microsecond counts;
  - every item that would be left out because another lands on the same place (`RetickReport`'s
    collisions).

  If there are any, the user is warned before the change is made. The warning gives how many items
  would move, per part (and for the tempo map, the events and the passthrough tracks), the largest
  move in milliseconds, and each item that would be left out. The user can **Change anyway** or
  **Cancel**. A change that moves nothing, such as 480 → 960, gives no warning.
- The change is one undoable step, and undoing it puts every tick back as it was.
- Byte-for-byte passthrough no longer holds for the next save, as after any *keep times* edit.

Acceptance criteria:

- **AC C1.1** A chart created with one stem and no notes validates with no errors
  ([project-spec F3](project-spec-v1.md#f3-validate)), packs to a `.sng`, and reopens in the editor
  with the same tempo map, parts and metadata.
- **AC C1.2** Cancelling New chart, or failing to copy a stem during Create, leaves no folder or temp
  folder behind.
- **AC C1.3** Create is unavailable while the target folder exists, and an existing folder's
  contents are never touched.
- **AC C1.4** Giving `Guitar.OGG`, `Drums.ogg` and `mix.mp3` assigns `guitar`, `drums` and `song`,
  and the folder contains `guitar.ogg`, `drums.ogg` and `song.mp3`. Giving `mix.mp3` and
  `other.mp3` assigns `song` to one and leaves the other unassigned, with Create disabled.
- **AC C1.5** Choosing Pro drums writes `pro_drums = True` and choosing 5-lane writes
  `five_lane_drums = True`. The reopened chart's drums are read with the same layout.
- **AC C1.6** Changing a chart's resolution from 480 to 960 moves nothing in time and gives no
  warning. From 480 to 192, a chart with a note at tick 1 gives the warning, with the number of
  items that would move and the largest move. **Cancel** leaves the chart unchanged. **Change
  anyway** is one undo step, and undoing it gives a model equal to the one before.
- **AC C1.7** On an open song, OK with nothing changed adds no undo step and no pending change, and
  Cancel changes nothing. Adding a part through Chart settings shows its `diff_<part> = -1` as a
  pending change.

### C2. Open and import

**Input:** a song folder or a `.sng`.

**Behaviour:**

1. **Opening a song.**
   - In Chart Packer, the **Edit chart** button on the toolbar runs `ChartBuilder <song path>`
     ([§4.8](#48-launching-and-coordination)). It is enabled only while exactly one song is
     selected and no job (pack, unpack, merge, validate, deduplicate) is running.
   - In Chart Builder, songs open from **Open chart** (Ctrl+O) and the recent songs on the home
     screen, or by dropping a song folder or `.sng` on the window. A folder is picked by a file in
     it (its `song.ini` or chart), as one dialog can't pick both files and folders, and a file
     dropped from a song folder opens the folder too.
   - Each Chart Builder window has one chart open. A song that another Chart Builder already has
     open isn't opened a second time ([§4.8](#48-launching-and-coordination)).
2. **Which chart is opened.** `ChartReader.Pick` chooses: `notes.mid` if there is one, otherwise
   `notes.chart`. A song with neither is offered **New chart in this song**, which sets up its
   timing and parts in Chart settings, as a new chart does ([C1](#c1-new-chart-and-chart-settings)).
3. **`.chart` import.**
   - The chart is converted with `ChartToMidi`. Any `ChartMergeWarning`s are shown before the
     editor opens, with the times they happen at, and the user can go ahead or cancel.
   - `ChartToMidi` returns the `.chart`'s `Offset` but doesn't write it anywhere. When `song.ini`
     sets no `delay`, the import adds the Offset as a pending `delay` change, as `SongMerger`
     already does through `SongChartSettings.DelayOf`.
   - **The `.chart`'s `[Song]` metadata** (`Name`, `Artist`, `Album`, `Genre`, `Year`, `Charter`,
     `PreviewStart` and the like) isn't read anywhere in the app today. The import reads it and adds
     each value as a pending `song.ini` change wherever `song.ini` has no value for that key. That
     way, removing `notes.chart` loses nothing.
   - On the first save, `notes.chart` is staged for removal after a confirmation, so the song
     doesn't carry two charts that the games might pick between differently.
4. **Drafts.** Chart Packer's unsaved metadata drafts don't reach Chart Builder
   ([§4.6](#46-saving)). **Edit chart** on a song with unsaved drafts first asks the user to
   **Save** them, **Discard** them, or **Cancel**, and Chart Builder opens only after a save or a
   discard. Chart Builder's own pending `song.ini` changes ([C17](#c17-save-and-export)) are saved
   with the chart, and the save confirmation lists them.
5. **Add part.** Chart settings' Parts section, and the lanes' + menu, add any v1 part that isn't
   there yet ([C1](#c1-new-chart-and-chart-settings)). Adding drums asks for the variant if the song
   doesn't set one.
6. **Remove part** removes a part's track, after a confirmation. It can be undone.
7. **Recovery.** If an autosave exists that is newer than the chart on disk
   ([C15](#c15-editing-ergonomics)), the user is offered **Recover** or **Discard**.

Acceptance criteria:

- **AC C2.1** Opening a `notes.mid` and saving without edits writes a file that `ChartReader`
  reads to the same `ChartDocument` and `MidiChartLoader` reads to the same model. Every
  passthrough track is byte-identical, and every `RawEvent` on an edited track (such as an
  animation note or a face-off marker) is written back at its tick.
- **AC C2.2** Importing a `notes.chart` and saving produces a `notes.mid` that reads the same as
  the `.chart` did, apart from the `ChartMergeWarning`s shown at import. The `.chart`'s `[Song]`
  values reach `song.ini` wherever it had none.
- **AC C2.3** Opening a `.sng`, editing a note and saving keeps the `.sng`'s mask and every other
  file's bytes ([project-spec F5](project-spec-v1.md#f5-metadata-editor) item 5 and AC5.1).
- **AC C2.4** Killing the app during the first save of an imported `.chart` song folder never
  leaves the folder with neither `notes.chart` nor `notes.mid`.
- **AC C2.5** While one Chart Builder has a song open, a second Chart Builder won't open it, and
  Chart Packer warns before writing to it. Once the first Chart Builder closes the song, or its
  process is killed, both work as normal.
- **AC C2.6** After Chart Builder saves a song that Chart Packer opened in it, activating Chart
  Packer's window shows the saved chart and `song.ini` in the library and inspector, without a
  rescan.

### C3. Editor layout

**Behaviour:**

1. **Two views of one model, switched with `V`.** Tab is left alone, for moving focus.
   - **Highway** (vertical). Time runs upwards, as in the games and Moonscraper. The lanes are in
     the middle, with the waveform or spectrogram drawn behind them or beside them.
   - **Timeline** (horizontal), in the style of a DAW. Time runs left to right and each lane is a
     row. A wide waveform or spectrogram strip sits above the lanes. It suits long-range editing
     and lining notes up with the audio.

   The selection, the cursor, zoom (as seconds on screen) and the tool all carry across a switch.
2. **Overview strip** along the edge. It shows the whole song: sections, note density, star power,
   solos, lint marks and the current view, and clicking or dragging it jumps there.
3. **Part and difficulty picker.** Every part with its four difficulties. The letters use the
   detail panel's difficulty colours from `Theme.axaml` (green, yellow, red, purple) where the
   difficulty has notes, and grey where it is empty. The detail panel shows only charted
   difficulties, so the grey letter is new.
   - Ctrl+1–4 switches difficulty.
   - **Ghost difficulty**: another difficulty of the same part can be shown faintly behind the
     current one, to check reductions against it.
4. **Properties panel** for the selection:
   - **Notes:** tick, time, measure and beat, lane, length, and flags. The chord's forcing and
     the part's cymbal/tom flag are shown as such ([§4.1](#editable-model)).
   - **Phrases:** start and end, plus for rolls and trills the lowest difficulty they reach.
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
   - Placing a marker sets the BPM from that tick on. BPMs are entered with up to three decimals
     and stored as whole microseconds per quarter note, as MIDI stores them. The BPM shown is
     worked out from that.
   - **Time signature markers** have a numerator from 1 to 64 and a power-of-two denominator.
2. **Anchors.**
   - Anchoring a tempo marker locks it to its time in seconds, in audio time
     ([§4.4.1](#441-playback-changes)).
   - Anchors are saved in `notes.mid` as text events on the tempo track
     ([§7.3](#73-text-events)), which the games ignore, so they survive saving, packing and
     reopening.
   - **Changing a BPM before an anchor.** The last tempo segment before the anchor that wasn't
     itself edited absorbs the change, as long as it starts after the previous anchor (or the
     start). Its BPM is recalculated so that the anchor stays where it is in the audio.
   - If there is no such segment (the edited segment runs straight into the anchor, and nothing
     else lies between the anchors), the change can't keep the anchor. The editor says so and
     offers to unanchor it, or to add a marker one beat before it.
   - Dragging an anchored marker in time changes the BPMs on both sides of it. This is how a
     tempo map is fitted to a live recording, beat by beat.
3. **When a tempo changes, notes keep their ticks (the default) or their times.**
   - Keeping ticks means the notes move in time with the grid, which is right while the tempo map
     is still being built.
   - Keeping times re-ticks, after the change, every note start and sustain end, phrase, event
     and `RawEvent`, and the passthrough tracks too ([§4.2](#lossless-passthrough)).
4. **Snap.**
   - From 1/1 to 1/192 of a whole note, including triplets (1/12, 1/24, 1/48…), plus a custom
     value.
   - `[` and `]` step through the common values. Snap can be switched off.
   - At resolutions 192, 480 and 960 every common snap lands on a whole tick. Where a snap doesn't
     (a custom snap, or an imported chart at another resolution), the nearest tick is used.
5. **Tap tempo.** Tapping `B` in time with the playing song sets the BPM of the segment at the
   cursor from the median interval of the taps.
6. **Set BPM from selection.** Select a span and say how many beats it lasts, and the BPM is
   computed. This is the quickest way to fit one bar to the waveform.
7. **Proposed tempo maps** from [C14](#c14-onset-and-beat-assist) arrive as one undoable command.
8. **Beat lines** come from `TempoMap.BeatLines`. Measure lines are stronger, and measure numbers
   are shown.

Acceptance criteria:

- **AC C4.1** In a map with markers at 0 s and 10 s and an anchor at 30.000 s, changing the BPM of
  the first segment leaves that anchor at 30.000 s ± 1 ms. Changing the BPM of the segment from
  10 s, which runs straight into the anchor, is refused with the offer to unanchor.
- **AC C4.2** With *keep times* on, a tempo change leaves every note's time, every phrase's and
  event's time, and every passthrough event's time within half a tick of where it was.
- **AC C4.3** Tick ↔ seconds round trips agree with the existing `TempoMap` to within half a tick
  for every tempo map in the test fixtures.
- **AC C4.4** Anchors survive a save, a pack to `.sng` and a reopen. Loading and saving a chart
  without edits gives the same tempo values (microseconds per quarter note), time signatures and
  anchors.

### C5. 5-fret notes

**Behaviour:**

1. **Lanes:** green, red, yellow, blue and orange, plus **open**, which is drawn across the whole
   highway.
2. **Chords:** several frets on one tick. The editor won't place an open note in a chord with frets,
   or a fret on an open. A loaded chart that has one is flagged (CHR022, an Error). The editor shows
   the chord as the games read it, so the read-back check still passes and the chart can be saved
   ([§4.3](#43-writing-midichartwriter)).
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
   - **Force HOPO**, **force strum** and **tap** are set per chord, because MIDI's markers cover
     every note on a tick. Setting one on a note sets it for the note's chord. They are drawn as
     distinct markers.
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
   - The tom markers (pitches 110–112) apply to **every difficulty**, so cymbal or tom is held per
     tick and pad for the whole part ([§4.1](#editable-model)).
   - Toggling a note between cymbal and tom adds, splits or removes the tom markers it needs, and
     changes the same pad at that tick on every difficulty. When another difficulty has a note
     there, the status bar says so. The author never manages the markers by hand.
   - Red is always a snare. 5-lane has no markers: yellow and orange are cymbals, and blue and
     green are toms.
4. **Accents and ghosts.**
   - Per note, on any pad.
   - Saved as velocity 127 (accent) or 1 (ghost), with `[ENABLE_CHART_DYNAMICS]` added to the
     track automatically while the track has any, and removed when it has none.
   - Every other note is written with velocity 100. Velocities in a loaded chart without
     `[ENABLE_CHART_DYNAMICS]` are not read as accents or ghosts ([§4.1](#editable-model)).
5. **Drum fills (activation lanes).**
   - A phrase on every difficulty. Its last note is where star power is activated.
   - **Generate fills** proposes fills over the selection or the whole part. The author picks,
     for each run:
     - the **spacing**: fills start no closer than *n* measures apart (default 4);
     - the **fill length** (default 1 measure);
     - **what a fill must end on**: a crash (default) or any note. A crash is a green cymbal in
       Pro, orange in 5-lane, and green in 4-lane.

     Fills are proposed only after the second star-power phrase has ended (the earliest point a
     player can have enough to activate), and never inside a star-power phrase. Where no note
     fits at a spot, the fill is left out. The proposals are shown as ghosts, and the author keeps
     or drops each one. Fills are never added silently, and the accepted ones are added as one
     undoable command.
6. **Rolls and trills.**
   - Single-lane roll and two-lane trill phrases.
   - New ones reach Expert only, as the games default to. Each can be extended down to Hard,
     Medium or Easy, which is saved as the velocity convention `ChartToMidi` already writes
     ([§7.2](#72-shared-markers-every-difficulty)).
7. **Disco flip.** `[mix <d> drums<n>d]` sections, which swap red and yellow in the games, are
   ranges per difficulty ([§4.1](#editable-model)). Notes are always stored on the pads the file
   uses, so moving or resizing a range never changes the file's notes. Inside a range they are
   drawn on the pads the player hits.
8. **4-lane ↔ 5-lane conversion helper.**
   - Converts a whole drum part with the fixed mapping in [§7.6](#76-drum-layout-conversion).
     Clone Hero and YARG each convert in their own slightly different way when they play one
     layout on the other. This mapping is Chart Builder's own, chosen to round-trip.
   - It is one undoable command. Notes it can't place (a second note landing on a pad that
     already has one on that tick) are left out. They are listed, with their times, in a report
     when the command finishes. Undoing the command brings them back. Lint can't see notes that
     are no longer in the chart, so the report is the only place they are listed.
9. **Drum variant.**
   - Switching between 4-lane and Pro is a song setting (`pro_drums`), not a conversion: a 4-lane
     chart's tom markers are kept, and are ignored while `pro_drums` is `False`.
   - Switching to or from 5-lane runs the conversion helper. Converting from plain 4-lane uses the
     cymbal/tom flags as they stand, which the player never saw; the helper says so.

Acceptance criteria:

- **AC C6.1** A Pro chart with every pad as cymbal and as tom, accents, ghosts, a 2× kick, a fill, a
  roll, a trill and a disco flip saves and reads back with all of them.
- **AC C6.2** Toggling the middle note of three blue cymbals to a tom leaves exactly one tom marker,
  covering only that note. A blue note at the same tick on Hard becomes a tom too, and the status
  bar says so.
- **AC C6.3** Converting 5-lane to Pro and back, on a chart with no tick that has both orange and
  green, gives the original notes back.
- **AC C6.4** A chart without `[ENABLE_CHART_DYNAMICS]` whose notes have velocity 127 gains no
  accents when one ghost note is added. Only that note is written with a velocity other than 100.

### C7. 6-fret (GHL) notes

**Behaviour:**

1. **Lanes:** white 1–3 and black 1–3 (drawn stacked per column, as in the game), plus open.
2. **Chords**, including the "barre" chords where a white and a black share a column. As in
   5-fret, an open note can't be placed in a chord with frets (CHR022).
3. **Sustains, forced HOPO, forced strum and tap**, as for 5-fret ([C5](#c5-5-fret-notes)),
   including forcing per chord.
4. Star power, solos and sections work as for 5-fret.

Acceptance criteria:

- **AC C7.1** Every one of the 6 lanes plus open, a barre chord, and each forcing kind save and
  read back unchanged.

### C8. Phrases and events

**Behaviour:**

1. **Star power** phrases. Every difficulty shares them, as MIDI stores them (pitch 116).
2. **Solos** (pitch 103), drawn as a shaded band with a note count.
3. **Sections** in `EVENTS`. They are named freely and shown in the overview strip and in a new
   section list beside it. Sections can be renamed in the list, and **Insert section** takes the
   name from a menu of common ones (Intro, Verse 1, Chorus 1, Bridge, Solo, Outro…).
4. **Big rock ending** (all five pitches 120–124 held together on a guitar or bass track) and
   `[coda]`. On drums, the same pitches are fills, and a big rock ending is the fill markers after
   `[coda]`. Chart Builder shows those as a big rock ending, not a fill, and the fill rules (CHR042,
   CHR043) skip them. No code reads big rock endings or `[coda]` today, so this is new.
5. **Song events**: `[music_start]`, `[music_end]`, `[end]`. `[end]` is kept after the last note
   and moved on save if notes are added after it (CHR085).
6. **Free text events**, in `EVENTS` or on a part track, for anything else. Events the model
   doesn't know are shown and kept, not deleted.

Acceptance criteria:

- **AC C8.1** Every phrase and event kind round trips. A free text event the model doesn't know
  survives an unrelated edit and a save.

### C9. Difficulty authoring

**Behaviour:**

1. **Copy difficulty** copies one difficulty's notes and its per-difficulty markers (forcing, taps,
   disco flip) to another, replacing it after a confirmation. It can be undone.
2. **Reduce** makes a draft of a lower difficulty from a higher one, as a starting point to edit.
   - The **Reduce** dialog shows the rules for the part's family, prefilled from the defaults in
     Settings ([§10](#10-settings)). Changes made in the dialog apply to that run only. Nothing is
     stored with the song. The shipped defaults:

     | Rule | Hard | Medium | Easy |
     | ---- | ---- | ------ | ---- |
     | Max chord size (5-fret / GHL) | 2 | 2 | 1 |
     | Frets used (5-fret) | all | G–B | G–Y |
     | Columns used (GHL) | all | 1–3 | 1–2 |
     | Thinning grid (no closer than) | 1/16 | 1/8 | 1/4 |
     | Forced HOPOs and taps | kept | removed | removed |
     | Sustains shorter than a beat | kept | kept | removed |
     | Drums: pads per hit (plus kick) | 2 | 2 | 1 |
     | Drums: kick | kept | thinned to 1/8 | thinned to 1/4 |
     | Drums: 2× kick | removed | removed | removed |

   - Chord reduction keeps the outermost frets. Reducing to a single note keeps the lowest fret.
     Frets outside the allowed range are moved inward, with their shape kept as far as possible.
     For GHL, a white and a black on one column count as one column.
   - Thinning keeps notes on strong beats first.
   - The result is shown as a ghost over the target difficulty, then applied as one undoable
     command.
3. **Difficulty letters** in the part picker show which difficulties have notes
   ([C3](#c3-editor-layout)).

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
   map. It is mixed in after the time-stretcher ([§4.4.1](#441-playback-changes)), so it stays
   sharp and in time with the stems at any speed.
4. **Hit sounds**: a short sound on each note as it reaches the strike line, mixed in the same way.
   There is one sound per part, and a pad sound for each drum pad. The volume is separate from the
   stems'.
5. **Latency compensation.** Everything drawn against the audio (the cursor, highway, waveform,
   spectrogram and overview) is drawn at **`SmoothPosition` − latency × speed**
   ([§4.4.1](#441-playback-changes), [C11](#c11-calibration-tool)).
   - `Position` already allows for the audio waiting in SDL's stream queue.
   - The calibration covers everything after that: SDL's device buffer and the output device's
     own latency, which the app can't see.
   - The latency is in real time, so it is scaled by the playback speed to give song time.
6. **Following.** While playing, the view keeps the cursor at a fixed place, a quarter of the way
   from the strike end. Scrolling pauses following until playback is restarted.

Acceptance criteria:

- **AC C10.1** At 50% speed with pitch correction, metronome clicks in the mixed output stay within
  2 ms of the beat lines over a 5-minute song, measured against the song time `PositionMap` gives
  for each output frame.
- **AC C10.2** Changing the calibrated latency moves what is drawn against the audio by exactly
  that amount times the speed, and doesn't move the audio.

### C11. Calibration tool

A tool for measuring the audio/visual latency of the user's setup, so that what is seen and what is
heard line up while charting. **The display is the ground truth.** The audio is adjusted (in
practice, everything drawn is shifted against the audio, [C10](#c10-playback)) until it matches
the display.

**Input:** none.
**Output:** a latency in milliseconds for the current audio output device.

**Opening:**

The tool is part of Chart Builder. It opens:

- from Chart Builder's **Settings**;
- from Chart Builder's transport bar;
- from **Calibrate…** in Chart Packer's Settings, which runs `ChartBuilder --calibrate`
  ([§4.8](#48-launching-and-coordination));
- **automatically, once**, the first time Chart Builder opens on a device that has no calibration.
  This can be skipped.

**Behaviour:**

1. **Manual test.** This is the main test.
   - A loop at 100 BPM in 4/4. On every beat:
     - a **light flashes**: a large tile goes bright for 60 ms;
     - a **note scrolls down a small highway** and reaches the strike line;
     - a **beep plays**: a 1 kHz tone of 30 ms with 2 ms fades, which has a sharp start.
   - **The first beat of each bar is marked**: a 2 kHz beep, a differently coloured flash and a
     wider note. One beat lasts 600 ms, so without the mark a latency of 400 ms would look and
     sound the same as −200 ms. With it, the pattern repeats every 2.4 s, and the whole ±500 ms
     range can't be mistaken for anything else.
   - The beep is made by `Synth` and played through `StemPlayer`'s post-stretch stage and
     `IAudioOutput` ([§4.4.1](#441-playback-changes)), the same output path as a chart's stems, so
     it has exactly their queueing and device latency.
   - **One schedule.** The beats are scheduled on the app's monotonic clock.
     - The flash and the note are drawn at each beat's time.
     - Each beep is written into the output at the frame that `SmoothPosition` says will leave
       SDL's queue at the beat's time minus the current latency.
     - Changing the latency therefore **moves the beep, never the flash**.
   - The user adjusts the latency until the beep and the flash (or the note reaching the line)
     happen together. Controls:
     - a slider from −500 to +500 ms (Bluetooth output can be well over 200 ms late);
     - buttons for −10, −5, −1, +1, +5 and +10 ms;
     - the arrow keys (Shift for ×5);
     - a number box.

     The change is heard from the next beat that hasn't yet been queued (the queue holds about
     150 ms).
   - **Why the highway note as well as the light.** A moving object reaching a line is easier to
     judge than two instants. It is also what the user will be judging against while charting.
     The light is kept as the user asked, and either can be hidden.
2. **Tap test (optional).** This gives a measured starting value, which the manual test then fine
   tunes.
   - Both phases run at **60 BPM**, so a tap's nearest beat is unambiguous for any latency in the
     ±500 ms range.
   - **Visual phase.** The user taps in time with 16 flashes, with no sound.
   - **Audio phase.** The user taps in time with 16 beeps, with the screen blank.
   - Taps are mouse clicks or touches on the tile. (Not the Space key: see the timestamps below.)
   - The first 4 taps of each phase are warm-up and are ignored.
   - Each tap's error is measured against its nearest beat. Taps more than 1.5 interquartile
     ranges from the median are dropped.
   - **Latency = mean audio error − mean visual error**, over the taps kept. The user's reaction
     time and the mouse's latency are the same in both phases, so they cancel out. What's left is
     how much later the audio arrives than the display.
   - The result shows the estimate, its spread (the larger of the two phases' interquartile
     ranges), and how many taps were kept. **Use this value** puts it into the manual test to be
     checked. A spread over 30 ms suggests trying again.
   - **Tap times** come from the input events' own timestamps, not from when the UI thread handles
     them.
     - In Avalonia 12, pointer events carry a timestamp (`PointerEventArgs.Timestamp`) but key
       events don't. The raw input events that do (`RawInputEventArgs.Timestamp`) can't be reached
       from app code (`InputManager` and `RawKeyEventArgs`' members aren't public in 12.1), so the
       tap test takes pointer taps only.
     - A timestamp is on the OS input clock. It is moved onto the app's clock by subtracting, at
       handling time, how long the event waited: the input clock's current time minus the event's
       timestamp.
3. **Per device.**
   - Latency is stored **per audio output device**, because headphones, Bluetooth and speakers
     differ by hundreds of milliseconds.
   - **Device key.** A device is identified by a stable OS device ID where the platform gives one
     (for example, a Windows endpoint ID or a Core Audio device UID), and otherwise by its name.
     SDL's device names may change across reboots or docks, and its device handles only last for
     one session, so the ID is preferred when there is one.
   - `IAudioOutput` gains a `Device` (an `AudioDevice`: a name and an optional ID) and a
     `DeviceChanged` event, which `SdlAudioOutput` fills in. The name is SDL's. The ID needs a
     small platform call beside SDL: on Windows it is the default render endpoint's ID, from the
     MMDevice API (`WindowsAudioEndpoint`). Other platforms use the name alone for now.
   - **Which device.** The app has no device chooser. It always plays on the OS default device,
     and SDL moves the stream when the default changes, without telling the app. SDL's own device
     events only reach an SDL event loop, which the app doesn't run, and a silent move raises
     none. `SdlAudioOutput` therefore checks which device it is on when playback starts and once a
     second, and raises `DeviceChanged` when it differs. Chart Builder, the tool and Chart Packer
     then load that device's latency.
   - **Lookup order:** the stored value for the device's ID, then for its name, then a **global
     fallback**. When a device is found by name, its value is also saved under its ID if it has
     one, so a later rename doesn't lose it.
   - The tool always says which device it is calibrating.
4. **Where it applies.**
   - Chart Builder ([C10](#c10-playback));
   - **record mode**, where key presses are shifted by the latency before they're quantised
     ([C15](#c15-editing-ergonomics));
   - Chart Packer's inspector highway (`NoteHighwayView`) with its lane waveforms, lyrics lane and
     background, with the same scaling by speed. Chart Packer reads the value from the shared
     latency file ([§10](#10-settings)). The seek bar's waveform isn't moved: it marks what has
     played a column at a time, where tens of milliseconds don't show, and its position is the
     seek position.
5. **Saving.** **Save** stores the value in the shared latency file. **Cancel** restores the
   previous value. **Reset** sets 0.

*Settled in CM2:*

- **The tap test's estimate is a mean, not a median.** Once the outliers are dropped, about ten
  taps are left in each phase. With so few, the median's spread is too wide for AC C11.2: 92% of
  estimates fell within ±20 ms in its test. The mean gives 96.5%.
- **One schedule, re-anchored.** The beeps are a `CalibrationSchedule` over a silent mix. It ties
  the monotonic clock to `SmoothPosition` at an anchor, and is replaced when the latency changes
  and once a second, so the device's clock can't drift from the system's.
  - The schedules share a record of the beats already given (`CalibrationBeepsGiven`), so a
    re-anchor never plays a beep twice.
  - A latency raised past what's already queued skips one beep.
- **The first-use prompt** comes the first time a song plays in the Editor on a device with only
  the fallback. Each device is offered it once (`BuilderSettings.CalibrationPrompted`), whatever
  the answer.
- **The Settings rows.** The latency is an action-only row (`ActionSetting`), with a
  **Calibrate…** button, in both apps' Settings.

Acceptance criteria:

- **AC C11.1** With the latency set to +100 ms, the frames handed to `IAudioOutput` have the beep
  starting 100 ms earlier against the flash schedule than at 0 ms, to within one frame. The flash
  schedule is unchanged.
- **AC C11.2** The tap-test estimator is given simulated taps: a known audio offset, normal jitter
  with σ = 20 ms, and 2 outliers of ±150 ms per phase. Over 1,000 seeded trials, the median
  estimate is within ±2 ms of the offset, and 95% of estimates are within ±20 ms. (With 12 kept
  taps per phase, ±20 ms is about two standard deviations of the estimate.)
- **AC C11.3** When the output moves to another device (`DeviceChanged`), Chart Builder and the tool
  load that device's stored latency, or the fallback.
- **AC C11.4** With a latency of 400 ms, each marked (2 kHz) beep is written 400 ms before the
  marked beat it belongs to, so it can only be lined up with a marked flash.
- **AC C11.5** Cancel leaves the stored latency as it was before the tool opened.

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
   quiet stems. `StemPeaks` averages the channels today, so stereo needs per-channel peaks, which
   are new.

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

   **Limits of these settings.**
   - At 44.1 kHz the FFT's bins are 21.5 Hz apart. Below about 870 Hz, the log bins are narrower
     than that, so they repeat or interpolate a few FFT bins: the Kick band (30–120 Hz) shows
     about four distinct bands.
   - The 46 ms window also blurs the start of a hit by up to half its length, even though the
     columns are 5.8 ms apart.
   - The spectrogram is for seeing where a sound is, not for timing it. Onsets
     ([C14](#c14-onset-and-beat-assist)) are for timing.
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
   - For guitar and bass they come from the part's stem (`LaneStems`). For drums they come from the
     drum stems, split by band (kick, snare, cymbals), so each tick sits near the pad it most likely
     belongs to. When the part has no stem of its own, they come from the mix.
   - A **sensitivity** slider changes the threshold live.
2. **Snap to onset.** When on, placing or moving a note snaps to the nearest onset within half the
   current grid step. Otherwise it snaps to the grid as normal. The grid always wins when no onset
   is in range. A note snapped to an onset is usually off the grid, so CHR004 (Info) will list it.
   That is expected. When many notes need it, fixing the tempo map is usually the better cure.
3. **Nudge to onset** moves the selected notes to their nearest onsets, as one undoable command.
   It shows how far each note moves, and notes with no onset nearby stay where they are.
4. **Proposed tempo map.**
   - `TempoEstimator` proposes beat times for the whole song, or for a selection, with a
     confidence.
   - They are shown as a ghost tempo map over the real one.
   - The user can **accept** it (one undoable command that writes tempo markers, and anchors every
     *n* measures, default 4), **adjust** it (drag a proposed beat; the proposal updates around it),
     or **discard** it.
   - A low-confidence region (rubato, a breakdown) is marked so the author maps it by hand.
   - The estimator prefers tempos from 60 to 200 BPM. A faster song may be proposed at half its
     tempo, which the author can double with one click.
5. **First beat.** **Find first downbeat** proposes where measure 1 starts. This is often the
   hardest part of starting a chart. The author chooses how to apply it:
   - **Lead-in** (the default): `delay` is left alone, and the first tempo segment's BPM is set so
     that the next measure starts on the downbeat.
   - **Delay**: `delay` is changed so that tick 0 lands on the downbeat. This is shown as a pending
     change ([C17](#c17-save-and-export)).
6. Everything here is a hint. Nothing changes the chart without an explicit action, and every
   action can be undone.

Acceptance criteria:

- **AC C14.1** On synthetic click tracks at constant tempos from 60 to 200 BPM, the proposed tempo
  is within 0.1 BPM, and every proposed beat is within 10 ms of a click. From 200 to 240 BPM, the
  proposed tempo is within 0.1 BPM of the click tempo or half of it, and every proposed beat is
  still within 10 ms of a click.
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
   GHL by a lane mapping the user confirms. Drums paste only into drums of the same variant.
4. **Transforms** on the selection:
   - move by one grid step or one lane;
   - **lefty flip** (mirror the lanes);
   - shift all notes up or down one lane;
   - set or clear sustains, or set them to "until the next note";
   - change any flag;
   - stretch or compress the timing between two points (for fixing the double- or half-time
     sections common in imported charts). Passthrough tracks are re-ticked to match
     ([§4.2](#lossless-passthrough)).
5. **Record mode.**
   - While the song plays, number keys place notes on lanes at the moment they're pressed. Holding
     a key holds a sustain (on 5-fret and GHL).
   - Press times come from the input events' timestamps where they can be had, as in the tap test
     ([C11](#c11-calibration-tool)), and otherwise from when the event is handled.
   - Each press is converted to song time as **`SmoothPosition` − latency × speed** at the moment
     of the press ([§4.4.1](#441-playback-changes)). It is then quantised to the grid, or to
     onsets if snap to onset is on.
   - Each pass is one undoable command. Recording can overwrite the selected range or merge with
     what's there.
   - This works at any speed, so a fast passage can be recorded at 50%.
6. **Autosave and recovery.**
   - Every 2 minutes (configurable), and when playback stops, the model is written to a recovery
     file in the per-user config folder (`Recovery/`), never into the song. Nothing is written
     when nothing has changed since the last autosave.
   - **What it holds.** The model, as a `notes.mid` from `MidiChartWriter` (so anchors and
     `RawEvent`s come with it), plus the pending `song.ini` changes and a manifest. The manifest
     records the song's full path (folder or `.sng`), the size and modified time of its chart file
     when it was opened, and the autosave time.
   - **Which song it belongs to.** A recovery file is matched to a song by its full path. If the
     chart on disk has changed since the recovery file's manifest recorded it, recovery still
     offers it, but warns that the song has changed since.
   - Saving deletes it. Opening a song with a newer recovery file offers to restore it
     ([C2](#c2-open-and-import)).
7. **Closing** Chart Builder, or the app, with unsaved changes asks: Save, Discard or Cancel.
8. **Keyboard shortcuts** are listed in [§9](#9-keyboard-shortcuts).

Acceptance criteria:

- **AC C15.1** Any sequence of commands followed by the same number of undos gives a model equal to
  the start. Redoing them all gives a model equal to the end.
- **AC C15.2** Killing the app mid-edit and reopening the song offers the recovered chart, which
  includes every change up to the last autosave.
- **AC C15.3** In record mode with a latency of 40 ms, a key pressed when `SmoothPosition` is 40 ms
  past a beat (that is, exactly on the beat as the display shows it and the user hears it) is
  placed on that beat. At 50% speed, the same holds for a key pressed 20 ms of song time past the
  beat.

### C16. Chart lint

**Behaviour:**

1. **When it runs.** The rules in [§6](#lint-rules) run on a snapshot of the open chart in the
   background after each change. Each rule declares its **scope**, so it keeps up while typing:
   - **range** rules re-check only the ticks the change touched, widened to the neighbouring notes
     (the HOPO rules look one chord back and one forward);
   - **part** rules (such as CHR080 and CHR081) re-check the parts the change touched;
   - **song** rules (such as CHR003, CHR083 and CHR084), and every rule after a tempo-map edit,
     re-check the whole chart.
2. **How findings are shown.** Findings use the existing `Finding` type, with rule IDs `CHRnnn`
   and messages in `FindingMessages.resx`. They appear in the findings panel and as marks on the
   overview strip, and each one links to its place in the chart. `Finding` gains an optional chart
   location (part, difficulty and tick range) for that, beside its existing byte offset and
   metadata key.
3. **Quick fixes.** The existing `QuickFixRegistry` and `IQuickFixStrategy` only edit metadata
   (`QuickFix.ApplyTo(MetadataItem)`). Chart Builder adds a chart kind of fix, which proposes an
   `IChartCommand` for a finding, and registers chart fixes with the same registry. Each fix is
   one undoable command (see the table for which rules have one).
4. **Severity.**
   - Lint **never blocks saving**. A chart in progress must always be saveable.
   - **Errors** are things the author wrote that the games read differently, such as an open note
     in a chord (CHR022). The editor shows what the games read, so the read-back check
     ([§4.3](#43-writing-midichartwriter)) passes. In Chart Builder, Errors block **Save and pack**,
     the same way SNG errors block packing.
5. **In Chart Packer's library.** The rules live in Core, so Chart Packer runs them too.
   - Validating a song from the library also runs chart lint, so charts made outside Chart Builder
     get the same checks ([project-spec F3](project-spec-v1.md#f3-validate)).
   - **In the library, chart lint never blocks packing.** CHR findings of every severity are shown
     as findings, and packing goes ahead, so every song that packs today still packs.
   - A `.chart` song is linted by converting it with `ChartToMidi` in memory. A batch validation
     lints songs on the same worker threads as the rest of validation, and skips lint for songs
     whose chart is unchanged since it was last linted.
   - CHR004 uses only the common grids, never the editor's current snap, so it gives the same
     result in the library and in Chart Builder.

Acceptance criteria:

- **AC C16.1** Every CHR rule has a fixture that triggers it, and a clean fixture triggers none.
- **AC C16.2** Lint after a single-note edit on a 20,000-note chart finishes within 50 ms.

### C17. Save and export

**Behaviour:**

1. **Save** (Ctrl+S, on a snapshot of the model):
   1. writes `notes.mid` in memory with `MidiChartWriter` ([§4.3](#43-writing-midichartwriter));
   2. reads it back and compares it with the model. **A difference stops the save.**
   3. stages the bytes to a temp file and saves through `MetadataDraft`/`DraftWriter`
      ([§4.6](#46-saving)).
2. **`song.ini` changes.** Every change Chart Builder makes to `song.ini` is shown as a pending
   change in Chart Builder's **Pending changes** list (beside the findings panel) as soon as it is
   made, not only at save. Each change can be reverted there. Chart Packer's metadata editor
   doesn't see these changes until they are saved ([§4.6](#46-saving)). The full list:
   - `song_length`, when it is unset or no longer matches the audio's length within 1 s. This
     happens on save, so CHR084 only appears for charts linted outside Chart Builder;
   - `pro_drums` and `five_lane_drums`, for the drum variant: when the variant is chosen in Chart
     settings ([C1](#c1-new-chart-and-chart-settings)), switched
     ([C6](#c6-drums-4-lane-pro-and-5-lane)), or detected on load ([§4.1](#editable-model));
   - `charter`, if empty, from the **Default charter name** setting ([§10](#10-settings));
   - `diff_<part> = -1` for new parts ([§7.5](#75-songini-keys-chart-builder-writes)). The
     suggested rating ([C18](#c18-preview)) is offered, but is never written without the user's
     say-so;
   - `multiplier_note` removed, when the chart had star power on 103
     ([§7.2](#72-shared-markers-every-difficulty));
   - `delay`, from a `.chart` import's Offset ([C2](#c2-open-and-import)), or **Find first downbeat**
     ([C14](#c14-onset-and-beat-assist));
   - the `.chart`'s `[Song]` values, on import ([C2](#c2-open-and-import)).

   `sustain_cutoff_threshold` and the HOPO threshold are never changed by Chart Builder. Its display
   follows them.
3. **Save and pack** saves, then packs the song to a `.sng`
   ([project-spec F1](project-spec-v1.md#f1-pack-song-folder--sng)) with Core's packing
   (`PackPlanner`, `SongJob`). It uses Chart Packer's output folder and overwrite settings, which
   Chart Builder reads from Chart Packer's settings file and never writes.
4. **Export copy** writes the song with its draft applied to another folder, using the existing
   `SongExport`.

Acceptance criteria:

- **AC C17.1** A save that is interrupted (killed mid-write) leaves the previous `notes.mid` intact.
- **AC C17.2** For every fixture chart, reading the saved `notes.mid` with `ChartReader` gives the
  notes, flags and star power the editor shows.
- **AC C17.3** Saving never changes a `song.ini` key the user hasn't seen as a pending change.
- **AC C17.4** For every fixture chart, reading the saved `notes.mid` with `MidiChartLoader` gives
  back the model, including accents, ghosts, phrases, events, anchors and `RawEvent`s.

### C18. Preview

**Behaviour:**

1. **Highway preview.** A game-style view of the part and difficulty, using the existing
   `NoteHighwayView`, played in sync with the audio and with notes "hit" automatically. It can run
   docked beside the editor while editing, or full-screen.
   - `NoteHighwayView` draws a `HighwayScene`, which `HighwayViewModel` builds from a
     `ChartDocument`. The preview builds its scene from the model's game view
     ([§4.3](#43-writing-midichartwriter)), rebuilt for the changed range after each edit.
     Nothing is written to disk.
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

- **Error:** the author wrote something the games read differently. The editor shows what the games
  read, so the chart still saves ([§4.3](#43-writing-midichartwriter)). In Chart Builder, an Error
  blocks **Save and pack**, not Save. In the library, it blocks nothing
  ([C16](#c16-chart-lint)).
- **Warning:** likely a mistake, or likely to play badly.
- **Info:** for the author's awareness.

**Fix** means a quick fix exists. Rule IDs are **stable**. A rule may be retired, but its ID is
never reused.

### Timing

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR001 | Warning | No time signature at tick 0; the games assume 4/4 | Add 4/4 at 0 |
| CHR002 | Warning | A tempo below 20 or above 400 BPM (probably a typo or a half/double-time mistake) | — |
| CHR003 | Warning | A note, phrase or event after the end of the audio (in audio time, [§4.4.1](#441-playback-changes)) | — |
| CHR004 | Info | A note isn't on any common grid: a whole-note division from 1/1 to 1/192, or a triplet one | Snap to grid |
| CHR005 | Warning | Two tempo markers or two time signatures on the same tick | Keep the later one |

### 5-fret and GHL

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR020 | Warning | A sustain runs into the next note on its lane, so the games will cut it short | Trim sustain |
| CHR021 | Info | A sustain is shorter than the sustain cutoff, so the games read it as a plain note | Remove sustain |
| CHR022 | Error | An open note in a chord with frets | Remove open |
| CHR023 | Info | A force HOPO or force strum that doesn't change the chord | Remove force |
| CHR024 | Warning | A forced HOPO on a chord that repeats the previous note or chord exactly. The games play it as a HOPO, but the same frets can't be hammered on again. | Remove force |
| CHR025 | Warning | The first note of a part is a HOPO (forced or tap) | Remove force |
| CHR026 | Warning | Two notes on the same lane closer than 1/64 of a whole note in ticks (likely a stray duplicate) | Delete the later one |

### Drums

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR040 | Info | A tom marker that covers no note | Remove marker |
| CHR041 | Warning | More than two pads at once (not counting the kick): more hands than a drummer has | — |
| CHR042 | Warning | A drum fill overlaps a star power phrase (not counting a big rock ending) | — |
| CHR043 | Warning | A drum fill whose last tick has no note on some difficulty, so there is nothing to activate on there (not counting a big rock ending) | Move end to last note |
| CHR044 | Warning | A roll or trill phrase with fewer than two notes, or a trill covering only one pad | — |
| CHR045 | Warning | Tom markers on a song whose `pro_drums` is `False`, so they will be ignored | Set `pro_drums = True` |
| CHR046 | Warning | Orange notes on a 4-lane or Pro song | — |
| CHR047 | Info | An accent or ghost on a kick, which some games ignore | — |
| CHR048 | Warning | The chart has a `PART DRUMS_2X` track. It is kept as it is and doesn't follow edits to `PART DRUMS`. | — |

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
| CHR084 | Warning | `song_length` doesn't match the audio's length within 1 s. A charter save corrects it, so this mostly appears in the library. | Update `song_length` |
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

Force HOPO, force strum and tap mark every note on the ticks they cover, so they act per chord
([§4.1](#editable-model)). Note velocity is 100, except accents (127) and ghosts (1) on a drums
track with `[ENABLE_CHART_DYNAMICS]`.

### 7.2 Shared markers (every difficulty)

| Pitch | Meaning |
| ----: | ------- |
| 103 | Solo |
| 104 | Tap (5-fret and GHL). Read; the writer writes taps as sysex instead. |
| 110, 111, 112 | Pro drums tom markers: yellow, blue, green |
| 116 | Star power |
| 120–124 | Drums: fill / activation lane, or a big rock ending after `[coda]`. Guitar and bass: big rock ending, when all five are held together |
| 126 | Drums: roll (single lane) |
| 127 | Drums: trill (two lanes) |

These pitches on a part track are kept as `RawEvent`s ([§4.1](#editable-model)): Chart Builder
doesn't edit them, but writes them back unchanged.

| Pitch | Meaning |
| ----: | ------- |
| 105, 106 | Face-off sections for player 1 and player 2 (`ChartToMidi` writes them from `.chart` `S 0`/`S 1`) |
| 125 | Drums: a lane marker `ChartToMidi` writes from `.chart` `S 67` |
| Anything else | For example Rock Band animation notes. Kept as it is. |

A song whose `multiplier_note` is 103 keeps its star power on 103 instead of 116, and has no solos
(`ChartReadOptions.MultiplierNote`). Chart Builder reads such charts as they are, and always writes
star power on 116 and solos on 103, clearing `multiplier_note` (shown as a pending change).

A roll or trill reaches Expert only, unless its velocity extends it to lower difficulties: 21–30
down to Easy, 31–40 down to Medium, 41–50 down to Hard. The writer uses 25, 35, 45 or 100, as
`ChartToMidi` already does.

### 7.3 Text events

| Event | Where | Meaning |
| ----- | ----- | ------- |
| `[ENHANCED_OPENS]` | part track, tick 0 | Offset −1 notes are opens |
| `[ENABLE_CHART_DYNAMICS]` | drums track, tick 0 | Velocity 127 = accent, 1 = ghost |
| `[mix <d> drums<n>]`, `[mix <d> drums<n>d]` | drums track | Drum mix per difficulty; the `d` suffix is disco flip (parsed by `ChartTextEvents.TryParseMix`) |
| `[section <name>]` / `[prc_<name>]` | `EVENTS` | Practice sections |
| `[music_start]`, `[music_end]`, `[coda]`, `[end]` | `EVENTS` | Song events |
| `[chartpacker_anchor <µs>]` | tempo track, at the anchored marker's tick | A tempo anchor ([C4](#c4-tempo-map-and-grid)). The number is the anchor's audio time in whole microseconds. The games ignore it. |

### 7.4 Tracks the writer creates

The tempo track, each part (`PART GUITAR`, `PART BASS`, `PART RHYTHM`, `PART GUITAR COOP`,
`PART DRUMS`, `PART GUITAR GHL`, `PART BASS GHL`, `PART RHYTHM GHL`, `PART GUITAR COOP GHL`),
`EVENTS` and `BEAT` (pitch 12 on each measure's downbeat and 13 on other beats). Everything else is
passed through ([§4.2](#lossless-passthrough)), including:

- `PART KEYS`, which `MidiChartReader` reads as a 5-fret part, but which Chart Builder doesn't edit
  in v1;
- `PART DRUMS_2X`, which the app's reader doesn't recognise, and which doesn't follow edits to
  `PART DRUMS` (CHR048).

A legacy `T1 GEMS` or `PART REAL_DRUMS_PS` track is read as its part, and saved under the current
name. If a file has two tracks for one part, the one `MidiChartReader` keeps is edited and the
other is passed through.

### 7.5 `song.ini` keys Chart Builder writes

Each part's difficulty rating, from the app's `MidiPartCatalog`. `-1` means "not rated", the same
convention `MetadataDifficulty` already uses.

| Part | Key |
| ---- | --- |
| Lead, Bass, Rhythm, Co-op | `diff_guitar`, `diff_bass`, `diff_rhythm`, `diff_guitar_coop` |
| GHL Guitar, Bass, Rhythm, Co-op | `diff_guitarghl`, `diff_bassghl`, `diff_rhythm_ghl`, `diff_guitar_coop_ghl` |
| Drums (4-lane, 5-lane) | `diff_drums` |
| Drums (Pro) | `diff_drums` and `diff_drums_real` |

The other keys Chart Builder writes are listed in [C17](#c17-save-and-export).

### 7.6 Drum layout conversion

The mapping the conversion helper ([C6](#c6-drums-4-lane-pro-and-5-lane)) uses. Kick and 2× kick
are unchanged, and red stays red.

| 5-lane | → Pro |
| ------ | ----- |
| Yellow (cymbal) | Yellow cymbal |
| Blue (tom) | Blue tom |
| Orange (cymbal) | Green cymbal |
| Green (tom) | Green tom |

| Pro | → 5-lane |
| --- | -------- |
| Yellow cymbal | Yellow |
| Yellow tom | Blue |
| Blue cymbal | Orange |
| Blue tom | Blue |
| Green cymbal | Orange |
| Green tom | Green |

- Converting 5-lane → Pro → 5-lane gives the original notes back, except on a tick that has both
  orange and green: both land on Pro green, and one is left out and reported.
- Going the other way, Pro → 5-lane can put two notes on blue (yellow tom and blue tom) or two on
  orange (blue cymbal and green cymbal); one of each pair is left out and reported.
- Plain 4-lane converts as Pro, using its tom markers as they stand
  ([C6](#c6-drums-4-lane-pro-and-5-lane)).

---

## 8. UI outline

Chart Builder has two top-level windows, and only one is open at a time. The launcher (the home
screen) shows until a chart is created or opened. Then the Editor window takes its place, with the
editor's tools in its toolbar, and closing the Editor brings the launcher back. New chart's Chart
settings is a dialog over the launcher, so the launcher stays until Create.

**Launcher (home screen):**

```txt
┌ Chart Builder ─────────────────────────────────────────────────────────┐
│                                                                         │
│              ┌────────────┐  ┌────────────┐  ┌────────────┐             │
│              │   (icon)   │  │   (icon)   │  │   (icon)   │             │
│              │ New chart  │  │ Open chart │  │  Settings  │             │
│              └────────────┘  └────────────┘  └────────────┘             │
│                                                                         │
│   Recent                                                                │
│   ┌───────────────────────────────────────────────────────────────────┐ │
│   │ Artist – Title            D:\Songs\Artist - Title          today  │ │
│   │ Other Artist – Song       D:\Songs\Other.sng           yesterday  │ │
│   └───────────────────────────────────────────────────────────────────┘ │
│                                                                         │
│   Drop audio files to start a new chart, or a song to open it.          │
└─────────────────────────────────────────────────────────────────────────┘
```

**Editor window:**

```txt
┌ Untitled chart - Chart Builder ───────────────────────────────────────────────────────┐
│ [◎ Chart Builder v0.3.0] [⚙ ◉] [Select|Place|Erase|Sustain|Phrase|Tempo] [⟲ ⟳]        │
├─────────────────────┬────────────────────────────────────────────┬────────────────────┤
│ Chart Inspector   « │ Guitar·X ▾ + ✕┃Bass·X ▾ + ✕┃Drums·X ▾ + ✕  │ Properties       » │
│                     │              ┃             ┃               │                    │
│ Artist – Title      │   highway    ┃   highway   ┃   highway     │ Note  m41 · beat 3 │
│ [album art]         │              ┃             ┃               │ Tick  78,960       │
│ Tracks              │              ┃             ┃               │ …                  │
│ Chart files         ├━━━━━━━━━━━━━━┻━━━━━━━━━━━━━┻━━━━━━━━━━━━━━━┤                    │
│ Other files         │ Vocals ▾                              + ✕  │                    │
│ Findings            │   vocals                                   │                    │
│ Metadata            │                                            │                    │
╞═════════════════════╧════════════════════════════════════════════╧════════════════════╡
│ Timeline                                                              1:38.4 / 4:12.0 │
│ 0:00      0:30      1:00      1:30   │  2:00      2:30      3:00      3:30      4:00  │
│ ▁▂▅▇█▇▅▃▂▁▂▃▅▇▅▃▁▂▃▅▇█▇▅▃▂▁▂▃▅▇▅▃▂▁▂│▅▇█▇▅▃▂▁▂▃▅▇▅▃▁▂▃▅▇█▇▅▃▂▁   notes over the mix   │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- The ⚙ menu has **Chart settings** and **Program settings**. The ◉ (View) menu will choose which
  lanes are shown; it is a placeholder so far. The toolbar's groups wrap onto more rows as the
  window narrows.
- The middle shows **lanes**: each a part's highway at one difficulty, or a vocal part. They are
  docked like Visual Studio's tool windows: dragging a lane's header shows guides on the lane under
  the pointer and on the edges of the whole area, and dropping on one docks the lane against that
  edge. A lane's highway is always vertical; only the room it takes is tall or wide. ━ splitters
  resize the lanes. Each lane's ▾ menu switches its part (each part is shown at most once), + adds a
  lane (a highway to the right of the last one, a vocal part the full width along the bottom), and ✕
  closes it. The lanes, where they are and their sizes are saved (`LaneDockLayout`, in
  `ChartPacker.Core/Charting/Layout/`).
- « and » collapse a panel to a strip along that edge, with its tab titles turned on their side.
  Clicking a title brings the panel back. The panel widths, whether each is collapsed, and the
  timeline's height (the ═ splitter above it) are saved.
- Dragging or clicking on the timeline scrubs the playhead.

The two mock-ups below show the canvas in each view. They were drawn before the Editor window's
layout, so their part picker, findings and transport bar have still to be placed in it.

**Highway view:**

```txt
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ← Close     Artist – Title *    [Highway|Timeline]  Snap [1/16 ▾]  ⟲ ⟳   [Save ▾]    │
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

**Chart settings, New chart mode** (drawn as one page; the list on the left jumps to each
section):

```txt
┌ New chart ───────────────────────────────────────────────────────────────────┐
│                                                                              │
│ Song     │ Name         [ Through the Fire                ]                  │
│ Audio    │ Artist       [ Example Band                    ]                  │
│ Timing   │ Charter      [ Me                              ]                  │
│ Parts    │                                                                   │
│ Location │ Guitar.ogg   [ guitar ▾ ]  ✕                                      │
│          │ mix.mp3      [ song   ▾ ]  ✕                                      │
│          │ [Add files…]                                                      │
│          │                                                                   │
│          │ Resolution [ 480 ▾ ]   BPM [ 120.000 ]   Time sig. [ 4 ]/[ 4 ▾ ]  │
│          │                                                                   │
│          │ ☑ Lead  ☐ Bass  ☐ Rhythm  ☐ Co-op  ☐ GHL …  ☑ Drums [ Pro ▾ ]     │
│          │                                                                   │
│          │ Location     D:\Songs   [Browse…]                                 │
│          │ Folder name  [ Example Band - Through the Fire ]                  │
│          │                                                                   │
│          │                                         [Cancel]  [Create]        │
└──────────────────────────────────────────────────────────────────────────────┘
```

**Chart settings on an open song:**

```txt
┌ Chart settings ──────────────────────────────────────────────────────────────┐
│                                                                              │
│ Timing   │ Resolution       [ 480 ▾ ]                                        │
│ Parts    │ Starting BPM     [ 120.000 ]                                      │
│          │ Time signature   [ 4 ] / [ 4 ▾ ]                                  │
│          │                                                                   │
│          │ ☑ Lead        ☐ Bass        ☐ Rhythm        ☐ Co-op               │
│          │ ☐ GHL Guitar  ☐ GHL Bass    ☐ GHL Rhythm    ☐ GHL Co-op           │
│          │ ☑ Drums  [ Pro ▾ ]                                                │
│          │                                                                   │
│          │                                             [Cancel]  [OK]        │
└──────────────────────────────────────────────────────────────────────────────┘
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

These are the defaults. Rebinding is future work.

- Shortcuts act on the focused editor canvas. Tab is never taken, so focus can always move out of
  the canvas.
- Letter, digit and bracket keys are matched by **physical key** (Avalonia's `PhysicalKey`), so
  they sit in the same place on every keyboard layout.
- On macOS, `Ctrl` in this table is `Cmd`.

| Keys | Action |
| ---- | ------ |
| `1`–`5` (`1`–`6` GHL) | Place or remove a note on that lane at the cursor; in record mode, play a note |
| `0` | Open note, or drum kick |
| `Shift`+`0` | 2× kick (Expert drums) |
| `Space` | Play or pause from the cursor |
| `Shift`+`Space` | Play the selection |
| `↑` / `↓` (Highway), `→` / `←` (Timeline) | Move the cursor one grid step later or earlier |
| `Alt`+arrows | Move the selection one grid step in time, or one lane across |
| `PgUp` / `PgDn` | Move the cursor one measure |
| `Ctrl`+`↑` / `Ctrl`+`↓` | Previous or next section |
| `[` / `]` | Coarser or finer snap |
| `O` | Snap to onset on or off |
| `B` | Tap tempo |
| `Ctrl`+wheel, `+` / `−` | Zoom |
| `Ctrl`+`1`–`4` | Easy to Expert |
| `V` | Switch between Highway and Timeline |
| `H` / `G` / `T` | Toggle force HOPO, force strum, tap on the selected chords |
| `C` | Toggle cymbal or tom (Pro drums, every difficulty at that tick) |
| `A` / `Shift`+`A` | Toggle accent or ghost (drums) |
| `S` | Star power phrase over the selection |
| `L` | Set the loop region to the selection |
| `R` | Start or stop record mode |
| `W` | Switch between waveform and spectrogram |
| `Ctrl`+`A` | Select all in the current difficulty |
| `Ctrl`+`Z`; `Ctrl`+`Y` or `Ctrl`+`Shift`+`Z` | Undo; redo |
| `Ctrl`+`X` / `C` / `V` | Cut, copy, paste |
| `Delete` | Delete the selection |
| `Ctrl`+`S` | Save |

## 10. Settings

Each app owns its settings file, and every file is in the per-user config folder
(`UserConfigPaths.ConfigFolder`). Both apps read and write theirs through Core's
`JsonSettingsFile<T>`. Several processes can share a file (several Chart Builders, say), so a save
re-reads the file and replaces only the settings this process changed since it last loaded or saved.
Other processes' changes to the other settings, and keys this version doesn't know, are kept. Each
save writes its own temp file and renames it into place.

| File | Owner | Holds |
| ---- | ----- | ----- |
| `settings.json` | Chart Packer (`AppSettings`) | Chart Packer's settings, as now. Chart Builder reads the output folder and overwrite policy from it for **Save and pack** ([C17](#c17-save-and-export)), and never writes it. |
| `builder-settings.json` | Chart Builder (`BuilderSettings`) | Every setting in the table below except the latency, plus the Editor window's layout (each side panel's width and whether it's collapsed, and the timeline's height), the recent songs, and the last parent folder used for a new chart |
| `audio-latency.json` | Chart Builder's calibration tool | The audio/visual latency. Chart Packer reads it for the inspector ([C11](#c11-calibration-tool)), and re-reads it when its window is activated ([§4.8](#48-launching-and-coordination)). |

Settings are edited in Chart Builder's own Settings window, which uses the shared settings window
and the Core `SettingDefinition` catalog that Chart Packer's uses. The catalog can express Toggle,
Integer, Range, Choice, Folder and Text rows. The latency map and the reduction rules aren't rows:
they are edited in the calibration tool and the Reduce dialog, and Settings shows a button that
opens each. Chart Packer's Settings shows a **Calibrate…** button that runs
`ChartBuilder --calibrate`.

| Setting | Default |
| ------- | ------- |
| Default resolution for new charts | 480 |
| Default snap | 1/16 |
| Default editor view | Highway |
| Editor highway speed | The inspector's default `HighwayNoteSpeed` |
| Notes keep ticks or times when the tempo changes | Keep ticks |
| Waveform or spectrogram behind the lanes | Waveform |
| Spectrogram memory budget | 256 MB |
| Metronome volume / hit-sound volume | 50% / 50% |
| Audio/visual latency | Per output device, plus a global fallback of 0 ms ([C11](#c11-calibration-tool)) |
| Prompt for calibration on first editor use | On |
| Autosave interval | 2 minutes |
| Default charter name | Empty. Prefills `charter` for a new chart, and fills an empty `charter` on save |
| Default difficulty reduction rules | As in [C9](#c9-difficulty-authoring). The Reduce dialog starts from them, and its changes apply to one run only. |

There is no existing playback-offset setting in `AppSettings` to extend. The latency is new. It is
stored in `audio-latency.json` as two maps, from device ID and from device name to whole
milliseconds, plus the fallback (`{ "byId": { … }, "byName": { … }, "fallback": 0 }`). It is
looked up in that order ([C11](#c11-calibration-tool)). Values are kept within ±500 ms, and a save
changes only the device saved, keeping the rest of the file (`AudioLatencyFile`). The file is
written to a temp file and renamed into place, as `settings.json` is, so a reader never sees half
of it.

The new-chart settings (default charter name and default resolution) moved from `AppSettings` to
`BuilderSettings` when new charts moved to Chart Builder, before any release included them, so
nothing is migrated. The last parent folder used for a new chart, which Chart settings starts from
([C1](#c1-new-chart-and-chart-settings)), is kept in `builder-settings.json` beside the window
layout. It isn't a row in Settings.

## 11. Non-functional requirements

| Area | Requirement |
| ---- | ----------- |
| Rendering | 60 fps while scrolling and playing a 10-minute chart with 20,000 notes per difficulty. Only the visible range is drawn. |
| Responsiveness | Spectrogram, onsets, tempo estimation, lint, autosave and saving run off the UI thread, on snapshots of the model ([§4.1](#editable-model)). The UI thread is never blocked for more than 16 ms while editing, nor 100 ms at any time ([project-spec §9](project-spec-v1.md#9-non-functional-requirements)). |
| Memory | The spectrogram stays within its budget and is never written to disk. The undo stack keeps every step but stores changes, not copies of the chart, and stays under 64 MB for a session of 10,000 edits. |
| Timing accuracy | What is drawn against the audio is within 1 ms of `SmoothPosition` − latency × speed, before display refresh ([§4.4.1](#441-playback-changes)). Metronome clicks and hit sounds are mixed after the time-stretcher and are sample-accurate in the mix. |
| Durability | A `.sng` save is atomic (the `.sng` rewrite). A song folder save is atomic per file (temp file and rename), and moves new files in before removing old ones ([§4.6](#46-saving)). Autosave never writes into the song. |
| Robustness | A malformed `notes.mid` opens as far as it can, with findings (in CM1, `ChartLoadIssue`s, [§4.1](#editable-model)). It never crashes the app. Unknown tracks and events are kept. |
| AOT | Zero trim and AOT warnings. No reflection, and no new packages apart from Serilog ([§4.7](#47-dependencies)). |
| Privacy | No network access, as now. |

## 12. Testing strategy

| Layer | Tests |
| ----- | ----- |
| Model ↔ MIDI | Round trips (load → save → load gives an equal model, AC C17.4) for golden `.mid` fixtures covering every part, variant and note kind in [§7](#midi-layout), with anchors, `RawEvent`s and velocities. Passthrough tracks stay byte-identical, and are re-ticked correctly after a keep-times tempo edit. The fixtures are written for these tests, not taken from community charts. |
| Writer vs reader | Every saved chart is read by `ChartReader` and compared with the model's game view (AC C17.2), over all the fixtures and every HOPO-threshold setting |
| Saving | The folder save's new order (move in, then delete), with a failure injected between the steps (AC C2.4) |
| Playback | Clicks in the post-stretch mix against `PositionMap` at several speeds (AC C10.1); `SmoothPosition` against a fake output whose queue drains in steps; latency scaled by speed (AC C10.2, C15.3) |
| `.chart` import | Every `.chart` fixture in the existing `ChartToMidi` tests imports with the same warnings |
| Commands | Each command reverses exactly; random command sequences, undone, give back the start (AC C15.1) |
| Tempo map | Tick ↔ seconds property tests against `TempoMap`; anchors stay put under edits (AC C4.1) |
| Lint | At least one fixture per CHR rule, and a clean fixture triggers none (AC C16.1) |
| Analysis | Synthetic click tracks at fixed and changing tempos for `TempoEstimator`; synthetic drum hits in separate bands for `OnsetDetector`; spectrogram bins checked against known sine frequencies |
| Calibration | The tap-test estimator with simulated taps that have a known offset, jitter and outliers (AC C11.2); the beep's position in the mixed output for a given latency (AC C11.1) |
| Reduction | Each rule in [C9](#c9-difficulty-authoring), on fixtures |
| View models | `ChartEditorViewModel`, the Editor window's and calibration view models against fake services. There are no automated UI tests, as in v1. |
| Two apps | `BuilderArgs` parsing of every form in [§4.8](#48-launching-and-coordination), including bad arguments; `SongLock` held and refused, with two `FileStream`s in one test process standing in for two processes, and released when the holder is disposed (AC C2.5); `JsonSettingsFile` keeping two processes' changes to different settings, and keys it doesn't know |

**Test isolation** is as in [project-spec §10](project-spec-v1.md#10-testing-strategy). Tests use
temp folders only. The recovery folder, the lock folder, the latency file and Chart Builder's
settings file take explicit paths, which keep tests away from the real config folder. `UserConfigGuard`
fails the run if anything there changes anyway.

## 13. Milestones

| Milestone | Contents | Exit criteria |
| --------- | -------- | ------------- |
| **CM0: Split** | `Directory.Build.props` (the version) and `Directory.Packages.props` (central package management). The `ChartPacker.UI` library, with the theme, dialogs, pickers and settings window. The `ChartBuilder` app: home screen, `BuilderArgs`, `BuilderSettings`, and the C1 wizard moved from Chart Packer as it is. `JsonSettingsFile<T>`, shared by both apps' settings. Chart Packer's **New chart** is removed, and its **Edit chart** button (one selected song, no job running) is added, disabled until CM2. `publish.ps1` publishes both apps into one folder, the installer installs both, and the release workflow reads the version from `Directory.Build.props`. | Both apps build and publish with zero AOT warnings, into one folder with one copy of each native library; the installer upgrades an existing install and adds both Start menu entries; the wizard opens from Chart Builder's home screen and from dropped audio files |
| **CM1: Model, writer and lint engine** | `EditableChart` and friends, `MidiChartLoader`, `MidiChartWriter`, passthrough and re-ticking, the game-view projection and extended read-back check, `MidiChartReader` reading dynamics, commands and undo. The lint engine: rule scopes, `Finding`'s chart location, and chart quick fixes. The `SongFolderFiles` order change. And the tests. | Every round-trip and writer-vs-reader test passes, and AC C2.4; zero AOT warnings |
| **CM2: Calibration, new chart, 5-fret editing** | The playback changes ([§4.4.1](#441-playback-changes)) and `Synth`, C11 calibration and the shared latency file, C1 new chart and Chart settings (without **Detect from audio**), C2 open and import, with **Edit chart** turned on, `SongLock` and Chart Packer's refresh ([§4.8](#48-launching-and-coordination)), and the player and highway view models moved to `ChartPacker.UI`, C3 both views, C4 tempo map and grid, C5 5-fret, C10 playback, C15 autosave and recovery, C17 save, and the timing, 5-fret and song lint rules. Calibration comes first because every timing judgement after it depends on it. | AC C1, C2, C3, C4, C5, C10, C11, C15.2 and C17 pass |
| **CM3: Drums and GHL** | C6 all three drum variants and conversion, C7 GHL, C8 phrases and events, and the drums and phrase lint rules | AC C6, C7 and C8 pass |
| **CM4: Audio assist** | C12 waveform, C13 spectrogram, C14 onsets and tempo estimation, and new chart's **Detect from audio** | AC C12, C13 and C14 pass |
| **CM5: Authoring tools** | C9 reduction, C15 record mode, clipboard and transforms, C16 lint in the library, C18 preview and suggested difficulty | AC C9, C15, C16 and C18 pass |

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
  library and inspector as well as Chart Builder.

## 15. Open questions

Decisions already made for v1, recorded here so they aren't reopened by accident:

- **Format:** Chart Builder saves `notes.mid` only, and `.chart` is import only.
- **Placement:** a separate app, Chart Builder, installed with Chart Packer by the same installer.
  The two share code (`ChartPacker.Core`, `.Audio`, `.Video`, `.UI`) but no process. They
  coordinate through command-line launches, a per-song lock file, and Chart Packer re-reading the
  songs Chart Builder changed. There is no IPC ([§4.8](#48-launching-and-coordination)).
- **Drafts:** each app keeps its own. Chart Packer asks to save or discard a song's drafts before
  opening it in Chart Builder ([§4.6](#46-saving)).
- **Settings:** each app writes only its own file. The latency is a shared file that only Chart
  Builder writes ([§10](#10-settings)).
- **New chart:** part of Chart Builder only. The wizard that moved from Chart Packer has been
  removed. New chart opens **Chart settings** over the launcher, and **Create** writes the song
  folder before the Editor opens it ([C1](#c1-new-chart-and-chart-settings)). Chart Packer has no
  **New chart**. Its only way into Chart Builder is **Edit chart**, for exactly one selected song.
- **Chart settings and the Chart Inspector:** Chart settings holds only the chart's timing and
  parts, plus, for a new chart, the name, artist, charter, audio and location the folder needs. Every
  other `song.ini` key, the album art, the stems and `delay` are edited in the Chart Inspector.
- **Changing the resolution:** allowed at any time. It re-ticks the chart keeping times, and warns
  first when anything would move in time or be left out ([C1](#c1-new-chart-and-chart-settings)).
- **Windows:** the launcher and the Editor are separate windows, and only one is open at a time
  ([§4.5](#45-app-chartbuilder)).
- **Views:** Highway and Timeline, both in v1.
- **Spectrogram:** held in memory only, within a budget.
- **4-lane ↔ 5-lane:** a fixed mapping of Chart Builder's own ([§7.6](#76-drum-layout-conversion)),
  close to what the games do but chosen to round-trip, because Clone Hero and YARG don't share one
  mapping.
- **Resolution:** 480 for new charts, and 192 or 960 by choice.
- **Anchors:** saved as `[chartpacker_anchor <µs>]` text events on the tempo track
  ([§7.3](#73-text-events)).
- **Passthrough under keep-times edits:** passthrough tracks are re-ticked so they keep their
  times. Byte-for-byte passthrough holds only while the tempo map is unchanged in that way
  ([§4.2](#lossless-passthrough)).
- **Chart lint in the library:** shown as findings, never blocks packing
  ([C16](#c16-chart-lint)).
- **Reduction rules:** set per run in the Reduce dialog, starting from defaults in Settings. Nothing
  is stored per song ([C9](#c9-difficulty-authoring)).
- **Calibration device key:** a stable OS device ID where there is one, then the device name, then
  the global fallback ([C11](#c11-calibration-tool)).
- **`.chart` after import:** removed on the first save, after a confirmation. No backup is kept
  ([C2](#c2-open-and-import)).
- **Suggested difficulty:** shown in Chart Builder only, with fixed hand-set weights in v1. Tuning and
  showing it in the library are future work ([C18](#c18-preview)).
- **Generated drum fills:** the author picks the spacing, length and ending note on each run
  ([C6](#c6-drums-4-lane-pro-and-5-lane)).

There are no open questions at present.
