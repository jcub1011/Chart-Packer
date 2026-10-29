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

- **Vocal percussion, overdrive and lyric shifts.** The editor's vocal lanes edit the notes, lyrics
  and phrases of `PART VOCALS` and `HARM1`–`HARM3` ([C3](#c3-editor-layout) item 7). Their
  percussion notes (96, 97), overdrive (116), lyric and range shifts and any other events are kept
  as they are ([§4.2](#lossless-passthrough)), and a `.chart`'s lyrics are imported only. The
  existing vocals tools (`VocalsEditor`, lyrics lane) are unchanged.
- **Pro keys, pro guitar/bass, dance, Elite drums.** Their tracks are kept as they are. (5-lane
  `PART KEYS` is edited as a 5-fret part, *revised for #116*.)
- **Venue, lighting, camera and crowd events.** Kept as they are.
- **Writing `.chart`.** `.chart` is imported only ([C2](#c2-open-and-import)).
- **Auto-charting.** No notes are generated from audio. Onsets and beats are shown as hints, and
  only the user turns them into notes or tempo markers ([C14](#c14-onset-and-beat-assist)).
- **Audio editing or encoding.** Stems are added, replaced and removed as files, as in
  [project-spec F7](project-spec-v1.md#f7-audio-playback), but never cut, mixed down or
  re-encoded.
- **A game mode** (score, star power, failing, multiplayer). Previewing uses autoplay
  ([C18](#c18-preview)). Playtesting in the editor judges hits against hit windows to check a chart
  plays right, and stops there ([C19](#c19-playtest-and-instrument-input)).

## 3. Users and scope

| | |
| --- | --- |
| **Users** | Chart authors, from first-time charters to people moving from Moonscraper or Editor on Fire |
| **Platforms** | As the app: Windows, macOS and Linux, Native AOT ([project-spec §3](project-spec-v1.md#3-target-users-and-platforms)). MIDI devices as instruments are Windows only for now ([C19](#c19-playtest-and-instrument-input)). |

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
├── ChartBuilder/                 Chart Builder app (new): launcher (home screen), Editor window, canvases
├── ChartBuilder.Devices/         Instrument devices (new, C19): SDL3 game controllers, and MIDI input on Windows (winmm)
├── ChartShared.UI/               Shared Avalonia library (new): theme, icons, WindowChrome, dialogs, pickers,
│                                 settings window, shared controls, and the drawing of notes and waveforms
├── ChartShared.Audio/            + Analysis/ (spectrogram, onsets, tempo estimation), + Synthesis/ (`Synth`: click, beep, hit sounds)
├── ChartShared.Core/             + Charting/ (editable model, commands, MIDI chart writer, lint, reductions, playtest), + SongLock,
│                                 + Input/ (bindings, routing and timing of instrument input, C19)
└── ChartShared.Core.Tests/       + Charting/, Audio/Analysis/ tests
```

Chart Packer and Chart Builder are two executables. They are built against the same package
versions (central package management in `Directory.Packages.props`), so they can share one install
folder and one copy of each native library (SDL3, Skia, HarfBuzz, ANGLE and FFmpeg). One installer
installs both ([project-spec §4](project-spec-v1.md#4-architecture)).

Core stays UI-free and Avalonia-free, as now
([project-spec §4.1](project-spec-v1.md#41-chartsharedcore)). Chart Builder's model, commands, writer
and lint all live in Core, so Chart Packer (for lint in the library, [C16](#c16-chart-lint)) and a
later CLI can use them. UI code that both apps need lives in `ChartShared.UI`, and moves there only
when the second app first needs it.

<a id="editable-model"></a>

### 4.1 Editable model (`ChartShared.Core/Charting/`)

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
- each note's exact sustain length, except that a hold under the 10-tick floor is a plain note. The
  song's cutoff is not applied to the model;
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
  CM2:* the Editor says how many there are when the chart opens, and lists each one under
  Findings as CHR086 ([§6](#lint-rules)) until a save writes the file without it. They aren't
  lint rules: they describe the file as loaded, so they aren't checked again as the chart is
  edited, and their ticks are the file's.

**Commands and undo.** Every change is an `IChartCommand` with `Apply` and `Revert` (add notes,
delete notes, move, change sustain, toggle flags, add phrase, edit tempo…). A composite command
groups the changes of one gesture, so a drag or a paste undoes in one step. Commands store changes,
not copies of the chart. The undo stack keeps every step of the session; the budget in
[§11](#11-non-functional-requirements) is a size target for that, not a limit that drops steps.
*Settled in CM2:* a drag applies its command to the chart as it goes, and is put on the undo stack
once, when it ends. Undo during a drag takes the drag back first.

**Snapshots.** Work off the UI thread (save, lint, autosave) runs on an immutable snapshot of the
model taken when the work starts, so editing never waits for it and it never sees a half-applied
command.

<a id="lossless-passthrough"></a>

### 4.2 Lossless passthrough

When a chart is loaded, every MIDI track Chart Builder doesn't edit (vocals, harmonies, pro keys, pro
guitar, venue, unknown tracks) is kept as raw bytes, sliced from the file as
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

**Vocal tracks.** `PART VOCALS` and `HARM1`–`HARM3` are passthrough tracks that the vocal lanes edit
([C3](#c3-editor-layout) item 7). An edit replaces the track with one written again
(`VocalTrackContent`): its sung notes (36–84), phrases (105, 106) and lyrics are written from what
was edited, and every other event (its name, percussion, overdrive, animations) is kept with its
bytes, at its tick, in its order. A vocal track that hasn't been edited is still written byte for
byte. One that has reads back as what was edited, though its bytes differ: running status isn't
kept, the notes are on the first channel, and at a tick the kept events come first, then the
lyrics, after the note-offs and before the note-ons.

**When the tempo map changes in a way that keeps times.** Passthrough tracks hold ticks, so a tempo
edit with *keep times* on ([C4](#c4-tempo-map-and-grid)), or a stretch
([C15](#c15-editing-ergonomics)), would leave vocals, venue and pro keys at their old ticks while the
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
     `NoteBuilder`'s rules and the song's `song.ini` settings. The editor and its preview read
     sustains with the 10-tick floor; the save check reads the chart under the cutoff the save
     writes, so the two agree for a normalized chart ([C18](#c18-preview)). The comparison extends
     `NoteCheck` (behind `ChartMergeWarning`) to
     compare star-power phrases, exact sustain lengths, accents, ghosts and the tempo map as well.
     `MidiChartReader` is extended to read `[ENABLE_CHART_DYNAMICS]` and velocities, as YARG does.

  The game view shows what the games will read even when the author wrote something they read
  differently (for example an open note in a chord, CHR022). So a chart with lint errors still
  passes the check and can be saved.

### 4.4 Audio analysis (`ChartShared.Audio/Analysis/`)

| Component | Responsibility |
| --------- | -------------- |
| `SpectrogramBuilder` | STFT over a stem using the existing `Spectrum/Fft` and `Dsp/Windowing`, mapped to log-frequency bins and quantised to bytes. Runs off the UI thread, produces columns in chunks so the view can draw as they arrive, and is **held in memory only** ([C13](#c13-spectrogram)). |
| `OnsetDetector` | Spectral-flux onset detection with an adaptive threshold: one envelope per stem, plus one per band for drums ([C14](#c14-onset-and-beat-assist)) |
| `TempoEstimator` | Estimates a local tempo through the song from the onset envelope's windowed autocorrelation (a tempogram), with a preference for 60–200 BPM, then tracks beats with dynamic programming that follows that local tempo, so step changes are tracked rather than averaged. Gives a proposed list of beat times, a confidence per region, and a tempo map with anchors. |
| `Synth` | Makes the metronome click and the note hit sounds as PCM. They are mixed in **after** the time-stretcher, so they are never stretched, and share the stems' output clock and latency ([§4.4.1](#441-playback-changes)). |

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
- **A smooth clock.** `Position` only moves when SDL takes a buffer, and while stretching it follows
  the grains, back and forth. Drawing uses `SmoothPosition`, which interpolates between those
  updates with a monotonic clock and follows the speed's nominal position rather than the grains.

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
- **`SmoothPosition`** runs on a `TimeProvider` (the system's monotonic clock), in output frames,
  which the device plays at its own steady rate. Each time SDL takes more, it runs up to 10% faster or
  slower until it has made up how far it is from what has been taken (over about 200 ms), so it
  never steps back. It is put straight onto what has been taken past 50 ms, or after a seek, a change
  of speed, playing or pausing. It maps output frames to the mix by a second `PositionMap` of the
  stretcher's nominal `SourcePosition`, which moves evenly at the speed. `Position` and the sounds laid
  over the mix stay on the grains played.
- **Regions.** The loop is a region that `StemPlayer` plays. A loop carries on
  from the region's start without dropping the queue. Played as mixed, it turns on the region's end
  frame; stretched, within a chunk (20 ms) of it. A seek drops a region played once, but not a loop.

**Latency and speed.** The calibrated latency ([C11](#c11-calibration-tool)) is in real time, and
`Position` is in song time. Everything drawn against the audio is drawn at
**`SmoothPosition` − latency × speed**, so at 50% speed a 40 ms latency is 20 ms of song.
*Changed for issue #103:* starting and stopping never jumps (`StemPlayer.DrawnPosition`). Paused,
what is drawn is at the audio's own position, and pausing (`PauseWhereDrawn`) puts the audio where
it was drawn, so the audio still to be heard is heard again on playing. Played from a standstill, a
seek or a region's start, what is drawn holds there until what's heard gets there. A negative
latency still steps forward as playing starts.

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
  window of its own. **New chart** opens a new chart in the Editor ([C1](#c1-new-chart-and-chart-settings)).
  Creating the chart there, or opening a song, closes the launcher and opens an **Editor window**
  in its place. Closing the Editor brings the launcher back.
- **Editor window** ([§8](#8-ui-outline)):
  - **Toolbar.** It shows the program's icon and version, a **Settings** button that opens
    **Program settings**, the editing tools, and **Undo** and **Redo**. Its groups wrap onto more
    rows as the window narrows rather than scrolling.
  - **Side panels, tabbed and docked like Visual Studio's tool windows.** The left one has the
    **Chart settings** tab, with its parts and ratings, metadata and tracks; the **Art** tab, with
    its album art and backgrounds ([C1](#c1-new-chart-and-chart-settings)); the **Sections** tab;
    and the **Tempo map** tab, with the resolution, the delay and the tempo map
    ([C4](#c4-tempo-map-and-grid)). The right one has the
    **Properties** tab, which shows
    the selection ([C3](#c3-editor-layout)), the **Pending changes** tab
    ([C17](#c17-save-and-export)) and the **Findings** tab ([C3](#c3-editor-layout) item 5).
  - Each panel can be resized, and collapsed to a strip along the window's edge showing its tabs'
    titles, each of which brings the panel back on that tab. Their widths and whether each is collapsed are saved
    ([§10](#10-settings)).
  - **Timeline**, along the bottom at the window's full width. It shows the chart's notes over the
    waveform of the whole mix. It can be resized (the height is saved) and scrubbed.
  - The chart's canvas (below) fills the middle.
- **`ChartEditorViewModel`**, which joins the editor's state to playback. *Settled in CM2:* the
  lanes are built in code, so nothing needs a `ViewLocator` yet.
- **Two custom-drawn canvases over one model**: `HighwayEditorCanvas` (vertical) and
  `TimelineEditorCanvas` (horizontal). They share one tool and hit-testing layer, `EditorTools`
  (place, select, erase, sustain, phrase, tempo, sync, loop), so behaviour is the same in both.
  - *Settled in CM2:* `EditorTools` lives in Core (`Charting/Editing/`) with the rest of the
    editor's state, UI-free, so its tests need no canvas. The canvases only map the pointer to a
    tick and a lane, through `LaneProjection`.
  - Drawing uses `GemPainter`, `HighwayPalette` and `LaneWaveformPainter`, which moved from
    Chart Packer's `NoteHighwayView` into `ChartShared.UI`.
  - Chart Packer's player and highway view models stay in Chart Packer. They're built round the
    inspector's song, drafts and offsets, which the editor doesn't have.
- **`CalibrationWindow`** ([C11](#c11-calibration-tool)) is a dialog in `ChartShared.UI`, which both
  apps open. It uses the shared `WindowChrome` for the dark title bar and its owner's icon.
- **Drag and drop.** Chart Builder's launcher takes dropped audio files, which start a new chart
  with them as its stems ([C1](#c1-new-chart-and-chart-settings)), and a dropped song folder or
  `.sng`, which opens it
  ([C2](#c2-open-and-import)). Chart Packer's main window takes no drops for Chart Builder.
- Strings go in Chart Builder's own `Strings.resx`, and strings both apps use go in
  `ChartShared.UI`'s `SharedStrings.resx`. New icons come from game-icons.net and are credited in
  `THIRD-PARTY-NOTICES.md`. Styling follows the shared theme in `ChartShared.UI/Styles/`.

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
- **A folder save's order** (`DraftWriter.SaveFolderAsync`, `SongFolderFiles.ApplyAsync`):
  1. every added file is copied to a temp file beside its target, so nothing has changed yet;
  2. the temp files are moved into place;
  3. `song.ini` is written;
  4. the removed files are deleted.

  A copy that fails leaves the folder, `song.ini` included, as it was (AC C17.3). The removals come
  last, so a crash during a `.chart` import's first save can't leave the folder with neither
  `notes.chart` nor `notes.mid` (AC C2.4); the worst case is a folder holding both, and
  `ChartReader.Pick` then reads the new `notes.mid`. A removed `notes.chart` goes only once
  `song.ini` holds what was taken from it. A crash between steps 2 and 3 leaves the new `notes.mid`
  with the old `song.ini`, which matters only for the drum layout keys and `multiplier_note`; the
  recovery file written at the start of the save ([C15](#c15-editing-ergonomics)) covers it.

### 4.7 Dependencies

**No new packages, except logging.** FFT, windowing, decoding, mixing, output, MIDI reading and
writing, and the settings are all in-house or already budgeted
([project-spec §4.3](project-spec-v1.md#43-native-aot-constraints-required)). The AOT constraints
apply unchanged: compiled bindings, source-generated MVVM and JSON, and no reflection.
Instrument input ([C19](#c19-playtest-and-instrument-input)) adds none either: game controllers
are read through SDL3's joystick subsystem, from the SDL3 package already used for audio, and MIDI
devices through Windows's own winmm, called with `LibraryImport`.

The one addition is **Serilog** (`Serilog` and `Serilog.Sinks.File`, Apache 2.0). Both apps log
through it to daily files in the per-user log folder (`UserConfigPaths.LogFolder`), which is kept
outside the config folder. It is configured in code (`ChartShared.UI/AppLog.cs`), never from a
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
| `--new [--] [<audio file>…]` | A new chart in the editor, with the audio files as its stems ([C1](#c1-new-chart-and-chart-settings)) |

`--` ends the options, so a path that starts with `-` is still a path. Paths are made absolute against
the current folder as they're read (`BuilderArgs.Parse`). Arguments that can't be used (an unknown
option, a second song path, an empty argument, a missing path) open the home screen and say why.

**Difficulty report.** `--difficulty-report <songs folder> [--out <file.csv>] [--depth <levels>]`
opens no window. It rates every song under the folder with the difficulty model
([difficulty-model.md](difficulty-model.md)) and writes a CSV (`DifficultyReport`). The CSV has a
row for each song, with its path and then, for guitar, bass, rhythm and guitar co-op:

- the `diff_*` rating `song.ini` reports;
- the model's rank for the part's highest charted difficulty;
- each of the five attributes' scores.

Values are to a tenth. A last column says why a song couldn't be read. Without `--out`, the CSV is
`difficulty-report.csv` in the current folder; `--depth` defaults to 10 folder levels. Songs are only
read, and neither the settings nor the log are touched. Progress goes to the console it was run from
(`DifficultyReportArgs.Parse`; exit code 0 when written, 1 for bad arguments, 2 when the CSV can't be
written).

**Finding Chart Builder.** Chart Packer runs the `ChartBuilder` executable that sits next to its
own (`Environment.ProcessPath`). If it isn't there, Chart Packer says Chart Builder isn't installed
and goes on without it. In Debug builds each app keeps its own output folder. Building Chart Packer
builds Chart Builder too, and records where its output folder is (an `AssemblyMetadata` attribute),
which Chart Packer uses when Chart Builder isn't beside it. Release builds have no such fallback.

**One song per Chart Builder process.** Each Chart Builder window has at most one song open, and
several can run at once on different songs.

**Song lock (`ChartShared.Core/Scanning/SongLock`).** Whoever is editing a song holds its lock, and
no other process of either app writes to it meanwhile, however many of each are running
(GitHub issue #89).

- The lock is an open handle on `{song}.lock` beside the song: `Foo.sng.lock` for `Foo.sng`, and
  `Foo.lock` beside the song folder `Foo`, so it's never one of the song's files. Beside the song,
  it also keeps out other users and other computers sharing the library. The handle is the lock,
  never the file: a lock file nobody has open means nothing, and the next lock takes it.
  - On Windows the file is opened delete-on-close, sharing reads only, so a second holder is
    refused, and the OS deletes the file however the process ends. The holder writes which app it
    is into the file, which the others read to say who has the song.
  - On Linux and macOS it's opened with `FileShare.None`, which .NET maps to an exclusive `flock`
    that the OS lets go of if the process dies. The file is deleted as the lock is let go (left
    behind by a crash), and the holder can't be read, so messages don't name the app.
  - A song whose folder can't be written to isn't locked: no one can write it there.
  - *Settled in CM2:* checking the lock opens its file for a moment, so taking the lock tries a
    few times, 50 ms apart, before saying another has the song.
- **Chart Builder** holds the lock while the song is open, and takes it for a new chart's first
  save. Opening a song another holds says who has it (another Chart Builder, or a Chart Packer with
  unsaved changes to it) and doesn't open it. Opening the song a window already has reads it again
  under the lock it holds. Save and pack holds the `.sng` it writes, and skips one another holds.
- **Chart Packer** holds a song from its first unsaved change until the change is saved or
  discarded (`SongLocks`, over a per-process `SongLockSet`, so its own holds don't refuse each
  other), and holds every song it writes for as long as it writes it: a metadata save, Save all, a
  dedup delete, a merge into it, a pack or unpack written over it, or a download replacing it. A
  write to a song another holds is refused and says who has it; there's no going ahead. A change
  made to a song just taken elsewhere is dropped, with a notice.
- Chart Packer watches the songs its inspectors show, about once a second. While another holds
  one, the inspector is read-only and says why, and its player and background video let go of the
  song's files and don't open them again, so the holder can replace them; once it's let go, the
  song is read again.
- On Windows a file can't be replaced or deleted while another process has it open, even only to
  read, so every save replaces and deletes a song's files through `SongFileCommit`, which tries again
  for up to 5 s while one is in use. That outlasts Chart Packer's watch letting go, and brief
  readers such as Explorer's thumbnails.
- `SongFileGate` stays the gate between reads and writes within each process.

**Refreshing Chart Packer.**

Chart Packer doesn't keep the Chart Builder processes it starts. It checks for changes when its
window is activated, as it is when the user comes back from Chart Builder.

- Chart Packer remembers every song it has opened in Chart Builder since it started, with a stamp
  of it: the size and modified time of every file in the folder, or of the `.sng`. When its window
  is activated, it re-reads the songs whose stamp changed, so the library, inspector and packer
  show them.
- Chart Packer never starts a new chart, so a song Chart Builder creates appears in its
  library on the next scan.
- Chart Packer re-reads the shared latency file ([§10](#10-settings)) when its window is
  activated.

---

## 5. Features

Every feature lists numbered acceptance criteria (**AC C*n*.*m***). "Findings" are `Finding`s
shown in the app's findings panel. Chart lint rules are listed in [§6](#lint-rules).

### C1. New chart and Chart settings

**New chart** opens a new chart in the Editor straight away. There is no wizard or dialog before it:
the chart starts on defaults, and everything about it is set in the Editor.

- **Chart settings** is a tab in the Editor's left panel that sets everything a chart needs: its
  timing and `delay`, its parts and their difficulty ratings, the song's metadata (name, artist,
  charter, album, genre, year and any other `song.ini` key) and its tracks. It is the same for a new
  chart and a song that was opened.
- The **Art** tab beside it sets the album art, the background image and the background video.
- Every edit in either tab is an undo step, as the chart's edits are.
- A new chart has no song folder until it is first saved. The first save asks where it goes
  ([C17](#c17-save-and-export)).

**Input:** nothing, or one or more audio files.
**Output:** a new chart in the Editor. Its first save writes a new song folder containing
`notes.mid`, `song.ini` and its stems. From then on it is a song like any other: Chart Builder holds
its lock, it is on the recent songs, and it appears in Chart Packer's library
([§4.8](#48-launching-and-coordination)).

**Entry points.**

- **New chart** on Chart Builder's launcher, audio files dropped on the launcher
  ([§4.5](#45-app-chartbuilder)), and `ChartBuilder --new` on the command line
  ([§4.8](#48-launching-and-coordination)). Each closes the launcher and opens the Editor with a new
  chart (below).
- The **Chart settings** tab in the Editor's left panel, for whatever chart is open.
- A song with neither chart file is offered **New chart in this song** when it is opened
  ([C2](#c2-open-and-import)). That opens the Editor with a new chart in that song, and its first
  save adds `notes.mid` and the `song.ini` keys to the song through its draft ([§4.6](#46-saving)).

Chart Packer has no **New chart**. Charts are created in Chart Builder, and Chart Packer's only way
into it is **Edit chart** ([C2](#c2-open-and-import)).

**A new chart** starts with:

- the **Default resolution** setting ([§10](#10-settings)), 120 BPM and 4/4 at tick 0;
- a Lead (5-fret guitar) part, with no notes;
- an empty `EVENTS`: `[music_start]`, `[music_end]` and `[end]` are placed as it's saved
  ([C8](#c8-phrases-and-events) item 5);
- the audio files it was given, as its stems:
  - Files can be `.ogg`, `.opus`, `.mp3` or `.wav`. Each is given a stem from its file name using
    `AudioStems`, e.g. `Drums.ogg` → `drums`, `song.mp3` → `song`.
  - `AudioStems` gives no stem for a name it doesn't recognise. The first such file is then given
    `song` if no other file has it (`NewChartStems`).
  - A file that isn't audio, has no stem, or would share a stem is left out, and the Editor's notice
    says which and why.
  - Until the first save, each stem is played from where it is. A file that doesn't decode is said
    by playback, as for any song ([C10](#c10-playback)).
- **No audio is allowed.** The chart is made against a silent timeline. The song then has finding
  SNG052 (no audio stem) until a stem is added in Chart settings' Tracks.

In Chart settings' Metadata, a new chart's name and artist are filled from the first stem's tags
where it has them (`AudioFileInfo.Read`), and its charter from the **Default charter name** setting
([§10](#10-settings)).

*Settled after CM5:* the album, genre and year are filled from the tags too. An edit to the song
takes the place of whatever else would change its key (the default charter, `song_length`, a rating
or a delay), and the keys the chart decides (the drum layout keys, `multiplier_note` and
`sustain_cutoff_threshold` while the chart has a sustain) can't be edited. The song's edits
count as unsaved changes, so closing
asks first and autosave keeps them, and none can be made, undone or redone while a save runs.

*Revised after CM5:* the Chart Inspector, which edited the song as Chart Packer's inspector does, is
gone. Its edits were pending changes taken back with Revert rather than undo steps, and its tracks
were only listed. Chart settings and the Art tab take its place:

- Each edit to the song (a `song.ini` value, a rating, the delay, the art, a background or a track)
  is an **undo step** on the chart's undo stack, and still a pending change until saved
  ([C17](#c17-save-and-export) item 2). It is kept as the values the save would write, before and
  after (`SongValueCommand`, `MediaCommand`), so undoing an edit made before a save gives back what
  the song had, as a new pending change.
- A file a save replaces or removes (art, a background or a track) is copied first to a temp folder
  of the Editor window's own (`MediaStash`), deleted as the window closes, so an undo past the save
  can put the file back. The copies a crash leaves are swept a day later.
- The tracks are played and drawn as they'll be saved: adding, replacing or removing one, or undoing
  it, opens the audio again, keeping the playhead, playing on, and each stem's mute, solo and volume.

**The tab.** Chart settings is a tab in the Editor's left panel, the first. Its sections,
**Timing**, **Parts**, **Metadata** and **Tracks**, are one under the other, with each field's label
above it, as the panel can be narrow (mock-up in [§8](#8-ui-outline)). The panel opens on it.

**Metadata** is Chart Packer's inspector's metadata, shared with it (`MetadataEditorView`): a row per
`song.ini` key, edited in place, and a row to add one. The ratings are left to Parts. A row's
**Undo** (back to the song as saved) and **Remove** show as it's hovered or focused, Undo staying
while there's a change, and are on its right-click menu too; Quick Fix is a link under a failing value.

**Tracks** is Chart Packer's inspector's track list, shared with it (`TrackListView`): a row per
track with its mute, solo, volume and waveform, the tracks a pending change removes, and **Add
track**. A row's **Replace** and **Remove**, and a removed track's **Keep**, are on its right-click
menu. It takes the place of the transport bar's Stems menu ([C10](#c10-playback)). The mute, solo
and volume are only for listening: they aren't undo steps and aren't saved.

**The Art tab** has a tile each for the album art, the background image and the background video,
showing what the song will have once saved; the video plays against the song while the tab is shown.
An empty tile has **Choose…**; a tile's right-click menu has **Replace…**, **Remove** and **Revert
to saved**. A file is checked before it's used: an image is decoded, and a video is checked to be
one FFmpeg can play. Under the video, a **Video start** box sets `video_start_time` in milliseconds to
line the video up with the audio: positive skips that much of the video, negative holds its first
frame. It can be typed or dragged sideways, as Chart Packer's offset boxes can; the video shows each
value at the playhead as it changes, and the value is set as one undo step once it's left alone.

**Timing.**

- **Resolution**: 192, 480 or 960, the values at which every common snap lands on a whole tick. A
  chart opened at another resolution shows that value too. Changing it re-ticks the chart (below).
- **Starting BPM**: 10 to 1000, with up to three decimals.
- **Starting time signature**: any that [C4](#c4-tempo-map-and-grid) allows.
- The starting BPM and time signature are the tempo and time signature markers at tick 0. Changing
  them is a tempo edit ([C4](#c4-tempo-map-and-grid)): it follows the *keep ticks* or *keep times*
  setting, and keeps anchors where they are.
- **Detect from audio** comes with [C14](#c14-onset-and-beat-assist) (CM4). It runs the tempo
  estimate on the mix, fills in the starting BPM, and proposes a tempo map to review in the editor.
  *Settled in CM4:* it's a button under Starting BPM. The BPM is the first confident stretch's,
  tidied to a whole BPM when it's within 0.05 of one (`BpmTidy`), and set as one undoable step, as
  a BPM typed is. The proposal for the whole song then shows in the editor, as C14 item 4's does.
- *Settled after CM4:* the timing is in the Tempo map tab ([C4](#c4-tempo-map-and-grid) item 9),
  not Chart settings. Its **Timing** has the resolution and the audio delay; the starting BPM and
  time signature are the first row of its list, the song's start; and **Detect from audio** is in
  its **From the audio**. A starting BPM or time signature out of range that the chart already has
  doesn't hold up the other settings.

**Parts.**

- A row for each v1 part: 5-fret Lead, Bass, Rhythm, Co-op and Keys; GHL Guitar, Bass, Rhythm and Co-op;
  Drums, with a choice of 4-lane, Pro or 5-lane. Each row has a tick box, ticked when the chart has
  the part, and the part's difficulty rating (`diff_<part>`), empty while it's unrated, with an ✕ that
  clears it to `-1`. Pro drums have a second rating, `diff_drums_real`. A rating typed is applied
  once the typing pauses, as a BPM is, and each is an undo step.
- MIDI has no empty difficulties, so a new part starts with all four difficulties empty. Each one
  exists as soon as it has a note.
- Unticking a part that has notes asks for confirmation, as **Remove part** does
  ([C2](#c2-open-and-import)).
- The drum variant of a drums part that has notes is changed with the conversion in
  [C6](#c6-drums-4-lane-pro-and-5-lane) item 8 when it goes to or from 5-lane. Switching between
  4-lane and Pro is a setting (C6 item 9), and keeps the notes as they are.

**Applying.**

- Each change is applied as it is made, so the starting BPM and time signature can be found by ear
  while the song plays; playback goes on, and its beats follow. A BPM or beats per measure being
  typed is applied once the typing (or the spinner) pauses for 0.6 s, so a burst of typing is one
  step. A value out of range is said under the field and applies nothing.
- Each applied change (resolution, tempo, time signature, parts or drum variant) is **one undoable
  command**. Undo and redo show in the tab.
- Their `song.ini` effects are added to Chart Builder's pending changes
  ([C17](#c17-save-and-export)): `diff_<part> = -1` for each added part, and
  `pro_drums`/`five_lane_drums` for the drum variant. A new chart has no `song.ini` yet, so every
  part's `diff_<part> = -1` and the drum keys are pending until its first save writes them.
- A change that leaves the chart as it was adds no undo step and no pending change.

**Changing the resolution.**

- The chart is re-ticked so that everything keeps its time (`ChartRetick` with
  `TickMaps.KeepTimes`). The tempo and time signature markers' ticks are scaled by the new resolution
  over the old, their tempos (microseconds per quarter note) are unchanged, and each anchor moves with
  its marker. Everything else is re-ticked as a *keep times* tempo edit is
  ([C4](#c4-tempo-map-and-grid)), the passthrough tracks included
  ([§4.2](#lossless-passthrough)).
  *Settled in CM2:* a marker's scaled tick is rounded to the nearest tick, halves up. Tempo or time
  signature markers that land on one tick are all kept, and the warning says the games read only
  the last of them. An anchor that lands on another anchor's tick is left out, and listed.
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

**The first save** of a new chart ([C17](#c17-save-and-export)).

- The song's **name and artist** are set in Chart settings' Metadata. The save can't go ahead until
  both are set: it says so and brings Chart settings up. The dialog shows them, and starts the
  folder name from them.
- It asks where the song folder goes: a parent folder and a folder name.
  - The parent folder is by default the last one used for a new chart, otherwise Chart Packer's
    library root (read from Chart Packer's settings, never written).
  - The folder name starts as `<artist> - <name>`, with characters that aren't allowed in a path
    replaced (`NewChartFolder.NameFor`). It is checked against the `.sng` filename
    rules (`NewChartFolder.ProblemOf`).
  - An **existing folder is never used or overwritten**. If the name is taken, the save can't go
    ahead, and the dialog says why.
- **What is written** (`NewChartCreator`):
  - each stem, copied as `<stem>.<ext>` in lowercase;
  - `notes.mid`, written from the chart as any save is ([§4.3](#43-writing-midichartwriter));
  - `song.ini`, holding the chart's pending changes ([C17](#c17-save-and-export)) that weren't
    reverted: the name, artist and charter, `diff_<part> = -1` for each part (the keys are in
    [§7.5](#75-songini-keys-chart-builder-writes)), `pro_drums`/`five_lane_drums` as the drum
    variant needs, and `song_length` from the longest stem.
- **How it is written.** Everything is written into a temp folder beside the target, which is then
  renamed into place. A failure or cancel leaves nothing behind.
- The song is then locked and added to the recent songs, and the Editor goes on with it as a song
  opened from that folder.

Acceptance criteria:

- **AC C1.1** A new chart with one stem and no notes, once saved, validates with no errors
  ([project-spec F3](project-spec-v1.md#f3-validate)), packs to a `.sng`, and reopens in the editor
  with the same tempo map, parts and metadata.
- **AC C1.2** Cancelling a new chart's first save, or failing to copy a stem during it, leaves no
  folder or temp folder behind.
- **AC C1.3** A new chart's first save can't use an existing folder, and an existing folder's
  contents are never touched.
- **AC C1.4** Giving `Guitar.OGG`, `Drums.ogg` and `mix.mp3` makes them the stems `guitar`, `drums`
  and `song`, and the saved folder contains `guitar.ogg`, `drums.ogg` and `song.mp3`. Giving
  `mix.mp3` and `other.mp3` makes one `song` and leaves the other out, saying so.
- **AC C1.5** Choosing Pro drums gives `pro_drums = True`, and choosing 5-lane gives
  `five_lane_drums = True`, as pending changes and in the saved `song.ini`. The reopened chart's
  drums are read with the same layout.
- **AC C1.6** Changing a chart's resolution from 480 to 960 moves nothing in time and gives no
  warning. From 480 to 192, a chart with a note at tick 1 gives the warning, with the number of
  items that would move and the largest move. **Cancel** leaves the chart unchanged and puts the
  resolution back. **Change anyway** is one undo step, and undoing it gives a model equal to the one
  before.
- **AC C1.7** A change that leaves the chart as it was adds no undo step and no pending change, and
  typing a BPM adds one undo step once the typing pauses. Adding a part through Chart settings shows
  its `diff_<part> = -1` as a pending change.

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
   `notes.chart`. A song with neither is offered **New chart in this song**, which opens it in the
   editor with a new chart, as New chart does ([C1](#c1-new-chart-and-chart-settings)).
3. **`.chart` import.**
   - The chart is converted with `ChartToMidi`. Any `ChartMergeWarning`s are shown before the
     editor opens, with the times they happen at, and the user can go ahead or cancel.
   - `ChartToMidi` returns the `.chart`'s `Offset` but doesn't write it anywhere. When `song.ini`
     sets no `delay`, the import adds the Offset as a pending `delay` change, as `SongMerger`
     already does through `SongChartSettings.DelayOf`.
   - **The `.chart`'s `[Song]` metadata** is read by `ChartSongSection`, and each value is added as
     a pending `song.ini` change wherever `song.ini` has no value for that key. That way, removing
     `notes.chart` loses nothing. `Name`, `Artist`, `Album`, `Genre` and `Charter` become their own
     keys; `Year` becomes `year`, without the `", "` Moonscraper writes before it; `PreviewStart`, in
     seconds, becomes `preview_start_time` in milliseconds; and `Difficulty` becomes `diff_band`. The
     resolution, the audio streams and `MediaType` belong to the `.chart` alone.
   - The `.chart`'s own sustains decide the `sustain_cutoff_threshold` the save writes, as for any
     chart: a pending change that can't be reverted ([§4.6](#46-saving)).
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
7. **Recovery.** If the song has an autosave ([C15](#c15-editing-ergonomics)), the user is offered
   **Recover** or **Discard**, or can cancel the open. The offer warns when the song's chart has
   changed on disk since the autosave. An autosave that is the song's chart byte for byte (a save
   that went through, then a crash before its autosave was deleted) is deleted without asking.

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
   *Settled in CM2:* each docked lane has its own view, and `V` switches the lane in focus. Every
   lane shares the cursor, selection and tool. Zoom is each lane's own look-ahead (C10 item 6), which
   a switch keeps.
2. **Overview strip** along the edge. It shows the whole song: sections, note density, star power,
   solos, lint marks and the current view, and clicking or dragging it jumps there. *Settled in
   CM2:* it's the timeline along the Editor's bottom, which already spans the song. It shows the
   lane in focus.
   - *Settled:* it shows every phrase of the lane in focus, a thin band under the ruler for each
     row of its phrase rail ([C8](#c8-phrases-and-events) item 1), in the rail's order and colours:
     star power (as the games read it), solos and big rock endings on 5-fret and GHL; star power,
     solos, fills (with a big rock ending), rolls, trills and the difficulty's disco flips on drums.
     The sections' names are below the bands. The chart's tempo and time-signature changes are drawn
     as amber lines down the strip, in the tempo markers' colour, whatever tool is active, and the
     loop region's start and end as blue lines ([C10](#c10-playback) item 2).
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
     *Settled in CM3:* the end is the tick after the last one the phrase covers. Properties also
     shows the kind, which it doesn't change, and the start's time. A start or end typed there moves
     that end of every selected phrase, each keeping its other end, as one undoable step.
   - *Settled in CM3:* **Disco flip ranges** ([C6](#c6-drums-4-lane-pro-and-5-lane) item 7): start
     and end, set as a phrase's are, as one undoable step, and the difficulty, which is only shown.
     A range without an end shows an empty end.
   - **Tempo markers:** BPM, time signature, and whether it's anchored.
   - With several items selected, a field shows `<<multiple>>` when the values differ, and
     editing it sets every item. This is the same model as the metadata editor's bulk editing.
5. **Findings panel**: the [§6](#lint-rules) lint findings, each linked to the place in the chart
   where it happens. *Settled in CM2:* the Findings tab's title doesn't show the counts, as a
   collapsed panel's strip reads the titles only once. The counts are at the top of the panel.
6. **Transport bar**: play and pause, position, speed, pitch correction, loop, metronome, hit
   sounds, stem mute and solo, and the calibration value ([C11](#c11-calibration-tool)). The loop
   button repeats the loop region, which the toolbar's Loop tool draws ([C10](#c10-playback) item
   2). Beside
   Record, the Playtest button (never on with Record; both can be off), the playtest's stats, the
   Problems list and Clear ([C19](#c19-playtest-and-instrument-input)).
7. **Vocal lanes.** A lane showing `PART VOCALS` or `HARM1`–`HARM3` is always drawn along a
   timeline, as a piano roll:
   - one row per semitone, every pitch a part sings (36 to 84) whatever its notes, so any pitch can
     be placed and the rows never move as notes are edited, with black keys' rows darker and each C
     named at the left edge;
   - each sung note a bar on its pitch's row; a slide (`+`) a line from the note before; a spoken
     note (`#`, `^`) an outline along the middle row, middle C's;
   - the phrases shaded behind the rows, and as bars in a strip above them; and the lyrics in a band
     under the rows, each syllable at its tick, with a hyphen where it joins the next. A syllable
     that would run into the one before is left out, as the lane is zoomed out;
   - the beats, sections, tempo markers, loop and selection as a highway's lane has them;
   - what's on the strike line lit, as Chart Packer's lyrics lane lights it, wherever the cursor is,
     playing or not: the note being sung near white with a glow where it crosses the line, its
     syllable in the same tint, and its phrase shaded a little more than the others.

   Scrolling, zoom (look-ahead) and the keys that move the cursor or play work as in any lane. A
   lane whose track the chart hasn't says so, and offers to add it, empty. The part is read from its
   passthrough track ([§4.2](#lossless-passthrough)) by `EditorSession.VocalsOf` into a `VocalLane`,
   in ticks, and read again when the track is edited, re-ticked or put back by an undo.

   *Settled with vocal editing:* the lane edits its part with the editor's tools, each edit one
   undoable step that replaces the part's track ([§4.2](#lossless-passthrough)):
   - **Select** picks a note (Shift adds it or takes it away), and dragging it moves the selected
     notes in time, to the grid, and in pitch, by the rows crossed, never past 36 or 84. A note's
     end resizes the selected notes. A press between notes draws a box over ticks and rows. As the
     rows are thin, a note is hit on the row either side of its own when no note is on the
     pointer's.
   - **Place** puts a note a grid step long on the grid step and row pressed, and a drag stretches
     it, never past the next note's start; Shift places a slide. On a note, it moves it.
   - **Erase** removes each note passed over, **Sustain** drags the selected notes' end, **Phrase**
     draws a phrase from the grid step pressed, and **Tempo** adds and drags tempo and time-signature
     changes as in a highway's lane.
   - **The phrase strip** works with any tool, as a highway's phrase rail does: a press on a bar
     selects its phrase (Shift adds it or takes it away), and a drag moves the selected phrases, or
     resizes one by the end grabbed. A part's phrases never overlap. A new phrase is written as
     105, or as 105 and 106 when every phrase the part has is.
   - **Lyrics** belong to the notes that start on their tick: a note moved or deleted takes its
     lyric with it, unless a note that stays starts there. A lyric on no note's start stays where
     it is.
   - **Properties** shows the selected notes' tick, pitch (a MIDI number, or a name such as `F#3`),
     length and lyric, symbols included, with Slide and Spoken, and the selected phrases' start and
     end. **Type lyrics…** gives a typed line's syllables to the selected notes in turn, a slide
     skipped, split at spaces and after hyphens (`Hel-lo world`).
   - **Keys** ([§9](#9-keyboard-shortcuts)): Alt and the arrows move the selected notes a grid step or
     a semitone, an octave with Shift; Ctrl+A selects the part's notes; Delete and Ctrl+X, C and V
     work on its notes, with their lyrics. A vocal part's notes paste only into a vocal lane, and a
     highway's never into one. The keys that change a highway's notes or a part's audio do nothing.
   - The vocal findings ([§6](#lint-rules)) name their part, and going to one opens its lane and
     selects the notes on its tick.

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
   - On the lanes, a time signature can be dragged on its own, or with the tempo marker at its tick;
     the change the song starts with stays put.
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
   - *Settled in CM2:*
     - The segment that absorbs a change is the last one before the anchor that starts after the
       edited marker. Only the first anchor after an edit can move.
     - Removing or moving a marker is absorbed in the same way.
     - A marker can't be moved past an anchor.
     - Every tempo edit is one `ChangeTempoMap` (`TempoMapEdits`), so it undoes exactly.
3. **When a tempo changes, notes keep their ticks (the default) or their times.**
   - Keeping ticks means the notes move in time with the grid, which is right while the tempo map
     is still being built.
   - Keeping times re-ticks, after the change, every note start and sustain end, phrase, event
     and `RawEvent`, and the passthrough tracks too ([§4.2](#lossless-passthrough)).
   - *Settled in CM2:* keeping times leaves the tempo markers on their ticks. Anchored markers
     keep their times by absorption (item 2).
4. **Snap.**
   - From 1/1 to 1/192 of a whole note, including triplets (1/12, 1/24, 1/48…), plus a custom
     value.
   - `[` and `]` step through the common values. Snap can be switched off.
   - At resolutions 192, 480 and 960 every common snap lands on a whole tick. Where a snap doesn't
     (a custom snap, or an imported chart at another resolution), the nearest tick is used.
   - *Settled in CM2:* the snap counts from the time signature in force, and ends at the next one.
     It rounds to the nearest tick. With snap off, notes go to the nearest tick, but the cursor
     keys still step by the snap.
5. **Tap tempo.** Tapping `B` in time with the playing song sets the BPM from the median interval
   of the taps. *Restated in CM2:* it needs three taps. It sets the segment the first tap was in,
   once the taps have stopped for 2 s, as one undoable step, with the anchor offer (item 2) when it
   needs one.
6. **Set BPM from selection.** Select a span and say how many beats it lasts, and the BPM is
   computed. This is the quickest way to fit one bar to the waveform.
7. **Proposed tempo maps** from [C14](#c14-onset-and-beat-assist) arrive as one undoable command.
8. **Beat lines** come from `TempoMap.BeatLines`. Measure lines are stronger, and measure numbers
   are shown.
9. **The Tempo map tab** in the left panel lists every tick where the tempo or the time signature
   changes, the song's start among them, in time order, each with its time and its measure and
   beat. The change in force at the cursor is marked.
   - Each row has a BPM field and a time signature's beats and note value. The BPM is a scrub field:
     dragged sideways, or stepped with the arrows and the wheel, in 0.1 BPM steps. The chart follows
     it as it's dragged, and the change is applied as one undoable step once it's been left alone. A
     field with nothing placed at its tick shows what's in force there, greyed. Typing in it places
     a marker there; emptying it removes the marker. Each edit is one undoable step, with the anchor
     offer (item 2) when it needs one.
   - A row can be gone to, anchored, moved by its tick (its tempo marker and time signature
     together) and removed. The tick is a scrub field too, in whole ticks; the chart follows it as
     it's dragged, and the move is applied as one undoable step once it's been left alone. Where both
     are at one tick, removing asks which.
   - On the lanes, the Tempo tool previews where a press would add a marker, with the Phrase tool's
     hover line, and shows a resize arrow over a change a drag moves: a tempo marker and any time
     signature at its tick, or a time signature on its own. The change the song starts with can't be
     moved, so shows the usual cursor.
   - **Add tempo change** adds a marker at the cursor, on the grid. **Add time signature** puts one
     at the start of the measure the cursor is in.
   - A time signature stands on its own: it needs no tempo marker at its tick.
   - Above the list, **Timing** has the resolution and the audio delay
     ([C1](#c1-new-chart-and-chart-settings)), and **From the audio** has **Detect from audio**,
     **Propose tempo map** and **Find first downbeat** ([C14](#c14-onset-and-beat-assist)).

Acceptance criteria:

- **AC C4.1** In a map with markers at 0 s and 10 s and an anchor at 30.000 s, changing the BPM of
  the first segment leaves that anchor at 30.000 s ± 1 ms. Changing the BPM of the segment from
  10 s, which runs straight into the anchor, is refused with the offer to unanchor.
- **AC C4.2** With *keep times* on, a tempo change leaves every note's time, every phrase's and
  event's time, and every passthrough event's time within half a tick of where it was.
- **AC C4.3** Tick ↔ seconds round trips agree with the existing `TempoMap` to within half a tick
  for every tempo map in the test fixtures.
- **AC C4.4** Anchors survive a save and a reopen. Loading and saving a chart without edits gives
  the same tempo values (microseconds per quarter note), time signatures and anchors. A pack to
  `.sng` keeps the tempos and time signatures but leaves the anchors out
  ([§7.3](#73-text-events)).

### C5. 5-fret notes

**Behaviour:**

1. **Lanes:** green, red, yellow, blue and orange, plus **open**, which is drawn across the whole
   highway. *Settled in CM2:* on a highway, the Place tool puts an open note with Alt-click or
   `0`. A timeline gives open notes a row of their own.
2. **Chords:** several frets on one tick. An open note can be placed in a chord with frets, and a
   fret on an open: YARG plays them, and charts in the wild have them. Such a chord is still flagged
   (CHR022, an Error), as other games read it differently. The editor shows the chord as the games
   read it, so the read-back check still passes and the chart can be saved
   ([§4.3](#43-writing-midichartwriter)).
3. **Sustains:**
   - A sustain is drawn by dragging the note's tail, by placing a note with a Ctrl-drag, or set
     with the sustain tool; the selected notes' sustains change by a Ctrl-drag on one of them or a
     right-drag ([§9](#9-keyboard-shortcuts)).
   - Sustains snap to the grid.
   - A hold of at least 10 ticks is a sustain; a shorter one is a plain note, in the editor and in
     what it saves. The song's `sustain_cutoff_threshold` does not limit what can be authored.
   - A sustain dragged shorter than 10 ticks (by its tail, a Ctrl-drag or a right-drag) becomes a
     plain note as it's dragged. A loaded chart's holds under 10 ticks are made plain too.
   - On save, `sustain_cutoff_threshold` is derived from the sustains the save writes, after a
     sustain that runs into the next note on its lane is cut short there:
     `max(2, min(resolution / 3, shortest sustain - 1))`, or left alone when there are no sustains.
     The tick under the shortest sustain keeps it playable in both YARG (`length < cutoff`) and
     Clone Hero (`length <= cutoff`); the third-of-a-beat cap covers songs whose sustains are all
     longer than that.
   - **Extended sustains** (one note held while others are played) and **disjoint chords**
     (notes of one chord with different lengths) are allowed.
4. **Forcing:**
   - **Natural HOPOs** are shown as they will be read, using `NoteBuilder`'s rules and the song's
     HOPO threshold (`hopo_frequency`, `eighthnote_hopo`, `hopofreq`, or the default).
   - **Force HOPO**, **force strum** and **tap** are set per chord, because MIDI's markers cover
     every note on a tick. Setting one on a note sets it for the note's chord. They are drawn as
     distinct markers.
   - A force that changes nothing is flagged (CHR023), with a quick fix that removes it.
   - *Settled in CM2:* H, G and T set the flag on every selected chord, or clear it when all of
     them have it. Force HOPO and force strum exclude each other; tap goes with either.
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
   - *Settled in CM3:* a toggle makes the fewest marker changes that flip only the notes toggled. A
     marker grows only across ticks with no note on its pad, so neighbouring notes keep what they
     were. Toggling a selection makes every note a tom, or every note a cymbal when all of them are
     toms already. A note that is moved takes the cymbal or tom of the tick it lands on, as the
     markers stay where they are.
4. **Accents and ghosts.**
   - Per note, on any pad.
   - Saved as velocity 127 (accent) or 1 (ghost), with `[ENABLE_CHART_DYNAMICS]` added to the
     track automatically while the track has any, and removed when it has none.
   - Every other note is written with velocity 100. Velocities in a loaded chart without
     `[ENABLE_CHART_DYNAMICS]` are not read as accents or ghosts ([§4.1](#editable-model)).
   - *Settled in CM3:* an accent is drawn with a bright ring around it, and a ghost faded with a
     dashed edge, on both apps' highways. The editor shows them as soon as they're added, as the save
     will write `[ENABLE_CHART_DYNAMICS]` for them.
5. **Drum fills (activation lanes).**
   - A phrase on every difficulty. Its last note is where star power is activated.
   - **Generate fills** proposes fills over the selection or the whole part, in the same window as
     Generate star power ([C8](#c8-phrases-and-events) item 1). The author picks, for each run:
     - the **spacing**: fills start no closer than *n* measures apart (default 4);
     - the **fill length** (default 1 measure);
     - **what a fill must end on**: a crash (default) or any note. A crash is a green cymbal in
       Pro, orange in 5-lane, and green in 4-lane;
     - **what to do with the fills already there**: add alongside them (default), or replace those
       wholly inside the span.

     Fills are proposed only after the second star-power phrase has ended (the earliest point a
     player can have enough to activate), and never inside a star-power phrase. Where no note
     fits at a spot, the fill is left out. The window lists the proposals to keep or drop, previews
     them as amber bands on the highway, and adds the kept ones as one undoable command.
   - *Settled in CM3:* a fill covers the notes from its start up to, but not including, its end
     (only its start, if it has no length), as the games read markers. Its last such note on a
     difficulty is where star power is activated there.
   - *Settled in CM3:* whether a drums phrase on pitches 120–124 is a fill or a big rock ending
     follows `[coda]` ([C8](#c8-phrases-and-events) item 4). Adding, moving or removing `[coda]`
     changes the phrases it decides in the same undoable step, and a fill can't be added after
     `[coda]`, nor a big rock ending before it.
   - *Settled in CM3:* Generate fills proposes at measure lines. A fill at one lasts the fill length
     in measures, by the time signatures in force, and ends on the first beat of the measure after:
     its **activation note**. That must be a crash (or, for any note, a note other than a 2× kick) on
     the hardest difficulty with notes, and every difficulty with notes must have a note other than a
     2× kick there, or the spot is left out. The fill ends the tick after that note, as CHR043's fix
     ends one ([§6](#6-chart-lint-rule-catalogue)), so the note is the last it covers and CHR043
     never fires on it. A crash on Pro is a green note no tom marker covers; 4-lane's green is a crash
     whatever its markers say. Spots are taken earliest first, from where the second star power
     phrase ends (overlapping phrases count as one, as saving merges them). A fill never overlaps
     star power (CHR042), another fill or a big rock ending, ends before the first `[coda]`, and
     starts at least the spacing in measures from every other fill's start, those already there and
     those proposed. Over the selection, a fill starts at or after the selection's start and ends on
     a note at or before its end.
   - *Settled:* **Generate fills…** is beside the Phrase tool's picker, for a drums lane with notes.
     Its options are in the generator window's toolbar, kept while the editor is open: the spacing,
     the fill length, what a fill ends on, and Existing Fills (Replace or Add Alongside, default Add
     Alongside). The window lists the fills proposed, each kept or dropped by its row,
     with ◀ Previous, Next ▶, Keep all, Drop all and Regenerate. The preview highway draws the kept
     fills as amber bands, and the whole-song timeline shows them as amber ghosts. Apply (Ctrl+Enter)
     adds the kept fills as one undoable command; Cancel adds nothing, and the chart is unchanged
     until Apply. In Replace, the fills wholly inside the span are removed when the kept ones are
     added; one reaching across its edge stays, and keeps its ground. When nothing fits, the preview
     says why: no drum notes, fewer than two star power phrases, or no spot.
6. **Rolls and trills.**
   - Single-lane roll and two-lane trill phrases.
   - New ones reach Expert only, as the games default to. Each can be extended down to Hard,
     Medium or Easy, which is saved as the velocity convention `ChartToMidi` already writes
     ([§7.2](#72-shared-markers-every-difficulty)).
7. **Disco flip.** `[mix <d> drums<n>d]` sections, which swap red and yellow in the games, are
   ranges per difficulty ([§4.1](#editable-model)). Notes are always stored on the pads the file
   uses, so moving or resizing a range never changes the file's notes. Inside a range they are
   drawn on the pads the player hits.
   - *Settled in CM3:* the model keeps the mix events, and a range is read from them: it starts at
     a `d` mix and ends at the next mix without one. A new range writes a `d` mix at its start and
     a plain mix at its end, each with the stem configuration in force there, and turns plain mixes
     inside it into `d` ones. Removing a range turns its mixes back, and leaves out any that then
     change nothing. A range can't overlap another, or cover a mix with another setting.
   - *Settled in CM3:* inside a range, Pro drums' notes are drawn where the games play them: red as
     a yellow cymbal in yellow's column, and yellow, cymbal or tom, as a red pad in red's. This holds
     on the highway and the timeline, in the strike glow, and for a ghost difficulty. Whether a note
     is flipped is the game view's, so the editor can't disagree with the games: a mix takes effect
     from the first note at or after it, and one still on at a difficulty's last note carries into
     the next difficulty up. 4-lane and 5-lane drums are never flipped, as the games flip Pro only.
   - *Settled in CM3:* editing inside a range works on the pads drawn. A click hits the note drawn
     in the column, and the Place tool and the number keys place the pad drawn there, so yellow's
     column places red; the hover shows it as it will be drawn. A box selects by the columns drawn.
     A move across columns, by a drag or `Alt`+arrows, gives each note the pad drawn in the column
     it lands in, at the tick it lands on, so red moved one column right inside a range is blue.
     When that isn't the same lane step for every note, the move is made as removing the notes and
     adding them again, in one undoable step. A move only in time keeps the pads stored, so a note
     moved into or out of a range is then drawn in the other column.
   - *Settled in CM3:* the phrase rail ([C8](#c8-phrases-and-events) item 1) beside a drums lane
     has a last row, **Disco flip**, with the ranges of the lane's difficulty, on every drum variant.
     A range a loaded file never ends runs to the far end of the view. The Phrase tool's picker
     offers **Disco flip** on Pro drums only, and draws a range on the lane's difficulty. Ranges are
     selected, moved, resized and deleted in the rail as phrases are, each drag one undoable step,
     and Delete removes them with the rest of the selection. A drag that would overlap another
     range, or cover a mix with another setting, is refused in the status. A range without an end
     can be selected, deleted and given an end in Properties, but not moved, nor its start changed,
     until it has one.
8. **4-lane ↔ 5-lane conversion helper.**
   - Converts a whole drum part with the fixed mapping in [§7.6](#76-drum-layout-conversion).
     Clone Hero and YARG each convert in their own slightly different way when they play one
     layout on the other. This mapping is Chart Builder's own, chosen to round-trip.
   - It is one undoable command. Notes it can't place (a second note landing on a pad that
     already has one on that tick) are left out. They are listed, with their times, in a report
     when the command finishes. Undoing the command brings them back. Lint can't see notes that
     are no longer in the chart, so the report is the only place they are listed.
   - *Settled in CM3:* of two notes landing on one pad, the one that maps back to where it came
     from is kept (the blue tom on 5-lane blue, the green cymbal on 5-lane orange). Converting
     5-lane to Pro, a tick where one difficulty's note becomes a cymbal and another's a tom on the
     same pad takes the hardest difficulty's, as `ChartToMidi` does, and the other note is listed
     too. A fifth-pad note in a 4-lane or Pro chart converts as a green tom.
   - *Settled in CM3:* the report is a message. When it lists notes, or the drums were 4-lane, it's
     a warning and the Messages dialog opens on it; otherwise it only says the drums were converted.
9. **Drum variant.**
   - Switching between 4-lane and Pro is a song setting (`pro_drums`), not a conversion: a 4-lane
     chart's tom markers are kept, so switching back to Pro brings them back.
   - *Settled in CM3:* the games read any tom marker as Pro drums, whatever `pro_drums` says
     (`DrumsLayouts`). So the markers of 4-lane drums are kept only while the chart is open: saving
     leaves them out, and CHR045 says so until then.
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
   - *Settled in CM3:* both views have three columns (a timeline's rows), plus the open note's. A
     column's black fret has its first half across the view, the left of a highway's column and the
     top of a timeline's row, and its white fret the second. A click places or picks the fret of the
     half it's in, or the other fret when that's the only one there, and a drag into the other half
     switches the selected notes between white and black when they're all one or the other. A gem
     alone fills its column; in a barre, the black covers the top half of the white. The keys `1`–`6`
     are white 1–3, then black 1–3.
2. **Chords**, including the "barre" chords where a white and a black share a column. As in
   5-fret, an open note can be placed in a chord with frets, which CHR022 flags.
3. **Sustains, forced HOPO, forced strum and tap**, as for 5-fret ([C5](#c5-5-fret-notes)),
   including forcing per chord.
4. Star power, solos and sections work as for 5-fret.

Acceptance criteria:

- **AC C7.1** Every one of the 6 lanes plus open, a barre chord, and each forcing kind save and
  read back unchanged.

### C8. Phrases and events

**Behaviour:**

1. **Star power** phrases. Every difficulty shares them, as MIDI stores them (pitch 116).
   - *Settled in CM3:* the **Phrase tool** draws a phrase of the kind picked beside it in the
     toolbar, from the grid step pressed to the one let go, either way, as one undoable step. A click
     adds nothing. The picker offers only the kinds the part in focus can have: star power, solo and
     big rock ending on 5-fret and GHL; star power, solo, drum fill, roll and trill on drums, where a
     fill drawn at or after `[coda]` is made a big rock ending (item 4). Pro drums also offer disco
     flip ([C6](#c6-drums-4-lane-pro-and-5-lane) item 7). When the focus moves to a part that can't
     have the kind picked, or the drums stop being Pro, it goes back to star power. New rolls and
     trills reach Expert only ([C6](#c6-drums-4-lane-pro-and-5-lane) item 6). In the phrase rail, the
     kind of its row under the pointer is the kind drawn, for the preview and for the drag, whatever
     the picker says; the picker itself stays as it was.
   - *Settled in CM3:* each lane draws its part's phrases as bars in a **phrase rail**: right of a
     highway's forcing marks, and above a timeline's. The rail has a row for each kind the part can
     have, in the picker's order, so overlapping phrases don't hide each other; a drums big rock
     ending is in the fills' row. Each row's background is a dark shade of its own kind's colour,
     naming that kind in a tooltip while the pointer is over it. With any tool, pressing a bar selects
     its phrase (Shift adds it or takes it away). Dragging a bar's body moves the selected phrases in
     time to the grid, keeping their lengths; dragging within a few pixels of an end moves that end to
     the grid, never closer than a grid step to the other. Each drag is one undoable step. Delete
     removes the selected phrases, with any selected notes and tempo markers, in one step. Selecting
     notes or tempo markers drops the selected phrases, and the other way round, unless Shift adds
     them. With the Select, Place or Phrase tool, dragging an empty part of a row draws a phrase of
     that row's kind, as the Phrase tool does in the lanes; the placement line across the lanes and
     the mark in the row show where, and the pointer is a cross there. Over a bar's body it is a move
     arrow, and over an end a resize arrow. On drums, a last row holds the lane's difficulty's disco
     flip ranges ([C6](#c6-drums-4-lane-pro-and-5-lane) item 7), which are edited the same way.
   - *Settled in CM3:* star power and solos also tint the lanes faintly behind the notes. Fills, big
     rock endings, rolls and trills were drawn only in the rail. *Settled:* every phrase kind and
     disco flip range tints the lanes faintly behind the notes, each in its kind's colour, with its
     start, and its end where it has one, marked in that colour; where ranges of different kinds
     overlap, their tints show through each other.
   - *Settled in CM3:* `S` ([§9](#9-keyboard-shortcuts)) puts star power over the selected notes:
     from the first one's tick to where the last to end is let go, or a tick past it when it has no
     sustain, so it covers every note as the games read markers. With no notes selected, it covers
     the span selected, in the focused lane's part. When a star power phrase already starts and ends
     exactly there, `S` removes it instead. With nothing selected, the status says so.
   - **Generate star power** proposes star power phrases over the selection or the whole part of a
     5-fret (keys included), GHL or drums lane, placed so a player has enough to activate going into
     the part's hardest passages. The author picks, for each run, about how many **seconds apart** phrases are
     (default 22, as the official charts are), and whether they're **charged for the hard passages**
     (default on) or only spread evenly. The window lists the proposals to keep or drop, shows them
     on the highway and the timeline, and adds the accepted ones as one undoable command. CHR066's
     fix in Chart Builder opens the window with the defaults over the whole part; it has no Fix All.
     The library's adds what it proposes, all at once.
   - *Settled:* phrases are placed against the part's highest difficulty with notes, and each
     covers a note on every difficulty with notes, so none is empty on one (CHR065). The count is
     the part's playing, in seconds with its rests left out (a gap between notes of 4 measures or
     8 s), over the spacing, and at least four, a full meter, for the whole part; over a selection,
     at least one. Fewer are proposed where fewer fit, but one wherever any fits. Over a selection,
     they're spaced from the star power just before and after it too, up to a spacing away.
   - *Settled:* a phrase starts on a measure line, or on a run's first chord where it's on a beat,
     and ends at a run's end or on the last chord before a measure line. A run is chords between
     rests of a beat and sections, as Reduce splits them ([C9](#c9-difficulty-authoring)). As the
     official charts end one, it runs on past its last chord's sustains to the measure line after
     them, where that's within a beat, or else to the next beat line, but never over the next chord.
     It has 4 to 40 chords, lasts no more than 10 s, and is best about a measure long (0.75 to 1.25),
     as most official phrases are; up to 4 where the part is too sparse for shorter. It doesn't
     cross a section, a solo's start or end (it can be inside a solo), a big rock ending or a drum
     fill, and keeps 4 measures clear of every other star power phrase, those already there, which it
     never removes; the official charts never have two closer than 2, and seldom closer than 5. It
     completes at least 2 measures of playing before the part's last note, so its star power can be
     used. Better spots start on a measure line on a chord, end a run, end on a sustain of a beat
     or longer (to whammy), and have about 7 chords, the official charts' median on Expert.
   - *Settled:* the meter is simulated as the games fill and drain it, for a player who hits every
     note and never whammies: a phrase gives a quarter of the meter, activating needs a half and
     spends it all, and a full meter lasts 8 measures, by the time signatures in force (a measure
     of 6/8 counts as one, as YARG drains it); a phrase completed while star power is on adds 2
     measures to it, up to a full meter's worth left.
   - *Settled:* the hard passages are found from the difficulty model
     ([C9](#c9-difficulty-authoring)): each stretch of 2 measures from each measure line is scored
     on its own, so endurance built up before it doesn't rank later ones higher; stretches without a
     note are left out; the scores are smoothed, and a peak is a hard passage when it rises above the
     higher of the lowest points between it and a higher peak either side (with no higher peak after
     it, the side before alone) by 15% of the part's range and 5 points at least, and is no easier
     than the part's median. One at the very start rises from nothing, so it isn't one. It starts
     where the scores rise halfway to its peak, and two less than 8 measures apart are one.
   - *Settled:* passages are charged for most prominent first, each where the plan with it still
     works, using at most three quarters of the phrases. For each, in time order, the meter is
     simulated with a player activating at each passage planned before, and it's given the phrases
     that bring the meter to a half: the last completing from half a measure to 4 measures of
     playing before it (best 1), the first of two starting after the activation before has run out
     and within 16 measures of playing before it. A passage still in the star power of the one
     before isn't charged for. The rest are shared over the stretches of playing between the
     phrases placed, one at a time to whichever would then have the most room, each at the best spot
     near its even share of the stretch. The stretch the part starts with has its first phrase half
     a share in, as the official charts give theirs early (about 10 s in). One that would put star
     power on over a passage charged for, so it can't be activated there, is left for the next best
     spot. Then a stretch longer than 45 s is given one more in its middle, up to two past the
     count. Without any passage, or with
     charging off, the phrases are spread evenly. The same chart always gets the same phrases.
   - *Settled:* **Generate star power…** is beside Generate fills, for a 5-fret (keys included), GHL
     or drums lane with notes. It and Generate fills share one window; its options are in the window's toolbar, kept
     while the editor is open: the spacing in seconds, whether to charge for the hard passages, and
     Existing Star Power (Replace, the default, or Add Alongside). Over the whole part in Add, it's
     refused while the part has star power, as the author's phrases are theirs to keep; over a
     selection it proposes beside them. The window lists the regions, each kept or dropped, previews
     them on the highway, and shows the meter and the hard passages on the whole-song timeline.
     Apply (Ctrl+Enter) adds the kept ones as one undoable command; Cancel adds nothing, and the
     chart is unchanged until Apply. When nothing fits, the preview says why: a part star power
     isn't generated for, no notes, star power already there, or no spot.
2. **Solos** (pitch 103), drawn as a shaded band with a note count.
   - *Settled in CM3:* the count is drawn at the solo's start. It counts the notes the solo covers on
     the difficulty shown, a chord once.
3. **Sections** in `EVENTS`. They are named freely and shown in the overview strip and in a new
   section list beside it. Sections can be renamed in the list, and **Insert section** takes the
   name from a menu of common ones (Intro, Verse 1, Chorus 1, Bridge, Solo, Outro…).
   - *Settled in CM3:* the list is the **Sections** tab, in the left panel after Chart settings. It
     lists every text event of `EVENTS` in time order, each with its time and its measure and beat:
     a section by its name; a song event (item 5, and `[coda]`) by what it is, set apart from the
     sections; anything else as it's written. Under them, headed by the part, are the focused lane's
     part's own text events, when it has any (item 6). Each row has a go-to, which moves the cursor
     there and leaves the focus where it is; its name or text, typed to rename it; its tick, scrubbed
     or typed in whole ticks to move it, the chart following it as it's dragged and the move applied
     as one undoable step once it's been left alone; and a delete. Each is one undoable step. A section is renamed by its name and keeps its
     `[section …]` or `[prc_…]` style, and an empty name is refused. The list follows edits, undo and
     redo, a drag's preview and the focus, and a row whose event hasn't changed keeps what's being
     typed in it.
   - *Settled in CM3:* **Insert section** inserts `[section <name>]` at the cursor, on the grid. Its
     menu has the common names and **Custom…**, which inserts one named "Section" (or the next free
     "Section 2", "Section 3"…) with its name ready to type over. Where there's a section at that tick
     already, it's refused rather than stacking two, as is moving or renaming an event into one there.
     Only a duplicate an edit makes is refused, so a loaded chart's sections already sharing a tick
     can still be renamed and moved. A name with `[` or `]` is refused, as the event's own brackets
     end at the first `]`. A tick typed far past the end of the song (later than an hour in, or 64
     measures past the chart's last note, event or tempo marker) is refused, for any text event.
   - *Settled in CM3:* `Ctrl`+`↑` ([§9](#9-keyboard-shortcuts)) moves the cursor to the start of the
     next section after it, and `Ctrl`+`↓` to the previous one's, in either view: up is later, as `↑`
     is on a highway and `PgUp` is. Only sections count, and sections at one tick are one start. With
     none that way, the cursor stays and the status says so.
   - *Settled in CM3:* each lane draws a section as a faint line across the lanes and the labels'
     margin, with its name in the margin: in a highway's rail above the measure number, cut short to
     fit, and in a timeline's ruler after the measure number and any tempo there.
4. **Big rock ending** (all five pitches 120–124 held together on a guitar or bass track) and
   `[coda]`. On drums, the same pitches are fills, and a big rock ending is the fill markers after
   `[coda]`. Chart Builder shows those as a big rock ending, not a fill, and the fill rules (CHR042,
   CHR043) skip them. No code reads big rock endings or `[coda]` today, so this is new.
5. **Song events**: `[music_start]`, `[music_end]`, `[end]`. The editor places all three itself
   (`SongEventPlacement`), and works out where they go again after every edit, undo or redo, and
   whenever the stems or the delay change. Any position a user or a loaded chart gave them is
   overwritten.
   - `[music_start]` is where the stems are first heard. A stem is heard where it reaches -60 dBFS
     for at least 50 ms (`AudibleSpans`); the preview and crowd stems don't count. It goes no later
     than the first note. With no audio, or none of it heard, it's the measure line at or before the
     first note, or tick 0 with no notes.
   - `[music_end]` is where the stems are last heard, but no earlier than the last note's end. A
     sustain counts, and so do the vocals. With no audio, it's the last note's end.
   - `[end]` is the first measure line after `[music_end]`.
   - Audio times become ticks through the tempo map, less the delay. The start is rounded down and
     the end up.
   - The editable model never holds them: commands undo by an event's place in `EVENTS`, so moving
     them outside the undo stack would shift what a later undo takes back. They're taken out as a
     chart is opened or recovered, duplicates included, and written into the snapshot a save,
     first save or **Export copy** writes, one of each. A save waits for the stems to be read
     (at most 30 s, then it goes with what was last heard). **Save and pack** saves first when the
     file's song events aren't where they now go.
   - *Settled in CM3:* **Insert event**, in the Sections tab, inserts `[coda]` at the cursor, on the
     grid. Inserting, moving or deleting `[coda]` makes the drum fills and big rock endings what it
     then decides, in the same step (item 4).
   - The Sections tab lists `[music_start]`, `[music_end]` and `[end]` among `EVENTS`' events, as
     read-only rows: they can be gone to, but not moved, deleted or renamed. Inserting one, or
     renaming another event into one, is refused, and the status says it's placed automatically.
6. **Free text events**, in `EVENTS` or on a part track, for anything else. Events the model
   doesn't know are shown and kept, not deleted.
   - *Settled in CM3:* **Insert event**'s **Text event…** inserts `[event]` in `EVENTS` at the cursor,
     on the grid, with its text ready to type over, and **Text event on *part*…** puts one on the
     focused part's track. Free text is edited as it's written, brackets and all; text left empty is
     refused, as deleting is how one is taken away. A part's text events are free text, so none is
     refused for what it says.

Acceptance criteria:

- **AC C8.1** Every phrase and event kind round trips. A free text event the model doesn't know
  survives an unrelated edit and a save.

### C9. Difficulty authoring

**Behaviour:**

1. **Copy difficulty** copies one difficulty's notes and its per-difficulty markers (forcing, taps,
   disco flip) to another, replacing it after a confirmation. It can be undone.
2. **Reduce** makes a draft of a lower difficulty from a higher one, as a starting point to edit.
   - The **Reduce** dialog shows the rules for the part's family, prefilled from the defaults in
     Settings ([§10](#10-settings)). Keys is a 5-fret part and reduces by the 5-fret rules; every
     part Chart Builder edits can be reduced (*revised for #116*). Changes made in the dialog apply to that run only. Nothing is
     stored with the song. The shipped defaults:

     | Rule | Hard | Medium | Easy |
     | ---- | ---- | ------ | ---- |
     | Max chord size (5-fret / GHL) | 2 | 2 | 1 |
     | Frets used (5-fret) | all | G–B | G–Y |
     | Columns used (GHL) | all | 1–3 | 1–2 |
     | Thinning grid (on average no closer than) | 1/16 | 1/8 | 1/4 |
     | Bursts no closer than | 1/16 | 1/16 | 1/8 |
     | Longest burst (chords or hits) | 3 | 3 | 3 |
     | HOPOs and taps, as played | kept | kept | kept |
     | Sustains shorter than a beat | kept | kept | removed |
     | Sustains held while other notes are played | removed | removed | removed |
     | Open notes in phrases with pressed notes | fretted | fretted | fretted |
     | Drums: pads per hit (plus kick) | 2 | 2 | 1 |
     | Drums: kick | kept | thinned to 1/8 | thinned to 1/4 |
     | Drums: 2× kick | removed | removed | removed |

   - Chord reduction keeps the outermost frets. Reducing to a single note keeps the lowest fret.
     Frets outside the allowed range are moved inward, with the melody's shape kept as far as
     possible. For GHL, a white and a black on one column count as one column.
   - Thinning keeps the chords that matter most first: strong beats, sustains, chords, the first
     note after a rest, turns in the melody, and the ends of phrases. Chords are on average no
     closer than the thinning grid, and only a short burst the source has comes closer, down to
     the burst's grid. Every gap is a fraction of a beat, never a time, so a faster song is harder.
     Every star power phrase and solo keeps at least one note.
   - The lane limits apply only to what Reduce makes. The editor still lets the charter place notes
     on any lane on any difficulty.
   - **Reduce all** fills Hard, Medium and Easy in one run, each from the difficulty above it, so
     each lower rhythm is a subset of the one above.
   - Reduce can cover the whole part, the selection, or the section at the cursor, leaving the
     target's notes elsewhere as they are.
   - The dialog checks the draft: its lint findings, each difficulty's suggested rating and peak
     notes per second ([C18](#c18-preview)), and a warning where a difficulty isn't easier
     than the one above it.
   - The result is shown as a ghost over the target difficulty, then applied as one undoable
     command.
3. **Difficulty letters** in the part picker show which difficulties have notes
   ([C3](#c3-editor-layout)).

*Settled in CM5:*

- **Where they are.** Copy difficulty and Reduce are in each lane's **Edit** menu. Copy difficulty
  asks first when the target has notes, leaves out 2× kicks below Expert, and copies the drum
  mixes, which hold disco flip. Tom markers and phrases are the part's, so they're shared already.
- **The dialog.** Reduce opens over the Editor with the rules for the part's family and target,
  from the defaults. Each change draws the draft again over the target, whose own notes fade
  behind it. Apply (Enter) adds it in place of what the target had, as one step; Cancel (Esc) adds
  nothing. An edit to the chart while it's shown drops the draft. Settings' **Default reduction
  rules** opens the same dialog on the defaults, with **Reset** beside it.
- **Thinning** ranks each chord by where it falls (a measure's first beat, a beat, then each
  halving, triplets just after the halving they're nearest) and takes them in that order where no
  chord taken is closer than the grid, on either side.
- **Chords** keep their lowest and highest columns, then inner ones up to the limit. A chord past
  the highest column moves down as a whole; only notes it would push below green are pressed
  together, the longer sustain kept. GHL notes keep their colour. Sustains a reduction runs into
  the next note on their lane end there.
- **Drums** are thinned kick and pads together, then the kicks on their own grid. Each hit keeps
  red first, then cymbals, then toms, by the pads as shown inside a disco flip.
- **GHL's columns.** The table's Hard "all" and Medium "1–3" are the same, as GHL has three
  columns; the table ships as written.

*Improved after v1 ([#90](https://github.com/jcub1011/Chart-Packer/issues/90)):*

- **Phrases.** A phrase is a run of chords split at a rest of a beat or longer, from one chord's
  end to the next chord, and at sections. Open notes split none and take no part in the melody.
- **Thinning** scores each chord: −1 for each step down from a measure's first beat (a beat, then
  each halving, triplets just after the halving they're nearest), +1.5 for a sustain of a beat or
  longer, +0.5 for a chord of two columns or more (on drums, two notes or more), +1 for the first
  chord after a rest, +0.5 for a turn of the melody (by each chord's lowest column; not drums),
  and +1 for a phrase's first or last chord. So a long sustain on the "and" beats a plain note on
  the beat. Chords are taken best first, the earlier on a tie, where no chord taken is closer
  than the grid's step or the minimum gap in milliseconds, on either side (the milliseconds were
  later removed: see below). Before them, each star
  power phrase and solo takes its best chord, where that keeps the gap. The drums' kick pass keeps
  its own grid only.
- **Chords** keep their lowest and highest columns, then inner ones up to the limit, as before.
  Then each phrase is fitted to the range as a whole:
  - a phrase inside the range doesn't move;
  - a phrase that fits the range moves down as a whole, so an orange–blue trill on Medium becomes
    blue–yellow, not blue–blue;
  - a phrase wider than the range is fitted to it with each step kept going up, down or the same
    where it can be, near the phrase scaled into the range. So green to orange on Easy still rises
    over green, red and yellow, rather than ending yellow–yellow–yellow.

  Only notes a chord would push below green are pressed together, the longer sustain kept.
- **Reduce all** is the last choice of **To**: Hard, Medium and Easy. Each one ticked is made from
  the nearest difficulty above it with notes, as it will be: Medium from Hard's draft, or from
  Hard as charted when Hard isn't ticked. Those with notes start unticked, and Apply asks before
  replacing one. **Rules for** picks which difficulty's rules the fields show. The whole run is
  one undo step.
- **Over** picks what the run covers: the whole part, the selection, or the section at the cursor.
  It starts on the selection when there's one. The target keeps its notes, forcing and mixes
  outside the range, and the gap holds against its notes on either side.
- **The check** under the summary lists each difficulty's suggested rating and peak notes per
  second with the draft in place, where a difficulty is rated as hard as the one above it or
  harder, and the draft's findings: the 5-fret, drum and phrase rules on its difficulties, and
  CHR081 against the difficulty above. CHR065 flags a phrase a draft left empty.

*Improved to keep a song's character:*

A single gap between every two chords turned Medium and Easy into a steady pulse. It cut the
syncopation, gallops and short runs that make a part fun to play. A lower difficulty may now be
harder than another song's at the same level, as its rating says, so long as it keeps the song's
rhythm.

- **Fractions of a beat, not time.** The minimum gaps in milliseconds are gone, both the average
  gap and the burst's. Every spacing rule is a grid, so it reads the same at any tempo and in any
  time signature. A gap in time sat at a different place in each song's rhythm. At 154 BPM eighths
  are 195 ms apart, just under Medium's old 200 ms, so a steady eighth chug became quarters, or
  groups of three with bursts. A faster song now keeps as many notes a beat as a slower one, and
  is harder, as its rating says. A charter who wants it easier picks a coarser grid. Settings saved
  with a gap in milliseconds load as before, without it.
- **Bursts.** The thinning grid is now what chords keep on average. Any one more than the longest
  burst in a row span at least that many of its steps. Within that, a burst's chords can be as
  close as the burst's grid, never closer. So on Easy two or three eighths with room around them
  stay, while a run of sixteenths still thins to the beats. A longest burst of 1 keeps every two
  chords a step apart, as before. Hard's bursts ship as close as its grid, so Hard is thinned
  evenly. The drums' kick pass keeps its own grid only.
  - Only the source's own bursts count. A burst is a run of two or more chords, each closer than
    the grid's step to the next, with a step or more on either side, that fits within the longest
    burst's steps. A longer run is a steady stream, which thins evenly. Allowing bursts in a stream
    cut it into groups the chart never had.
- **HOPOs and taps** are kept on every difficulty by default. Each chord plays as the source's
  does: a strum, a HOPO or a tap, read with the song's HOPO threshold. Where leaving notes out
  would change that, the chord is forced to a HOPO or a strum. A HOPO thinned past the threshold
  stays a HOPO, and a strum thinned within it stays a strum. With the rule off, forced HOPOs and
  taps are removed and forced strums kept, as before.
  - A chord on the same frets as the chord before it is a strum, never a HOPO. Thinning and
    fitting can press frets the source had apart into one, and a HOPO on the note just played is
    awkward. Its forcing is left out, so it reads as a strum. A tap stays a tap.
- **Anticipation.** A chord off the beat, with nothing charted on the next stronger position before
  the next chord, anticipates that position. An example is the "and" of 4 held over an empty
  downbeat. It scores as that position less 0.5, so it's kept over beat 4, but not over a chord on
  the position itself. The next stronger position follows the time signature. A chord off every
  grid anticipates nothing.
- **Riffs.** Measures charted the same are thinned the same. That means the same chords at the
  same places, each with the same notes and long sustain or not.
  Each chord's score is its mean across them. After thinning, a chord most of those measures left
  out is left out of the rest, unless that would leave a star power phrase or solo empty. A chord
  most kept is then added to the rest where the spacing allows.
- **Sustains.** On a difficulty that removes sustains shorter than a beat, a sustain a burst cuts
  short of a beat is a plain note.
- **Open notes in phrases with pressed notes.** Going between an open and a fret is hard to play,
  so Hard, Medium and Easy fret the opens of a phrase that has opens played alone and pressed notes
  both. Every open in the phrase becomes green, those before and after its frets as well as those
  between them. An open in a chord with frets becomes green pressed with them, so `o+R` is `G R`,
  and Easy keeps only the green. Each fret next to an open alone, with the same chord repeated
  beside it, moves up one and is kept off green, so the change is still a move. An Expert
  `R o R o G o G o` is `Y G Y G R G R G` on Hard. Frets further from an open keep their place, so a
  fill in the same phrase keeps its orange. The phrase is then fitted to the range as a whole, its
  steps judged on the source's frets. The lift follows the source, so Medium keeps it where its
  opens are thinned out. A fret next to an open only once thinning has left them side by side
  moves up one too. A phrase without an open alone keeps its opens, such as one of opens alone, or
  of `o+R` chords and frets. So does every phrase where green is the highest fret, since no fret
  can move off it. On GHL an open becomes the first white fret.
- **A chord change over one fret.** A step of the melody goes the way the chord's lowest fret goes,
  or, where that stays, the way its highest goes. Easy keeps a chord's lowest fret alone, so
  green–red then green–yellow was green twice, and the change was lost. Where a phrase loses a
  step like that, it's fitted again, keeping each step's way and as near its frets as it can. So
  that change is green then red on Easy. Medium keeps the chords, which show the change, and
  doesn't move.
- **A chord as wide as the range.** Fitting a phrase may make a chord narrower, its top pressed
  in, where its full width would lose a step. A chord as wide as the range has nowhere to go, so
  green–blue then red–orange on Medium was green–blue twice. Now it's green–blue then red–blue,
  and red–blue after that is red–yellow, so each change shows. A chord stays a chord, and is
  moved before it's narrowed. A run of one chord repeated is one step of the melody, so a long run
  counts no more than a short one.
- **Sustains held over other notes.** A sustain the draft holds past the start of the next chord,
  so held while other notes are played, is a plain note on every difficulty Reduce makes. Its chord
  keeps its forcing, so a tap stays a tap. It's judged on the draft, so a sustain whose notes under
  it are thinned away stays a sustain, and one that ends as the next chord starts is no extended
  sustain. A set saved before the rule takes the shipped value.

Acceptance criteria:

- **AC C9.1** Reducing Expert to Easy never makes two chords or notes closer than Easy's burst
  grid, and any one more than its longest burst in a row span that many steps of its thinning grid.
  It is undone in one step.
- **AC C9.2** Reduce all fills Hard, Medium and Easy as one undo step, and each one's chords are on
  ticks of the difficulty above it.
- **AC C9.3** Every star power phrase and solo with notes on the source keeps at least one note on
  the draft.

### C10. Playback

**Behaviour:**

1. Playback uses `StemPlayer`. It plays every stem sample-aligned, and has mute and solo per stem,
   volume, speed from 25% to 200% with **pitch correction** (on by default, as in the inspector),
   and sample-exact seeking.
2. **Play from cursor** (Space), and a **loop region** for repeating a hard passage at a lower speed.
   - The region is drawn with the toolbar's **Loop tool**, or with `L` held, which holds the tool on
     while it's down: a drag over the lanes sets its start and end on the grid, never shorter than a
     32nd note; a drag on either end resizes it; a drag on its body moves it keeping its length. A
     region drawn goes back to the tool picked before it, as the Sync tool does.
   - Drawing, moving or resizing the region doesn't start the song repeating it: whether it
     repeats is turned on and off with the transport bar's **Loop** button, or with a quick tap of
     `L` (a short press that touched nothing; held longer, `L` only holds the tool). The button is
     disabled while there's no region, and turning repeating off keeps the region drawn.
   - Every lane draws it as a light green box across it, whether or not the song repeats it just
     now; the bottom timeline marks its start and end with blue lines.
   - Hovering the region shows a small X at its top right, in the lane hovered, which clears it,
     whatever tool is picked. The middle button clears it while the Loop tool is active.
3. **Metronome**: a click on each beat, with an accented click on each measure, from the tempo
   map. It is mixed in after the time-stretcher ([§4.4.1](#441-playback-changes)), so it stays
   sharp and in time with the stems at any speed.
4. **Hit sounds**: a short sound on each note as it reaches the strike line, mixed in the same way.
   There is one sound per part, and a pad sound for each drum pad. The volume is separate from the
   stems'. *Settled in CM2:* hit sounds follow the game view ([§4.3](#43-writing-midichartwriter))
   of the lanes shown: a fret sound for 5-fret and GHL, and one per pad for drums.
5. **Latency compensation.** Everything drawn against the audio (the cursor, highway, waveform,
   spectrogram and overview) is drawn at **`SmoothPosition` − latency × speed**
   ([§4.4.1](#441-playback-changes), [C11](#c11-calibration-tool)).
   - `Position` already allows for the audio waiting in SDL's stream queue.
   - The calibration covers everything after that: SDL's device buffer and the output device's
     own latency, which the app can't see.
   - *Changed for issue #140:* the app reads what it can of that latency
     (`DeviceLatency`): SDL's device buffer (`SDL_GetAudioDeviceFormat`'s sample frames at the
     device's rate) and, on Windows, what WASAPI reports for the endpoint, the stream latency and
     device period of a shared-mode stream (`WindowsAudioEndpoint`). It comes with the device
     (`AudioDevice.DetectedLatencyMs`), and is **added** to the calibrated latency, so the
     calibration is only what's left: a Bluetooth link's delay, the display's, and whatever else
     the app can't see ([C11](#c11-calibration-tool) item 3). The latency drawn with is the two
     added up (`LatencyLookup.Milliseconds`). `Position` is unchanged.
   - The latency is in real time, so it is scaled by the playback speed to give song time.
   - *Changed for issue #103:* pausing leaves the audio, the cursor and the playhead where they were
     drawn, and playing holds there until it's heard, so neither jumps
     ([§4.4.1](#441-playback-changes)).
6. **Following.** While playing, the view keeps the cursor at a fixed place, a quarter of the way
   from the strike end. Scrolling pauses following until playback is restarted. *Restated in
   CM2:*
   - The cursor is always at that place: it's the lane's strike line.
    - Scrolling moves the song past it a grid step a notch, through the tempo map, so the strike
      line stays on the grid across tempo and time-signature changes. The cursor moves with it,
      and the audio seeks there, playing or paused. A touchpad's part-notches add up to grid steps.
      With `Shift` held, a notch moves four grid steps instead.
   - Pausing leaves the cursor where playback stopped, and Space plays from there.
   - Each lane's look-ahead, the seconds it shows beyond the strike line, is its own. It's set
     from the lane's header, or with Ctrl+wheel, `+` and `−`, and saved with the lane layout. A
     lane that hasn't had one set uses the **Editor highway speed** setting's: 2 s at 1×.

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
the display. The same window also measures an instrument device's own lag (item 2), which the
playtest takes off its inputs ([C19](#c19-playtest-and-instrument-input)).

**Input:** none.
**Output:** a latency in milliseconds for the current audio output device, or an input offset in
milliseconds for an instrument device.

**Opening:**

The tool is a window in `ChartShared.UI` (`CalibrationWindow`, opened through
`CalibrationWindowService`), so it belongs to neither app. Both open it in their own process, and
both read and write the one shared latency file ([§10](#10-settings)). It opens:

- from Chart Builder's **Settings**;
- from Chart Builder's transport bar;
- from **Calibrate…** in Chart Packer's Settings, which pauses the song and reads the latency again
  when the tool closes. The tool starts on the device the song plays on, so it names that device and
  shows its latency even when the tool's own output can't be opened;
- **automatically, once**, the first time Chart Builder opens on a device that has no calibration.
  This can be skipped.

Its input-device test opens from **Calibrate…** beside a device's offset in Chart Builder's Input
devices window, over that window, pausing the Editor. A **Bind** still waiting for a control stops
waiting first, so the test's taps never bind one. Chart Packer has no instrument input, so it has
only the audio/visual test.

Only one test is open at a time. Opening the tool again, for either test, brings the one open to
the front; when that's from the Input devices window, it says the tool is already open, and the
device isn't measured.

**Behaviour:**

1. **The test.** The window has the shared title bar (`TitleBar`), and two columns: the highway on
   the left, and on the right the controls in sections, as the song inspector lays them out
   (`Inspector.axaml`): the output device, the latency (with **Reset to 0**), Compare, and how to
   calibrate. *Changed for issue #140:* the latency set here is what's beyond the latency
   detected from the device. Under it, the tool shows the detected latency and the two added up
   ("Plus 23 ms detected from the device: 35 ms in all."), or that nothing detected is added.
   Cancel and Save are in a toolbar along the bottom.
   - The highway is the real one (`NoteHighwayView`), as the song's preview draws it, not a
     drawing of its own. What it draws, and when the frame reaches the screen, is what the user
     will be judging against while charting.
   - It plays an endless loop at **80 BPM in 4/4** (`CalibrationChart`): an **open note on every
     beat**, struck at the strike line with the real hit animation (`StrikeGlow`).
   - The **metronome** (`MetronomeSchedule`, `Synth.Click`) clicks on every beat, over a silent mix
     played through `StemPlayer` and `IAudioOutput` ([§4.4.1](#441-playback-changes)), the same
     output path as a chart's stems, so it has exactly their queueing and device latency.
   - **The first beat of each bar is marked**: an accented click and a measure line. One beat
     lasts 750 ms, so without the mark a latency of 400 ms would look and sound the same as
     −350 ms. With it, the pattern repeats every 3 s, and the whole ±500 ms range can't be
     mistaken for anything else.
   - **As playback draws it.** The highway is drawn at `SmoothPosition` − latency
     (`LatencyCompensation.Drawn`), exactly as a song's highway is ([C10](#c10-playback)), the
     latency being the value plus what's detected from the device (*changed for issue #140*). The
     clicks never move: changing the latency **moves the notes**, so the value found is the one
     playback needs, through the same code.
   - The user adjusts the latency until each click is heard as a note hits the line. Controls:
     - a number box, dragged or typed in as the inspector's offsets are (`DragScrub`), with a
       button back to the saved value;
     - buttons for −10, −5, −1, +1, +5 and +10 ms;
     - a slider from −500 to +500 ms (Bluetooth output can be well over 200 ms late);
     - the arrow keys (Shift for ×5).

     The notes move as soon as the value changes.
   - **Compare.** While **Hold for no calibration** or Space is held down, the notes are drawn with
     no latency but what's detected from the device (*changed for issue #140*), and the highway
     says so. Let go, they're drawn with the value again, so the user
     can see how much it moves them. The value itself doesn't change. Space never presses the
     button with the focus, and the window losing focus lets it go.
   - **Tap along** (*changed for issue #105*). **Start** hides the notes, so only the sound is
     followed. The metronome gives way to the test's own clicks (`CalibrationViewModel.TapClicks`).
     There is a quiet bar to get ready ("Get ready"), then a bar counted in on screen, 4, 3, 2, 1,
     at the clicks' tempo, still with no sound. Then the user taps with each of the 16 clicks that
     follow, the first on the beat after 1 ("Click n of 16"), and nothing clicks after. The metronome
     comes back once the test ends. The count is timed to the clicks' beat with no latency, so the
     value being measured never moves it.
     - **What counts:** any key but Escape, and a press on the highway. In Chart Builder, any
       instrument device counts too (`ICalibrationTaps`, `CalibrationTapInput`, over the Input
       devices' hub, as item 2 counts presses).
     - **Timing:** each tap is placed on the clicks' timeline (`SmoothPosition`, less how long ago
       it came), less the device's own offset from item 2. That offset is the screen's lag, the
       device's and the player's habit, so the device's lag and the habit drop out. What's left is
       the audio's lag less the screen's: the latency. A key or a press with no offset counts as
       is.
     - **Scoring** (`AudioTapTest`): as item 2's, except that a tap counts against a click from
       250 ms before it to 500 ms after it, so a Bluetooth latency isn't taken for the next beat.
     - **The result** goes in the latency, clamped to ±500 ms, with the spread and the taps used. It
       isn't saved, so the user can check it against the notes before **Save**. *Changed for issue
       #140:* the taps measure the whole latency, so what's detected from the device is taken off
       it first, and the status says so. A change of output
       device stops a test that's running.
   - The highway is only watched: it can't be scrubbed here, and Shift doesn't show its whole
     chart.
2. **Input devices.** The same window, with the device test's controls on the right
   (`DeviceCalibrationViewModel`, `DeviceCalibrationPanel`): the device and its saved offset, a
   **Start** button, and how to calibrate. Chart Builder hands it the device through
   `ICalibrationInput` (`DeviceCalibrationInput`, over the Input devices' hub), since
   `ChartShared.UI` can't see Chart Builder's input.
   - **Seen, not heard.** Nothing plays until Start. Then two bars lead in, "Get ready" for the
     first and a count-down of 4, 3, 2, 1 for the second, and **16 open notes** fall, one a beat at
     80 BPM, with no sound. The user hits the device as each reaches the strike line. "Note n of 16"
     shows where it is.
   - **What counts** (`PressDetector`): any key, button or MIDI note pressed, or the hat pushed any
     way, so a fret and a strum both count. Letting go doesn't, nor does a key repeating while it's
     held, nor an axis or a MIDI control, so a whammy bar, a tilt or a pedal is no tap. A MIDI note
     counts from the device's velocity floor.
   - **One clock.** The highway is drawn at the time since Start on `InputClock`, and the presses
     are timed by their own events on that clock, with no offset taken off. A press is measured
     against the note as it was drawn, so what's measured is how much later than the screen shows a
     note the device reports the hit: the device's lag, the display's and the renderer's, and the
     player's habit. The playtest judges presses on the drawn timeline too
     (`SmoothPosition` − latency), so the audio/visual latency drops out of both, and an offset
     measured here stays right when the audio/visual latency is calibrated again.
   - **Scoring** (`TapTest`): each tap is measured against its nearest note, ignoring taps more
     than half a beat from any (as in the lead-in); only the closest tap to each note is kept; taps
     more than 1.5 interquartile ranges from the median (and more than 5 ms) are dropped; with at
     least 8 left, the offset is their mean. The result shows the offset, the spread and how many
     taps were used. A spread over 30 ms suggests trying again. An offset beyond the ±250 ms an
     input offset can be is saved as the nearest it can.
   - **Try again** runs the test anew. **Save** stores the offset in the device's settings
     (`input-bindings.json`), keeping the rest, and the playtest takes it up at once; the Input
     devices window shows it. When the file can't be written, the tool says so and stays open, and
     the device keeps the offset it had. **Cancel** stores nothing.
   - **The keyboard** has an offset too. The window hands its keys on to the keyboard device, all
     but Escape, which cancels, before any button has them, so Space or Enter never presses one.
     Key events carry no timestamp, so a key is timed when it's handled, and its spread is wider.
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
     then load that device's latency (`AudioLatencyService`, in `ChartShared.UI`).
   - **Lookup order:** the stored value for the device's ID, then for its name, then a **global
     fallback**. When a device is found by name, its value is also saved under its ID if it has
     one, so a later rename doesn't lose it.
   - *Changed for issue #140:* the value found is what the device's latency is beyond what's
     **detected** from it ([C10](#c10-playback) item 5). While the file's `detect` switch is on
     (it is unless it's `false`), the detected latency is added to it; off, the value found is
     the whole latency. **Detect output latency** in both apps' Settings turns it on and off. It
     is kept in the shared file, so both apps and the tool always agree.
   - The tool always says which device it is calibrating.
4. **Where it applies.**
   - Chart Builder ([C10](#c10-playback));
   - **record mode**, where key presses are shifted by the latency before they're quantised
     ([C15](#c15-editing-ergonomics));
   - Chart Packer's inspector highway (`NoteHighwayView`) with its lane waveforms, lyrics lane and
     background, with the same scaling by speed. Chart Packer reads the value from the shared
     latency file ([§10](#10-settings)). The seek bar's waveform isn't moved: it marks what has
     played a column at a time, where tens of milliseconds don't show, and its position is the
     seek position. The merge window resumes from the audio's own position, not the drawn one.
5. **Saving.** **Save** stores the value in the shared latency file. **Cancel** restores the
   previous value. **Reset** sets 0, leaving only what's detected from the device.

*Settled in CM2:*

- **The first-use prompt** comes the first time a song plays in the Editor on a device with only
  the fallback. Each device is offered it once (`BuilderSettings.CalibrationPrompted`), whatever
  the answer.
- **The Settings rows.** The latency is an action-only row (`ActionSetting`), with a
  **Calibrate…** button, in both apps' Settings.

*Changed for issue #140:* the first-use prompt still comes on a device with only the fallback,
with or without a detected latency added, as detecting can't see a Bluetooth link's delay or the
display's. Below the latency row, both apps' Settings
have a **Detect output latency** toggle (`audio.detectLatency` in Chart Builder,
`playback.detectLatency` in Chart Packer), on by default, which sets the shared file's `detect`.

*Changed for issue #49:* the flashing light, the hand-drawn highway, the beeps and the tap test are
gone. The beeps were moved by the latency against a fixed flash (`CalibrationSchedule`), the
opposite of playback, which never moves the audio. The tap test's estimator (`TapTest`) now scores
the input-device test, in one phase.

Acceptance criteria:

- **AC C11.1** With the latency set to +100 ms, the highway is drawn 100 ms of song earlier than at
  0 ms (`LatencyCompensation.Drawn`), and the clicks are where they were.
- **AC C11.2** The input-device test's estimator is given simulated taps on its 16 notes: a known
  offset, normal jitter with σ = 20 ms, and 2 outliers of ±150 ms. Over 1,000 seeded trials, the
  median estimate is within ±2 ms of the offset, and 95% of estimates are within ±20 ms. Taps in
  the lead-in are ignored, and only the closest tap to a note counts.
- **AC C11.3** When the output moves to another device (`DeviceChanged`), Chart Builder and the tool
  load that device's stored latency, or its detected latency, or the fallback.
- **AC C11.4** The metronome clicks as each of the tool's notes is struck, accented on every fourth,
  the first beat of each bar.
- **AC C11.5** Cancel leaves the stored latency as it was before the tool opened.
- **AC C11.6** A button, key or MIDI note pressed is one tap, and letting go is none; a key held and
  repeating is one; a hat pushed any way is one; an axis, a MIDI control, a MIDI note under the
  velocity floor, or another device is none.
- **AC C11.7** Save stores the measured offset for the device, keeping its other settings, and has
  the playtest take it up.
- **AC C11.8** The audio tap test's estimator is given simulated taps on its 16 clicks: a known
  latency from −100 to +400 ms, plus a device offset that the taps have taken off, normal jitter
  with σ = 20 ms, and 2 outliers of ±150 ms. Over 1,000 seeded trials, the median estimate is
  within ±2 ms of the latency, and 95% of estimates are within ±20 ms. Taps with the count-in are
  ignored, the test's own clicks are only the 16, and a tap 450 ms late counts against its own click. Every device's presses count, each
  with its own offset.
- **AC C11.9** While detecting is on, a device's latency is its calibrated latency, or the
  fallback, plus the latency detected from it; while it's off, nothing is added. The tool edits
  and saves only the calibrated part, and its reset sets that to 0 (issue #140).
- **AC C11.10** The detected latency is the device buffer's frames at the device's rate plus the
  OS's latency, rounded to the millisecond and within 0 to 500 ms, and none when neither is known.

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

*Settled in CM4:*

- **Where the options are.** Each lane's header has an audio button, whose flyout picks the audio
  (the part's own stems, the mix, or any one stem), waveform or spectrogram, behind or beside, and
  each one's own options, and the onsets (C14). They're kept with the lane layout
  (`LaneAudioOptions`). The **Lanes show** setting picks waveform or spectrogram for a lane that
  hasn't been switched.
- **Resolution.** Every stem is read once at 5 ms a bucket (`WaveformResolution.High`), and drawn
  with a power of two of buckets to a bar, as many as fit in a screen pixel, so one read serves
  every zoom. The editor's waveforms come from one place, `StemWaveforms`, which also gives the
  bottom timeline its mix.
- **Stereo.** A stem's channels are read only when a lane first shows it in stereo
  (`StemPeaksReader`'s `channels`); until then it's drawn in mono. Left is drawn beside right on a
  highway, above it on a timeline, both at one gain.
- **Gain** is in dB, from −12 to +24, on top of the gain that brings the loudest peak to full
  scale.
- **Beside** is a column right of a highway's phrase rail. A timeline's strip is always drawn at
  full strength; Behind also draws the audio faintly behind its rows.
- **AC C12.1.** Pictures of the waveform are drawn within a budget of 6 ms in each 16 ms
  (`PaintBudget`). One there's no time for is stood in for by the neighbouring zoom's picture, and
  drawn once the frame is out.

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

*Settled in CM4:*

- **Levels.** Each byte is an absolute level, −120 dB plus half a decibel a step, a full-scale sine
  reading 0 dB. The contrast is applied as it's drawn, so changing it computes nothing again; the
  floor and ceiling are each lane's own (default −80 dB and 0 dB).
- **Low rows.** A row that holds an FFT bin takes the loudest of its bins. Below about 870 Hz the
  rows hold none, and each is read off a parabola through the levels of the louder bin either side
  and its neighbours.
- **Colours** run from black through violet, magenta and orange to pale yellow, blended in OKLab with
  lightness rising evenly (`SpectrogramColors`). A band preset dims the rows outside it to 30%.
- **Computing.** Columns are computed in chunks of 256 on threads below normal priority
  (`AnalysisWorkers`), the chunk nearest the middle of the view first, and frames two to an FFT.
  Chunks near the view come before onsets; the rest of the song after.
- **The budget.** A spectrogram counts at its full size from the moment it's started. One used in
  the last 2 s isn't released to make room for another: the newcomer is refused, and the editor
  says so, so two lanes that don't both fit don't take turns releasing each other. The budget is
  64 MB to 4096 MB.
- **Drawing.** As the waveform's, in pictures of 256 columns, each a column or the loudest of a
  power of two of them, drawn within the same budget, and drawn again as more of their columns are
  ready, at most every 100 ms. Behind the lanes it's at 45% opacity.

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
   - *Settled after CM4:* **Propose tempo map** and **Find first downbeat** are in the Tempo map
     tab's **From the audio** ([C4](#c4-tempo-map-and-grid) item 9), with **Detect from audio**,
     not the toolbar.
6. Everything here is a hint. Nothing changes the chart without an explicit action, and every
   action can be undone.

*Settled in CM4:*

- **Onsets** have their own STFT, separate from the spectrogram's: a 1024-sample window every 128
  samples, twelve bands an octave, each compared with the frame two before (spectral flux). The
  whole spectrum is compressed hard, so soft onsets in a loud mix count; the drum bands gently, so
  one band's leakage from a loud hit in another doesn't. A drum band's onset that's weak for its
  band, at the same moment as a strong one in another, is taken as leakage and left out. Each onset
  is timed off the audio itself, at the 32-sample block where the level rises most, which puts a
  sharp hit within a millisecond whatever its level.
- **Sensitivity** is each lane's own, in its audio flyout: a threshold on how far an onset stands
  out, so changing it computes nothing again. Onsets are drawn in a strip of 6 px per band: left of
  a highway's lanes, under a timeline's rows. Drums have three bands, kick nearest the lanes, in the
  colours of the pads they likely belong to.
- **Snap to onset** (`O`, and the toolbar) applies to placing a note with the mouse and to moving
  notes: the note grabbed goes to the onset, the others keep their places around it. Keys, sustains,
  phrases and tempo markers keep to the grid. The reach is half a step of the snap in the toolbar,
  even with the grid off. The onsets are those of the lane showing the part, at its sensitivity.
- **Nudge to onsets** (`Shift`+`O`) moves chord by chord, each by its own amount, keeping each
  chord's forcing with it. Two chords after one onset: the nearer has it. A chord can't land on a
  note that isn't moving. The report gives every chord's move in milliseconds, and opens the
  Messages dialog when any chord stayed.
- **The proposal** is estimated from the mix by default, or the lane in focus, over the selection or
  the whole song. Its downbeat is Find first downbeat's (item 5). It's reviewed in a bar over the
  lanes (Double, Halve, the downbeat a beat either way, Reset, Accept, Discard) and stays through
  edits, a delay change included, until it's accepted or discarded. Dragging a beat's handle pins
  it, and the beats either side are spaced evenly again up to the nearest pinned beat or two
  measures away.
- **Accepting.** For the whole song, the downbeat goes on measure 2's line and measure 1 leads in to
  it; every tempo marker and anchor is replaced, and the time signatures are kept. Beats share a
  marker while each is within the **merge** tolerance (5 ms by default) of its segment's tempo.
  Anchors go on measures 1 + n, 1 + 2n, …, and on both ends of each unsure stretch, which is one
  segment. For a selection, its first beat goes on the beat line nearest it, the segment before it
  takes up the change, the tempo in force after its last beat carries on, and the next anchor stays
  where it is.
- **Find first downbeat** chooses the beat of each measure with the strongest kick, and more weakly
  the strongest onsets, at the first such beat from where the music starts. It's shown as a line
  labelled **Downbeat?**, with a bar to step it a beat either way. **Lead-in** sets the first tempo
  so measure 2 starts on it; measure 1's other unanchored markers go. It's refused when the
  downbeat isn't after the chart's start, when measure 1 would be faster than 1000 BPM or slower
  than 10, or when an anchor inside measure 1 would move. **Delay** sets `delay` in place of the
  song's (`delay_seconds` goes too when it's 0), as a pending change Revert takes back.

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
   - click, Shift+click, and box select; holding Shift gives any tool the select tool's press on
     a highway, the first press while it's held selecting afresh;
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
   - Press times come from the input events' timestamps where they can be had, and otherwise from
     when the event is handled.
   - Each press is converted to song time as **`SmoothPosition` − latency × speed** at the moment
     of the press ([§4.4.1](#441-playback-changes)). It is then quantised to the grid, or to
     onsets if snap to onset is on.
   - Each pass is one undoable command. Recording can overwrite the selected range or merge with
     what's there.
   - This works at any speed, so a fast passage can be recorded at 50%.
   - Game controllers and MIDI devices record too, through their bindings, each press timed by its
     own event ([C19](#c19-playtest-and-instrument-input) item 7).
6. **Autosave and recovery.**
   - Every 2 minutes (configurable, 1 to 60), and when playback stops, the model is written to a recovery
     file in the per-user config folder (`Recovery/`), never into the song. Nothing is written
     when nothing has changed since the last autosave.
   - **What it holds.** The model, as a `notes.mid` from `MidiChartWriter` (so anchors and
     `RawEvent`s come with it), plus the pending `song.ini` changes and a manifest. The manifest
     records the song's full path (folder or `.sng`), the size and modified time of its chart file
     when it was opened, and the autosave time.
   - **Which song it belongs to.** A recovery file is matched to a song by its full path. If the
     chart on disk has changed since the recovery file's manifest recorded it, recovery still
     offers it, but warns that the song has changed since.
   - Saving deletes it. Opening a song with a recovery file offers to restore it
     ([C2](#c2-open-and-import)). *Settled in CM2:* the recovery file is also deleted once nothing
     is unsaved, and a recovered chart opens unsaved.
   - **How it is written** (`RecoveryStore`). The chart, then a JSON manifest, each to a temp file
     renamed into place. The manifest holds the chart's length and SHA-256, so a chart without a
     manifest that matches it, as a crash between the two would leave, is ignored. A song's files are
     named by a hash of its full path (`SongLock.KeyOf`). The manifest also holds the song's `song.ini`
     as the next save would leave it, which the chart is read back with, and the keys of the pending
     changes the user reverted, which stay reverted.
   - **A save** writes the recovery file from the bytes it's about to save, before it touches the
     song, and deletes it once the save has gone through. A crash mid-save always leaves one.
   - **A new chart never saved** is autosaved too, under an ID of its own, with its name, artist and
     the paths of its audio. The home screen lists these under **Unsaved new charts**, with
     **Recover** and **Discard**. Once it's first saved, its autosave moves to its song's.
   - **Closing** with Discard deletes it. When the OS shuts down, which won't wait for the close
     prompt, the chart is autosaved there and then.
7. **Closing** Chart Builder, or the app, with unsaved changes asks: Save, Discard or Cancel.
8. **Keyboard shortcuts** are listed in [§9](#9-keyboard-shortcuts).

*Settled in CM5:*

- **Selection.** The notes selected stay one track's, so Properties and the keys that change notes
  one at a time keep to it. A **range across lanes** (Alt+drag, or Ctrl+Shift+A for the section
  the cursor is in, from its start to the tick before the next) covers the lanes shown; delete,
  cut and copy, Alt+arrow moves in time, H, G and T, and the transforms act on every track it
  covers. **Select lane** is in the lane's Edit menu.
- **The clipboard** is the app's own, not the OS's, so a passage pastes into the next chart too. It
  holds notes in ticks from the passage's start, scaled when the resolutions differ, so a passage
  lands on the grid at any tempo. It keeps the lanes as shown, so a passage from inside a disco
  flip keeps the pads the player sees, and Pro drums keep which pads were toms. Phrases and disco
  flip sections aren't copied.
- **Paste.** Ctrl+V clears the span the passage covers, then puts it in; Ctrl+Shift+V merges: on
  frets each chord pasted takes the place of the chord at its tick, and on drums each note takes
  the place only of the note at its place. A passage of one track pastes into the focused lane; one
  of several, into the same parts and difficulties. 5-fret and GHL paste into each other through a
  lane mapping the user confirms, starting from the one last confirmed that way.
- **Transforms** are in the lane's Edit menu: lefty flip, shift a lane (also Alt+Shift+arrows
  across), and sustains cleared, one grid step, or until the next chord. Lefty flip mirrors GHL's
  columns, each note keeping its colour; drums aren't flipped, as that would change their cymbals,
  toms and disco flip. Shifting stops each note at the edge, joining notes that meet; opens and
  kicks stay.
- **Stretch timing** (×2, ×1.5, ×⅔, ×½, or to end at the cursor) works on the selection's span. It's
  a tempo-map edit: the tempos inside the span are scaled, everything keeps its time, and what's
  after the span moves by the change in its length. The passthrough tracks are re-ticked to match
  (§4.2), and anchors keep their times.
- **Record mode.** R starts a pass into the focused lane, playing from the cursor if the song isn't
  playing; R, Space or the song stopping ends it, as one step, and Esc cancels it. The transport
  bar has the Record button and Merge or Overwrite. (*Revised in CM6:* R and the Record button turn
  record mode on and off, and playing the song with it on starts a pass from where the song is; the
  song stopping ends it, as one step, and the next play starts another. Turned on while the song
  plays, a pass starts there; turned off, the pass ends. Esc cancels the pass and turns record mode
  off. Record mode and playtest ([C19](#c19-playtest-and-instrument-input) item 4) are never on
  together, and with both off, playing just plays.) Overwrite clears the range selected, or the
  stretch played, and leaves out presses outside a range selected. Presses on one tick make a
  chord, and a fret takes an open note's place. A key let go less than a grid step after it was
  pressed leaves a plain note; drums ignore holds. The pass is drawn as a ghost until it ends.
- **Press times** are read from the playback clock when the key event is handled, on every OS, as
  key events carry no timestamp. The wait before an event is handled isn't corrected; at 50% speed
  it's halved in song time. Keys held when the window loses focus are let go then. (*Revised in
  CM6:* controllers and MIDI devices carry timestamps, and are corrected
  ([C19](#c19-playtest-and-instrument-input) item 3).)

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

   *Settled in CM2:* lint runs a moment (150 ms) after the edits stop, one run at a time, and never
   during a drag. Edits made while it runs are linted, against its result, by the next run.
2. **How findings are shown.** Findings use the existing `Finding` type, with rule IDs `CHRnnn`
   and messages in `FindingMessages.resx`. They appear in the findings panel and as marks on the
   overview strip, and each one links to its place in the chart. `Finding` gains an optional chart
   location (part, difficulty and tick range) for that, beside its existing byte offset and
   metadata key. A finding about the song as a whole rather than a part, such as a tempo marker
   (CHR001, CHR002, CHR005, CHR082, CHR085), has a tick instead (`Finding.Tick`).
3. **Quick fixes.** The existing `QuickFixRegistry` and `IQuickFixStrategy` only edit metadata
   (`QuickFix.ApplyTo(MetadataItem)`). Chart Builder adds a chart kind of fix, which proposes an
   `IChartCommand` for a finding, and registers chart fixes with the same registry. Each fix is
   one undoable command (see the table for which rules have one). It's given the read options lint
   used, for fixes that depend on them. A fix that changes
   `song.ini` instead (CHR084's) restores the pending change the user reverted; it isn't an undo
   step, as reverting isn't.
   - **More than one right answer.** Where a finding can be fixed more than one right way, such as
     which of two notes too close together to delete (CHR026), each way is offered, labelled, for the
     author to pick. A way that would do something wrong, such as snapping a note onto another, isn't
     offered. Each way has an ID that's the same for every finding of its rule, so Fix All applies
     the same way to each.
   - **The finding's menu.** A finding's fixes are on its right-click menu, worked out as it opens
     from the chart as it is then. For a rule with one way to fix it: **Fix**, **Fix All For
     <part>** (every finding of its rule in its part, every difficulty) and **Fix All** (every
     finding of its rule in the chart). For one with more: a list of the ways, each with those three
     under it. Each Fix All is one undo step, its fixes worked out against the chart the fixes before
     them leave, so one an earlier fix has already seen to is passed over. Fix All is only offered
     for chart fixes, not CHR083's or CHR084's, and Fix All For only for a finding in a part. A
     finding without a fix shows a greyed-out **No Fixes Available**. The library's list, which
     fixes nothing, has no menu.
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

*Settled in CM5:*

- **What's read.** The library lints the chart as stored, with `song.ini` as drafted, and reads it
  with the options as loaded, as the games read it today, not as a Chart Builder save would write
  them. The audio is measured for CHR003 and CHR084; a `.sng`'s from inside it.
- **The cache** is in memory, for the session (`SongChartLintCache`): a song whose chart file,
  drafted `song.ini`, audio files and language are as they were is answered from it, and the audio
  isn't measured again when only `song.ini` changed. Nothing is written to disk.
- **In the Validator,** a song's chart findings follow its metadata issues, one line per rule and
  key with how many and where the first is, coloured by severity, and filtered by six chart types,
  one per group of the catalogue. CHR083's and CHR066's have fixes there (a suggested rating, and
  star power generated into the draft); the rest have none. A song's missing difficulties
  (CHR087) are a note there, filled by Generate Lower Difficulties instead. A
  song is listed for a chart Warning or Error, not for Info alone, and counts as failing until both
  its metadata and its chart pass. A song whose only chart finding to fix is CHR066 is listed too,
  so Fix selected can generate its star power, but doesn't count as failing. Ticking a song, or
  every song, ticks its CHR066 fix only when it's the song's only fix; otherwise it's ticked on its
  own, as it rewrites the chart though nothing fails. Fix selected generates a song's star power
  with its defaults, with no dialog.
  Save all asks only about metadata that still fails. A drafted song is linted again a moment after
  its draft changes.
- **In the detail panel,** the chart findings of the one song shown follow its own, with where
  each is.

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

   A new chart's first save asks where its song folder goes instead, and writes it with
   `NewChartCreator` ([C1](#c1-new-chart-and-chart-settings), The first save).

   *Settled in CM2:* the Editor isn't reopened after a save. It moves on to the saved song, so the
   undo stack, the cursor, the lanes and playback carry on. After a new chart's first save, its
   audio keeps playing from the files it was given until the song is next opened. A save marks the
   undo stack where its snapshot was taken, so edits made while it runs stay unsaved.
2. **`song.ini` changes.** Every change Chart Builder makes to `song.ini` is shown as a pending
   change in Chart Builder's **Pending changes** list (beside the findings panel) as soon as it is
   made, not only at save. Each change can be reverted there. Chart Packer's metadata editor
   doesn't see these changes until they are saved ([§4.6](#46-saving)). The full list:
   - `song_length`, when it is unset or no longer matches the audio's length within 1 s, worked out
     once the audio has been measured. Chart Builder lints the `song.ini` its save would write, so
     CHR084 appears there only when this change has been reverted;
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
   - the `.chart`'s `[Song]` values, on import ([C2](#c2-open-and-import));
   - a new chart's name, artist, album, genre and year, from its first stem's tags
     ([C1](#c1-new-chart-and-chart-settings));
   - Chart settings' edits ([C1](#c1-new-chart-and-chart-settings)): any key, a part's rating and
     `delay`. The Art tab's and Tracks' file changes are listed with them when Save asks first;
   - `sustain_cutoff_threshold`, derived from the shortest sustain the save writes
     ([C5](#c5-5-fret-notes)).

   Changes the chart is read by can't be reverted, as leaving one out would save a chart that plays
   differently from what the editor shows: the drum layout keys, `sustain_cutoff_threshold` while the
   chart has a sustain, and `multiplier_note`. The rest can, and a reverted change stays
   listed, struck through, until it's restored. Reverting isn't an undo step, and stays across saves.

   *Settled in CM2:* the list isn't kept as edits are made. It's worked out from the chart as it
   stands against `song.ini` as it was opened, so undoing an edit takes its change away. A part's
   rating is listed only for a part added since the song was opened. Only the reverts are kept.

   Otherwise, the HOPO threshold is never changed by Chart Builder, and its display follows it.
   `sustain_cutoff_threshold` is written by the save from the chart's own sustains.

   **Save asks first** only when it changes `song.ini` or removes a file, listing what it changes.
   An import's first save always asks, with **Remove notes.chart** and **Keep notes.chart**.
3. **Save and pack** saves, then packs the song to a `.sng`
   ([project-spec F1](project-spec-v1.md#f1-pack-song-folder--sng)) with Core's packing
   (`PackPlanner`, `SongJob`). It uses Chart Packer's output folder and overwrite settings, which
   Chart Builder reads from Chart Packer's settings file and never writes.
   - The `.sng` leaves out what only Chart Builder reads: the tempo anchors ([§7.3](#73-text-events))
     and the `song.ini` keys of parts the chart has no notes in (SNG062). A song opened from a
     `.sng` is copied the same way.
4. **Export copy** writes the song with its draft applied to another folder, using the existing
   `SongExport`.

Acceptance criteria:

- **AC C17.1** A save that is interrupted (killed mid-write) leaves the previous `notes.mid` intact.
- **AC C17.2** For every fixture chart, reading the saved `notes.mid` with `ChartReader` gives the
  notes, flags and star power the editor shows.
- **AC C17.3** Saving never changes a `song.ini` key the user hasn't seen as a pending change.
- **AC C17.4** For every fixture chart, reading the saved `notes.mid` with `MidiChartLoader` gives
  back the model, including accents, ghosts, phrases, events, anchors and `RawEvent`s, with
  `[music_start]`, `[music_end]` and `[end]` placed as [C8](#c8-phrases-and-events) item 5
  says.

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
   - For each part and difficulty, a suggested `diff_*` tier: 0 to 6 for charts written to be
     played, and 7 and up for overcharts.
   - Every part is rated by the difficulty model ([difficulty-model.md](difficulty-model.md)), with
     its instrument's costs. It measures strum (for drums, kick), note (hands), rhythm, pattern and
     endurance difficulty moment by moment, and scores the chart by its hardest stretches.
   - Pro drums are rated for each of their keys: `diff_drums` as 4-lane drums play them, and
     `diff_drums_real` with the cymbals.
   - It is shown with what it was based on, and is only a suggestion.
   - The parameters are fixed in v1, fitted to the ratings of official charts (difficulty-model.md
     §13 and §14.4).
4. **Difficulty analysis.** For any part and difficulty, the model's rank, a spider graph of its
   five attributes, and its difficulty over the song with the sections and the rank lines.

*Settled in CM5:*

- **Where the preview is.** A **Preview** tab in the Editor's right panel, and a window of its own
  (**Window**), which F11 makes full screen. Both show the lane in focus, or a part and difficulty
  picked, with practice, the speed and how far ahead it looks. A scrub on the highway moves
  playback there. The control is Chart Packer's inspector's, moved to `ChartShared.UI`.
- **Rebuilt by track.** After an edit that touches the track shown, the whole track's scene is
  built again from its game view, at most once a frame and only while it's shown, not only the
  range the edit touched, as the game view is worked out a track at a time.
- **Practice** turns the loop on, from the selection when there's none yet, and plays it from its
  start; turning the loop off ends practice.
- **The suggested rating** is the part's highest charted difficulty's, as one `diff_*` key rates the
  whole part. It's shown in the preview's header, with what it was worked out from and each
  difficulty's own tier in its tooltip. **Use as diff_…** sets the part's keys that `song.ini`
  rates otherwise, as pending changes Revert takes back; it's never written otherwise. It's also
  CHR083's fix. Every part is rated by `DifficultyAnalyzer`, with `DifficultyParameters.For` its
  instrument, and an overchart is suggested as 7 or more. Each of Pro drums' keys is set to its own
  tier. The suggestion is worked out off the UI thread.
- **The difficulty analysis** is a **Difficulty** tab in the Editor's right panel, and a window of
  its own. It follows the lane in focus, or a part and difficulty picked. It shows:
  - the rank;
  - a spider graph for the whole chart, the selection (in the song, or alone) or the playhead,
    with the whole chart's shape behind;
  - the peak envelope over the song, with lines for each attribute that can be shown;
  - the rank lines, the sections and solos, and the windows that count most toward the score.

  A click on the graph moves playback there. After an edit that touches the track, the whole track
  is read again a moment later, off the UI thread, and only while the analysis is shown.

Acceptance criteria:

- **AC C18.1** Preview follows the latency calibration and stays within one frame of the editor
  view while both are shown.

### C19. Playtest and instrument input

The keyboard, game controllers and MIDI devices can be played as instruments: to playtest a chart in
the editor, judged as the games judge it, and to record into it ([C15](#c15-editing-ergonomics)
item 5).

**Behaviour:**

1. **Devices.**
   - **Game controllers** (gamepads, guitar and drum controllers) are read through SDL3's joystick
     subsystem as raw buttons, axes and hats, so a guitar SDL doesn't know as a gamepad still works.
     They're read while the window is in the background too.
   - **MIDI devices** (drum kits, pad controllers, keyboards) are read through winmm, on Windows
     only for now. Other OSes list none. Each backend is an `IInputSource`, so another can be added
     without changing anything else.
   - Devices are opened the first time the Editor's song plays, or the Input devices window opens,
     and kept open until the Editor and the window close, so a MIDI port is free for other apps
     until then. A port another app holds is shown as in use.
2. **Bindings.** Each instrument family has a profile that binds controls to its actions.
   - 5-fret: Green, Red, Yellow, Blue, Orange, Strum up, Strum down, Whammy and Star power.
   - Drums: Kick, Red, Yellow tom, Yellow cymbal, Blue tom, Blue cymbal, Green tom, Green cymbal and
     Orange (a 5-lane kit's fifth pad).
   - An action can have several controls (two kick pedals, or a keyboard and a guitar). A control
     has one action in each profile.
   - The **Input devices** window lists the devices, each with a meter showing its activity, and the
     profiles. **Bind** takes the next control pressed: a key, a button, a hat direction, a MIDI note,
     or an axis moved past half way from where it rested. Controls already held are ignored, so a
     resting pedal or a stuck axis isn't taken. Binding a control another action has asks to move it.
   - Presets: the keyboard defaults (the number keys, as record mode's, with Enter and the arrow keys
     to strum), a General MIDI drum map, and the usual layout of Xbox 360 guitars.
   - Each device has an input offset in milliseconds, for wireless controllers and drum modules that
     add their own delay, typed in or measured with **Calibrate…** beside it
     ([C11](#c11-calibration-tool) item 2). The keyboard has one too. A MIDI device also has a velocity floor (against crosstalk), a retrigger
     time (against double triggers), and the velocities taken as an accent and as a ghost.
   - Space, Esc, P and N are the editor's while playtesting, and can't be bound.
3. **Timing.** Every input is timed by its own event's timestamp, on one monotonic clock, and turned
   into song time through the playback clock as it was at that moment:
   **`SmoothPosition` − latency × speed**, as everything drawn is ([C10](#c10-playback) item 5), less
   the device's offset. When the event is handled doesn't change it, so controllers and MIDI devices,
   read on threads of their own, are judged as exactly as they're timed. Keys carry no timestamp
   ([C15](#c15-editing-ergonomics)), and are timed when they're handled.
4. **Playtest.** P and the Playtest button turn playtest on and off. With it on, playing the song
   playtests the focused lane's part and difficulty: inputs are judged against its game view
   ([§4.3](#43-writing-midichartwriter)) by the rules in items 5 and 6. While it's paused, the editor
   works as it always does, so a missed note can be fixed straight away and the passage played
   again. Playtest and record mode ([C15](#c15-editing-ergonomics) item 5) are never on together:
   turning one on turns the other off. Both can be off, and then playing just plays.
   - While playing, a key bound to the lane's instrument plays, and doesn't edit, wherever focus is
     (but a text box). Other keys work as they always do; Space, Esc, P and N are never bound.
   - The strike line shows what's pressed, as the games show their frets and pads: a fret's target
     while it's held, a pad's or the kick's as it's hit, and a strum across the lanes, each in its
     colour. Only notes hit light their columns, as autoplay's do in the preview, with a burst out of
     the target. A missed note is marked where it is, and stays marked until it's played again. A
     meter at the strike line shows how early or late the last hits were.
   - The transport bar shows the accuracy, the combo and the mean offset. N and Shift+N move the
     cursor to the next and previous problem: a miss, an overstrum, an overhit or a dropped sustain.
     The Problems list shows every one, with how many passes it happened on.
   - Each lane played keeps what was found in it, marked over its notes, until **Clear** forgets
     every lane's marks, problems and stats. The stats and problems shown are the lane played last.
   - Each pass over the loop region, and each play, starts its stats afresh; problems add up over
     the passes.
   - Seeking, and the loop's jump back, judge nothing they skip.
   - The hit window is in real time, so at 50% speed it's half as long in song time, as the notes
     come half as fast.
   - Vocals and 6-fret aren't playtested in v1.
5. **5-fret rules.** As the games play, close enough to check a chart; each number is a setting.
   - The hit window is 70 ms either side of the note.
   - A single note is played with its fret held and none above it; frets below may be held
     (anchoring). A chord needs exactly its frets. An open note needs none.
   - A strum note is hit by a strum in the window, with its frets held. A strum up to 50 ms before
     the frets are right still counts once they are.
   - A HOPO is hit by pressing or lifting to its frets, while the combo is going; a tap, at any time.
     A strum up to 80 ms after such a hit is part of it. Pressing a fret that's wrong for the next
     HOPO or tap in the window (ghosting) means that one must be strummed.
   - A strum with nothing to hit, or the wrong frets, is an **overstrum**, and breaks the combo.
     Hitting a later note in the window misses the ones before it.
   - A sustain is held to its end with its frets held. Letting go more than 50 ms early is a
     dropped sustain, which counts as a problem but doesn't break the combo.
   - Whammy and star power are read but not judged.
6. **Drums rules.**
   - The hit window is 70 ms either side. Each note is hit by its own pad; a pad hit with no note to
     hit is an **overhit**, which breaks the combo (a setting).
   - Pro drums: a cymbal note needs a cymbal and a tom note a tom, for each colour whose cymbal and
     tom are bound to different controls; with one control for a colour, either counts. Notes in a
     disco flip are played as the game view has them.
   - 5-lane drums: a 5-lane kit's pads play their own lanes. A 4-lane kit's play them as
     [§7.6](#76-drum-layout-conversion) turns Pro into 5-lane.
   - The kick, and on Expert the 2× kick, which can be left out of the judging (a setting).
   - Accents and ghosts can be judged by velocity (a setting, off by default).
7. **Recording from devices** ([C15](#c15-editing-ergonomics) item 5).
   - Guitar: a strum places the frets held, or an open note, and letting go of a fret ends its
     sustain. As a setting, each fret can place a note as it's pressed instead, as the number keys
     do.
   - Drums: each hit places a note on its pad's lane. With **Dynamics from velocity** on, a hard hit
     is an accent and a soft one a ghost. A cymbal or a tom isn't recorded in v1, only the pad's
     colour.
8. **Recording a performance (later).** Every input becomes one stream of timed actions, which the
   judge and record mode each take. The judge gives the same result from the same actions, so a
   performance kept as its actions could be judged again after the chart changes.

*Settings* ([§10](#10-settings)): the early and late hit windows, cymbals must match, judge the 2×
kick, overhits break the combo, judge accents and ghosts, the guitar's record style and dynamics
from velocity. The bindings and each device's offset and MIDI settings are in the Input devices
window, kept in `input-bindings.json`.

Acceptance criteria:

- **AC C19.1** An input whose timestamp is exactly on a note is judged 0 ms off however long after
  it is handled, up to 100 ms, at 100% and at 50% speed, with a latency of 40 ms.
- **AC C19.2** Every rule in items 5 and 6 has a test of synthetic inputs that hits, misses,
  overstrums or overhits as described.
- **AC C19.3** Bindings are kept in `input-bindings.json` and found again for a controller
  reconnected or plugged into another port.
- **AC C19.4** Seeking while playtesting, or the loop jumping back, marks no note it skipped as
  missed.

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
| CHR003 | Warning | A note, phrase or event other than `[music_end]` or `[end]` after the end of the audio (in audio time, [§4.4.1](#441-playback-changes)) | — |
| CHR004 | Info | A note isn't on any common grid: a whole-note division from 1/1 to 1/192, or a triplet one | Snap to the nearest grid line, or the one on its other side, where no note is already; or delete |
| CHR005 | Warning | Two tempo markers or two time signatures on the same tick | Keep the later one |

### 5-fret and GHL

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR020 | Warning | A sustain runs into the next note on its lane, so the games will cut it short | Trim sustain |
| CHR021 | Info | Retired. A sustain is shorter than the song's cutoff, so the games would read it as a plain note. Chart Builder now derives the cutoff from the chart, so the rule is kept for its ID but no longer runs. | — |
| CHR022 | Error | An open note in a chord with frets | Remove the open note, or keep only it |
| CHR023 | Info | A force HOPO or force strum that doesn't change the chord at any HOPO threshold a MIDI chart can be read with | Remove force |
| CHR024 | Warning | A forced HOPO on a chord that repeats the previous note or chord exactly. The games play it as a HOPO, but the same frets can't be hammered on again. | Remove force |
| CHR025 | Warning | The first note of a part is a HOPO (forced or tap) | Remove force |
| CHR026 | Warning | Two notes on the same lane closer than 1/64 of a whole note in ticks (likely a stray duplicate) | Delete the later one, or the earlier |

### Drums

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR040 | Info | A tom marker that covers no note | Remove marker |
| CHR041 | Warning | More than two pads at once (not counting the kick): more hands than a drummer has | — |
| CHR042 | Warning | A drum fill overlaps a star power phrase (not counting a big rock ending) | — |
| CHR043 | Warning | A drum fill whose last tick has no note on some difficulty, so there is nothing to activate on there (not counting a big rock ending) | Move end to last note |
| CHR044 | Warning | A roll or trill phrase with fewer than two notes, or a trill covering only one pad | — |
| CHR045 | Warning | Tom markers on 4-lane drums. The games would read them as Pro, so saving leaves them out ([C6](#c6-drums-4-lane-pro-and-5-lane) item 9). | Switch to Pro, or remove the markers |
| CHR046 | Warning | A fifth-pad note (5-lane green) on a 4-lane or Pro song | — |
| CHR047 | — | *Retired in CM3.* An accent or ghost on a kick. The games never read one, so the loader doesn't keep one, and the editor can't place one. | — |
| CHR048 | Warning | The chart has a `PART DRUMS_2X` track. It is kept as it is and doesn't follow edits to `PART DRUMS`. | — |

*Settled in CM3:* a phrase covers the notes from its start up to, but not including, its end, as the games read
markers. CHR043's fill ends on the last note it covers on any difficulty, and its fix ends the fill on the last such
note every difficulty with notes has. CHR044 checks Expert, and each lower difficulty a roll or trill reaches where it
covers a note.

### Phrases and events

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR060 | Warning | A star power phrase with no notes in it | Delete phrase |
| CHR061 | Warning | A solo with no notes in it | Delete phrase |
| CHR062 | Warning | Two star power phrases, or two solos, overlap | Merge |
| CHR063 | Warning | Notes inside a big rock ending, which the games skip | — |
| CHR064 | — | *Retired in CM3.* A disco flip section that starts or ends between notes of a chord. A mix event applies to every note on its tick, so it can't. | — |
| CHR065 | Warning | A star power phrase or solo with no notes in it on a charted difficulty, though another difficulty has some. A phrase empty on every difficulty is CHR060's or CHR061's. | — |
| CHR066 | Info | A 5-fret (keys included), GHL or drums part has no star power phrases, so a player never earns star power on it. Only where a phrase fits, so generating it always proposes some. One finding per part. | Generate star power ([C8](#c8-phrases-and-events) item 1): a window to keep or drop in Chart Builder, added at once in the library |

*Settled in CM3:* CHR062 gives one finding for each group of phrases that saving would merge into one, and its fix
merges the group into its first phrase.

### Song

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR080 | Warning | A part is in the chart but has no notes on any difficulty. A part charted on only some difficulties is CHR087's. | — |
| CHR081 | Info | A lower difficulty has more chords per second than the one above it | — |
| CHR082 | Info | The chart has no sections | — |
| CHR083 | Warning | A part with notes has no `diff_*` value, or `-1` | Use suggested rating (Chart Builder and the library) |
| CHR084 | Warning | `song_length` doesn't match the audio's length within 1 s. A charter save corrects it, so this mostly appears in the library. | Update `song_length` |
| CHR085 | Info | `[end]` is missing or before the last note. Chart Builder places it on save, so it runs this rule only in the library, not in the editor | — |
| CHR086 | Info | Something in the chart file couldn't be loaded as written, and was left out, as the games leave it out ([§4.1](#editable-model)). Saving writes the file without it. | — |
| CHR087 | Info | A 5-fret, GHL or drum part has no notes on a difficulty below its highest charted one, so the games don't offer it. One finding per difficulty. | Generate with Reduce (Chart Builder); Generate Lower Difficulties (library) |

### Vocals

| ID | Sev. | Rule | Fix |
| --- | --- | --- | --- |
| CHR090 | Warning | A vocal note runs into the next one; a part sings one note at a time | — |
| CHR091 | Warning | A vocal note has no lyric (a slide's is `+`) | — |
| CHR092 | Warning | A vocal note is outside every phrase, so the games don't show it | — |
| CHR093 | Info | A lyric isn't on a vocal note's start | — |
| CHR094 | Warning | A slide has no sung note before it in its phrase to carry on from | — |
| CHR095 | Warning | `HARM2` or `HARM3` without `HARM1`, which the games need to show harmonies | — |

*Settled with vocal editing:* the vocal rules check the whole song after any edit, as a vocal part isn't one of the
model's parts. Each finding names its part's track and the tick at fault.

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
| `[chartpacker_anchor <µs>]` | tempo track, at the anchored marker's tick | A tempo anchor ([C4](#c4-tempo-map-and-grid)). The number is the anchor's audio time in whole microseconds. The games ignore it, so a packed or exported `.sng` leaves it out (`ExportCleanup`). A save keeps it, including a save into a `.sng` the song was opened from. |

### 7.4 Tracks the writer creates

The tempo track, each part (`PART GUITAR`, `PART BASS`, `PART RHYTHM`, `PART GUITAR COOP`,
`PART KEYS`, `PART DRUMS`, `PART GUITAR GHL`, `PART BASS GHL`, `PART RHYTHM GHL`,
`PART GUITAR COOP GHL`), `EVENTS` and `BEAT` (pitch 12 on each measure's downbeat and 13 on other
beats). `PART KEYS` is read and written as a 5-fret part (*revised for #116*; it used to pass
through). Everything else is passed through ([§4.2](#lossless-passthrough)), including `PART DRUMS_2X`,
which the app's reader doesn't recognise, and which doesn't follow edits to `PART DRUMS` (CHR048).

A legacy `T1 GEMS` or `PART REAL_DRUMS_PS` track is read as its part, and saved under the current
name. If a file has two tracks for one part, the one `MidiChartReader` keeps is edited and the
other is passed through.

### 7.5 `song.ini` keys Chart Builder writes

Each part's difficulty rating, from the app's `MidiPartCatalog`. `-1` means "not rated", the same
convention `MetadataDifficulty` already uses.

| Part | Key |
| ---- | --- |
| Lead, Bass, Rhythm, Co-op | `diff_guitar`, `diff_bass`, `diff_rhythm`, `diff_guitar_coop` |
| Keys | `diff_keys` |
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
screen) shows until a chart is started or opened. Then the Editor window takes its place, with the
editor's tools in its toolbar, and closing the Editor brings the launcher back. New chart opens the
Editor straight away, with no dialog before it.

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
│ Inspector│Settings «│ Guitar·X ▾ + ✕┃Bass·X ▾ + ✕┃Drums·X ▾ + ✕  │ Properties       » │
│                     │              ┃             ┃               │                    │
│ Artist – Title      │   highway    ┃   highway   ┃   highway     │ Note  m41 · beat 3 │
│ [album art]         │              ┃             ┃               │ Tick  78,960       │
│ Tracks              │              ┃             ┃               │ …                  │
│ Chart files         ├━━━━━━━━━━━━━━┻━━━━━━━━━━━━━┻━━━━━━━━━━━━━━━┤                    │
│ Other files         │ Vocals ▾                        2.0 s + ✕  │                    │
│ Findings            │   piano roll, lyrics under it              │                    │
│ Metadata            │                                            │                    │
╞═════════════════════╧════════════════════════════════════════════╧════════════════════╡
│ Timeline                                                              1:38.4 / 4:12.0 │
│ 0:00      0:30      1:00      1:30   │  2:00      2:30      3:00      3:30      4:00  │
│ ▁▂▅▇█▇▅▃▂▁▂▃▅▇▅▃▁▂▃▅▇█▇▅▃▂▁▂▃▅▇▅▃▂▁▂│▅▇█▇▅▃▂▁▂▃▅▇▅▃▁▂▃▅▇█▇▅▃▂▁   notes over the mix   │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- ⚙ opens **Program settings** (Ctrl+,). The left panel's tabs are **Chart settings**, **Art** and
  **Sections** ([C8](#c8-phrases-and-events) item 3). The ◉ (View) menu will choose which lanes are shown; it is a placeholder so
  far. The toolbar's groups wrap onto more rows as the
  window narrows.
- The middle shows **lanes**: each a part's highway at one difficulty, or a vocal part, drawn as a
  piano roll ([C3](#c3-editor-layout) item 7). They are
  docked like Visual Studio's tool windows: dragging a lane's header shows guides on the lane under
  the pointer and on the edges of the whole area, and dropping on one docks the lane against that
  edge. A lane's highway is always vertical; only the room it takes is tall or wide. ━ splitters
  resize the lanes. Each lane's ▾ menu switches its part (each part is shown at most once), + adds a
  lane (a highway to the right of the last one, a vocal part the full width along the bottom), and ✕
  closes it. The lanes, where they are and their sizes are saved (`LaneDockLayout`, in
  `ChartShared.Core/Charting/Layout/`).
- « and » collapse a panel to a strip along that edge, with its tab titles turned on their side.
  Clicking a title brings the panel back on that tab. The panel widths, whether each is collapsed, and the
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
│ ▶ 1:38.4 / 4:12.0   Speed [75%] ♪PC   ⟲Loop  ♩Metro  🔊Hits   ⏱ 42 ms                  │
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

**Chart settings** (the left panel's tab, for a new chart or a song opened; each change applies as
it is made):

```txt
┌──────────────────────────────────────┐
│ Chart settings │ Art │ Sections │ … «│
├──────────────────────────────────────┤
│ Parts                                │
│ ┌──────────────────────────────────┐ │
│ │                    Difficulty    │ │
│ │ ☑ Lead              [ 3  ] ✕     │ │
│ │ ☐ Bass              [    ] ✕     │ │
│ │ ☐ Rhythm  …                      │ │
│ │ ☑ Drums             [ 4  ] ✕     │ │
│ │     Pro drums       [ 5  ] ✕     │ │
│ │ Drums                            │ │
│ │ [ Pro ▾ ]                        │ │
│ └──────────────────────────────────┘ │
│ Metadata                             │
│ ┌──────────────────────────────────┐ │
│ │ name      Song                   │ │
│ │ artist    Band                   │ │
│ │ + Add                            │ │
│ └──────────────────────────────────┘ │
│ Tracks                               │
│ ┌──────────────────────────────────┐ │
│ │ M S 3.1 MB song.ogg ▁▃▅▃▁ ━━●━   │ │
│ │ M S 1.2 MB guitar.ogg ▂▅▂ ━━●━   │ │
│ │ + Add track                      │ │
│ └──────────────────────────────────┘ │
└──────────────────────────────────────┘
```

**Calibration tool:**

```txt
┌ ⊞ Calibrate Audio Latency ────────────────────────────────────── ─  □  ✕ ┐
├───────────────────────┬──────────────────────────────────────────────────┤
│ ┌───────────────────┐ │  Output device                                   │
│ │  Calibration off  │ │  ┌────────────────────────────────────────────┐  │
│ │ ═════════════════ │ │  │ ♪ Headphones (USB)                         │  │
│ │ ───────────────── │ │  └────────────────────────────────────────────┘  │
│ │ ═════════════════ │ │  Latency                                         │
│ │ ━━━━━━━━━━━━━━━━━ │ │  ┌────────────────────────────────────────────┐  │
│ │ ═════════════════ │ │  │ ◷ [ +42 ] ms ↶    [−10 −5 −1 +1 +5 +10]    │  │
│ │                   │ │  │ ━━━━━━━━━━━━━━━━━━━●━━━━━━━━━━━━━━━━━━━━━  │  │
│ │ ═════════════════ │ │  │ Plus 23 ms detected from the device: 65 ms…│  │
│ │ [ ][ ][ ][ ][ ]   │ │  │ The arrow keys move it by 1 ms, …          │  │
│ │                   │ │  └────────────────────────────────────────────┘  │
│ │                   │ │  Compare                                         │
│ │                   │ │  ┌────────────────────────────────────────────┐  │
│ │                   │ │  │ [◉ Hold for no calibration]                │  │
│ │                   │ │  │ While the button or Space is held, …       │  │
│ └───────────────────┘ │  └────────────────────────────────────────────┘  │
├───────────────────────┴──────────────────────────────────────────────────┤
│ [Reset to 0]                                        [Cancel]   [Save]    │
└──────────────────────────────────────────────────────────────────────────┘
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
| `1`–`5` (`1`–`6` GHL) | Place or remove a note on that lane at the cursor; in record mode, play a note. While the song plays with record mode off, nothing |
| `0` | Open note, or drum kick (as `1`–`5`, while the song plays) |
| `Shift`+`0` | 2× kick (Expert drums; as `1`–`5`, while the song plays) |
| `Space` | Play or pause from the cursor |
| `↑` / `↓` (Highway), `→` / `←` (Timeline) | Move the cursor one grid step later or earlier |
| `Alt`+arrows | Move the selection one grid step in time, or one lane across |
| `Alt`+`Shift`+arrows across | Shift the selected notes a lane, each stopping at the edge |
| `Alt`+`→` / `←`, `Alt`+`↑` / `↓` (vocal lane) | Move the selected vocal notes one grid step, or one semitone |
| `Alt`+`Shift`+`↑` / `↓` (vocal lane) | Move the selected vocal notes an octave |
| `Alt`+drag (Select tool, not on a selected note) | Select a time range across the lanes shown |
| `PgUp` / `PgDn` | Move the cursor one measure |
| `Home` / `End` | Move the cursor to the chart's start / to where its last note lets go |
| `Ctrl+G` | Go to a measure: asks for a measure number (1 up to the last measure with notes) and moves the cursor to its start |
| `Ctrl+F` | Find the passages of the lane that match the selected notes (matched as the Sync tool matches), and go to the next after the cursor; again goes on to the next |
| `F3` / `Shift+F3` | Go to the next / previous passage found, wrapping round |
| `Ctrl`+`↑` / `Ctrl`+`↓` | Next or previous section |
| `Ctrl`+`Shift`+`↑` / `Ctrl`+`Shift`+`↓` | Next or previous tempo or time signature change |
| `[` / `]` | Coarser or finer snap |
| `O` | Snap to onset on or off |
| `Shift`+`O` | Nudge the selected notes to their onsets |
| `B` | Tap tempo |
| `Ctrl`+wheel, `+` / `−` | Zoom |
| Wheel | Move the cursor one grid step later or earlier, the way the **Wheel scroll direction** setting says, in either view |
| `Shift`+wheel | Move the cursor four grid steps later or earlier |
| `Ctrl`+`1`–`4` | Easy to Expert |
| `V` | Switch between Highway and Timeline |
| `H` / `G` / `T` | Toggle force HOPO, force strum, tap on the selected chords |
| `C` | Toggle cymbal or tom (Pro drums, every difficulty at that tick) |
| `A` / `Shift`+`A` | Toggle accent or ghost (drums) |
| `S` | Star power phrase over the selection |
| `Alt`+`1`–`6` | Pick the Select, Place, Erase, Phrase, Tempo or Sync tool, in the toolbar's order; the number row's digits, not the number pad's; works wherever focus is, except in a text box |
| `L` | Hold for the Loop tool: drag out the loop region, or drag its ends or middle to resize or move it. Tapped briefly, having touched nothing, it turns repeating the region on or off ([C10](#c10-playback)) |
| `R` | Record mode on or off, and playtest off: on, playing the song records a pass. Space ends a pass, and Esc cancels it |
| `P` | Playtest on or off, and record mode off: on, playing the song playtests the lane ([C19](#c19-playtest-and-instrument-input)) |
| `N` / `Shift`+`N` | Move the cursor to the playtest's next or previous problem |
| `W` | Switch the lane between waveform and spectrogram |
| `Ctrl`+`A` | Select all in the current difficulty, or a vocal lane's part |
| `Ctrl`+`Shift`+`A` | Select the section the cursor is in, across the lanes shown |
| `Ctrl`+`Z`; `Ctrl`+`Y` or `Ctrl`+`Shift`+`Z` | Undo; redo |
| `Ctrl`+`X` / `C` / `V` | Cut, copy, paste over the span the passage covers |
| `Ctrl`+`Shift`+`V` | Paste, merged with what's there |
| `Delete` | Delete the selection |
| `Esc` | Cancel a pass of record mode; else take back a drag; else clear the selection |
| `F11` | In the preview's window, full screen on or off; Esc leaves it |
| `Ctrl`+`S` | Save |

Mouse gestures on a highway's lane (a vocal lane keeps its own, [C3](#c3-editor-layout) item 7).
Switching tools keeps the selection; placing notes, by mouse or key, leaves nothing selected.

| Gesture | Action |
| ------- | ------ |
| Place: click | Place a note |
| Place: drag | Place a run of notes, one on every grid step to the pointer, leaving out places taken |
| Place: `Ctrl`+drag | Place a note with a sustain to the pointer (not on drums) |
| Place: `Alt` with any of these | An open note, or the kick, instead |
| `Shift`+click or drag, any tool | Select, as the Select tool does; the first press while Shift is held selects afresh |
| `Ctrl`+drag on a selected note | Lengthen or shorten each selected sustain by the same grid steps |
| `Alt`+drag on a selected note | Lay a run of each selected note, as many grid steps as dragged |
| Right-drag | Lengthen or shorten each selected sustain by the same grid steps |
| `Ctrl`+right-drag | End every selected sustain at the pointer |
| `Alt`+right-drag | Lay a run of each selected note up to the pointer |
| Right-click | The note menu |
| Middle-drag | Erase, whatever the tool |

## 10. Settings

Each app owns its settings file, and every file is in the per-user config folder
(`UserConfigPaths.ConfigFolder`). Both apps read and write theirs through Core's
`JsonSettingsFile<T>`. Several processes can share a file (several Chart Builders, say), so a save
re-reads the file and replaces only the settings this process changed since it last loaded or saved.
Other processes' changes to the other settings, and keys this version doesn't know, are kept. Each
save writes its own temp file and renames it into place. It holds `{file}.lock` from its read to the
rename, so two processes' saves can't interleave and lose one's changes. The lock is the open handle,
which the OS lets go of if a process dies. A save that can't get the lock within about a second
isn't written.

Every settings file records an integer `SchemaVersion`, saying which shape its settings have. A file
without one reads as 0, as every file written before versioning did. A save raises it to the version
the build writes and never lowers it: a build that finds a file from a newer build leaves it as it is,
and its saves are refused (and traced), rather than write settings in the older shape under the newer
number. Nothing is written when the file is read, so an older file is stamped by the next save that
happens anyway — even one that changes no setting, unless the file can't be read then. A build
migrating a drastically changed schema reads the version the file was loaded with before changing its
settings.

| File | Owner | Holds |
| ---- | ----- | ----- |
| `settings.json` | Chart Packer (`AppSettings`) | Chart Packer's settings, as now. Chart Builder reads the output folder and overwrite policy from it for **Save and pack** ([C17](#c17-save-and-export)), and never writes it. |
| `builder-settings.json` | Chart Builder (`BuilderSettings`) | Every setting in the table below except the latency and the reduction rules, plus the Editor window's layout (each side panel's width and whether it's collapsed, and the timeline's height), the recent songs, the last parent folder used for a new chart, and how far ahead the preview looks |
| `audio-latency.json` | The calibration tool, which both apps open | The audio/visual latency. Chart Packer reads it for the inspector ([C11](#c11-calibration-tool)), and re-reads it when the tool closes and when its window is activated ([§4.8](#48-launching-and-coordination)). |
| `input-bindings.json` | Chart Builder's Input devices window | Each instrument profile's bindings, and each device's input offset and MIDI settings ([C19](#c19-playtest-and-instrument-input)). Enums are saved by name. |
| `reduction-rules.json` | Both apps | The default difficulty reduction rules ([C9](#c9-difficulty-authoring)): Chart Builder's Reduce dialog and Settings, and Chart Packer's Validator, which generates a song's missing difficulties by them ([C16](#c16-chart-lint)). Each saves only the sets it changed. Until it's first saved, they're read from `builder-settings.json`'s `ReductionRules`, where an older Chart Builder kept them. |

Settings are edited in Chart Builder's own Settings window, which uses the shared settings window
and the Core `SettingDefinition` catalog that Chart Packer's uses. The catalog can express Toggle,
Integer, Range, Choice, Folder and Text rows. The latency map and the reduction rules aren't rows:
they are edited in the calibration tool and the Reduce dialog, and Settings shows a button that
opens each. Chart Packer's Settings shows a **Calibrate…** button that opens the same calibration
tool.

| Setting | Default |
| ------- | ------- |
| Default resolution for new charts | 480 |
| Default snap | 1/16 |
| Default editor view | Highway |
| Editor highway speed | The inspector's default `HighwayNoteSpeed` |
| Wheel scroll direction | Wheel up moves later, in both lane views and on the timeline along the bottom |
| Notes keep ticks or times when the tempo changes | Keep ticks |
| Waveform or spectrogram behind the lanes (**Lanes show**, under Audio assist) | Waveform |
| Spectrogram memory budget (under Audio assist) | 256 MB (64 to 4096) |
| Metronome volume / hit-sound volume | 50% / 50%. At 50%, each sound plays at the level `Synth` makes it. A change applies from the next time playback starts. |
| Audio/visual latency | Per output device, plus a global fallback of 0 ms ([C11](#c11-calibration-tool)) |
| Detect output latency | On. The latency detected from the device is added to its calibrated latency. Kept in `audio-latency.json`, shared by both apps (issue #140) |
| Prompt for calibration on first editor use | On |
| Autosave interval | 2 minutes (1 to 60) |
| Default charter name | Empty. Prefills `charter` for a new chart, and fills an empty `charter` on save |
| Default difficulty reduction rules | As in [C9](#c9-difficulty-authoring). The Reduce dialog starts from them, and its changes apply to one run only. |
| Playtest hit window, early / late (under Input) | 70 ms / 70 ms (20 to 200) |
| Cymbals must match, judge the 2× kick, overhits break the combo (under Input) | On, on, on |
| Judge accents and ghosts (under Input) | Off |
| Guitar record style (under Input) | Strum: a strum places the frets held |
| Dynamics from velocity, when recording drums (under Input) | Off |

There is no existing playback-offset setting in `AppSettings` to extend. The latency is new. It is
stored in `audio-latency.json` as two maps, from device ID and from device name to whole
milliseconds, plus the fallback (`{ "byId": { … }, "byName": { … }, "fallback": 0 }`). It is
looked up in that order ([C11](#c11-calibration-tool)). Values are kept within ±500 ms, and a save
changes only the device saved, keeping the rest of the file (`AudioLatencyFile`). The file is
written to a temp file and renamed into place, under the same lock as `settings.json`, so a reader
never sees half of it and the two apps' saves don't overwrite each other.

*Changed for issue #140:* the values are what each device's latency is beyond what's detected
from it. The file also holds `"detect"`, whether the detected latency is added to the value found.
It's on unless it's `false`.

The new-chart settings (default charter name and default resolution) moved from `AppSettings` to
`BuilderSettings` when new charts moved to Chart Builder, before any release included them, so
nothing is migrated. The last parent folder used for a new chart, which a new chart's first save starts
from ([C1](#c1-new-chart-and-chart-settings)), is kept in `builder-settings.json` beside the window
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
| Playback | Clicks in the post-stretch mix against `PositionMap` at several speeds (AC C10.1); `SmoothPosition` against a fake output whose queue drains in steps; latency scaled by speed (AC C10.2, C15.3); the drawn position held as playing starts and the audio paused where it's drawn (issue #103) |
| `.chart` import | Every `.chart` fixture in the existing `ChartToMidi` tests imports with the same warnings |
| Commands | Each command reverses exactly; random command sequences, undone, give back the start (AC C15.1) |
| Tempo map | Tick ↔ seconds property tests against `TempoMap`; anchors stay put under edits (AC C4.1) |
| Lint | At least one fixture per CHR rule, and a clean fixture triggers none (AC C16.1) |
| Analysis | Synthetic click tracks at fixed and changing tempos for `TempoEstimator`; synthetic drum hits in separate bands for `OnsetDetector`; spectrogram bins checked against known sine frequencies |
| Calibration | The input-device test's estimator with simulated taps that have a known offset, jitter and outliers (AC C11.2); the drawn position for a given latency (AC C11.1); the tool's chart and its clicks (AC C11.4); what counts as a tap (AC C11.6); saving a device's offset (AC C11.7); the audio tap test's estimator and every device's taps with their offsets (AC C11.8); the detected latency, its lookup and its switch (AC C11.9, C11.10) |
| Reduction | Each rule in [C9](#c9-difficulty-authoring), on fixtures |
| View models | `ChartEditorViewModel`, the Editor window's and calibration view models against fake services. There are no automated UI tests, as in v1. |
| Playtest and input | Each 5-fret and drums rule with synthetic timed inputs (AC C19.2); timestamps through the song clock at several speeds and across seeks (AC C19.1, C19.4); bindings routing keys, buttons, axes, hats and MIDI notes, and their file round trip (AC C19.3); recording from timed actions. Device backends are checked by hand. |
| Two apps | `BuilderArgs` parsing of every form in [§4.8](#48-launching-and-coordination), including bad arguments; `SongLock` held and refused, with two locks in one test process standing in for two processes, released and its file gone when the holder is disposed, and a lock file left behind taken (AC C2.5); `SongLockSet`'s holds counted within a process; `SongFileCommit` waiting for a reader to let go; `JsonSettingsFile` keeping two processes' changes to different settings, and keys it doesn't know |

**Test isolation** is as in [project-spec §10](project-spec-v1.md#10-testing-strategy). Tests use
temp folders only. The recovery folder, the latency file and Chart Builder's settings file take
explicit paths, and song locks sit beside the temp songs, which keep tests away from the real config
folder. `UserConfigGuard`
fails the run if anything there changes anyway.

## 13. Milestones

| Milestone | Contents | Exit criteria |
| --------- | -------- | ------------- |
| **CM0: Split** | `Directory.Build.props` (the version) and `Directory.Packages.props` (central package management). The `ChartShared.UI` library, with the theme, dialogs, pickers and settings window. The `ChartBuilder` app: home screen, `BuilderArgs`, `BuilderSettings`, and **New chart**, which opens the Editor. `JsonSettingsFile<T>`, shared by both apps' settings. Chart Packer's **New chart** is removed, and its **Edit chart** button (one selected song, no job running) is added, disabled until CM2. `publish.ps1` publishes both apps into one folder, the installer installs both, and the release workflow reads the version from `Directory.Build.props`. | Both apps build and publish with zero AOT warnings, into one folder with one copy of each native library; the installer upgrades an existing install and adds both Start menu entries; New chart opens the Editor from Chart Builder's home screen and from dropped audio files |
| **CM1: Model, writer and lint engine** | `EditableChart` and friends, `MidiChartLoader`, `MidiChartWriter`, passthrough and re-ticking, the game-view projection and extended read-back check, `MidiChartReader` reading dynamics, commands and undo. The lint engine: rule scopes, `Finding`'s chart location, and chart quick fixes. The `SongFolderFiles` order change. And the tests. | Every round-trip and writer-vs-reader test passes, and AC C2.4; zero AOT warnings |
| **CM2: Calibration, new chart, 5-fret editing** | The playback changes ([§4.4.1](#441-playback-changes)) and `Synth`, C11 calibration and the shared latency file, C1 new chart and Chart settings (without **Detect from audio**), C2 open and import, with **Edit chart** turned on, `SongLock` and Chart Packer's refresh ([§4.8](#48-launching-and-coordination)), the highway's drawing moved to `ChartShared.UI`, C3 both views, C4 tempo map and grid, C5 5-fret, C10 playback, C15 autosave and recovery, C17 save, and the timing, 5-fret and song lint rules. Calibration comes first because every timing judgement after it depends on it. | AC C1, C2, C3, C4, C5, C10, C11, C15.2 and C17 pass |
| **CM3: Drums and GHL** | C6 all three drum variants and conversion, C7 GHL, C8 phrases and events, and the drums and phrase lint rules | AC C6, C7 and C8 pass |
| **CM4: Audio assist** | C12 waveform, C13 spectrogram, C14 onsets and tempo estimation, and new chart's **Detect from audio** | AC C12, C13 and C14 pass |
| **CM5: Authoring tools** | C9 reduction, C15 record mode, clipboard and transforms, C16 lint in the library, C18 preview and suggested difficulty | AC C9, C15, C16 and C18 pass |
| **CM6: Playtest and instrument input** | C19: the input model and judge, keyboard playtest in the editor, game controllers and the Input devices window, recording from devices, then MIDI devices on Windows | AC C19 pass; zero AOT warnings |

## 14. Future (after v1)

- Vocal percussion and overdrive, lyric and range shifts, and pitch detection from the vocals stem,
  building on the vocal lanes' editing ([C3](#c3-editor-layout) item 7).
- Pro keys, pro guitar/bass and Elite drums.
- Venue, lighting and camera events.
- `.chart` export.
- Auto-charting drafts from audio. This would build on C14, and the result would always be
  labelled as a draft.
- Playtesting 6-fret, extended sustains, and recording cymbals and toms from a kit
  ([C19](#c19-playtest-and-instrument-input)).
- MIDI devices on macOS and Linux.
- Keeping and replaying a playtest performance, judged again against the chart as it changes.
- Rebindable editor shortcuts (instrument bindings are in C19).
- The suggested rating shown in the library and inspector as well as Chart Builder.
- The difficulty model's values used by other tools for drums and six-fret, such as finding solos
  or placing star power.

## 15. Open questions

Decisions already made for v1, recorded here so they aren't reopened by accident:

- **Format:** Chart Builder saves `notes.mid` only, and `.chart` is import only.
- **Placement:** a separate app, Chart Builder, installed with Chart Packer by the same installer.
  The two share code (`ChartShared.Core`, `.Audio`, `.Video`, `.UI`) but no process. They
  coordinate through command-line launches, a per-song lock file, and Chart Packer re-reading the
  songs Chart Builder changed. There is no IPC ([§4.8](#48-launching-and-coordination)).
- **Drafts:** each app keeps its own. Chart Packer asks to save or discard a song's drafts before
  opening it in Chart Builder ([§4.6](#46-saving)).
- **Settings:** each app writes only its own file. The latency is a shared file that only Chart
  Builder writes ([§10](#10-settings)).
- **New chart:** part of Chart Builder only, with no wizard. New chart opens a new chart in the
  Editor straight away, and its first save asks where the song folder goes
  ([C1](#c1-new-chart-and-chart-settings)). Chart Packer has no **New chart**. Its only way into
  Chart Builder is **Edit chart**, for exactly one selected song.
- **Chart settings and Art:** Chart settings, the first tab in the Editor's left panel, holds the
  chart's timing and `delay`, its parts and their ratings, the song's metadata and its tracks; the
  Art tab holds the album art and backgrounds. Each change is applied as it is made, as an undo
  step. (*Revised after CM5:* these were in a Chart Inspector tab, as pending changes, not undo
  steps.)
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
- **Reduction rules:** set per run in the Reduce dialog, or Chart Packer's Generate Lower
  Difficulties window, starting from the defaults both apps share. Nothing is stored per song ([C9](#c9-difficulty-authoring)).
- **Calibration device key:** a stable OS device ID where there is one, then the device name, then
  the global fallback, with the latency detected from the device added while detecting is on
  ([C11](#c11-calibration-tool)).
- **`.chart` after import:** removed on the first save, after a confirmation. No backup is kept
  ([C2](#c2-open-and-import)).
- **Suggested difficulty:** shown in Chart Builder only, with fixed parameters in v1. Tuning and
  showing it in the library are future work ([C18](#c18-preview)).
- **Generated drum fills:** the author picks the spacing, length and ending note on each run
  ([C6](#c6-drums-4-lane-pro-and-5-lane)).

There are no open questions at present.
