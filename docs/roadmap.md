# Chart Builder — Roadmap

| | |
| --- | --- |
| **Status** | Current as of 2026-10-07 |
| **Companion docs** | [charter-spec-v1.md](charter-spec-v1.md) (what each milestone contains, and its exit criteria) |
| **Next up** | [CM2](#cm2-calibration-chart-settings-and-5-fret-editing)'s hand checks, then [CM3: Drums and GHL](#cm3-drums-and-ghl). See [Where CM2 stands](#where-cm2-stands). |

---

## Purpose

This file records the order in which Chart Builder's milestones are built, and where each one
stands. The spec's [§13](charter-spec-v1.md#13-milestones) owns what each milestone contains and
its exit criteria. This file owns only the order and the status.

## At a glance

| Order | Milestone | Status | Progress | Depends on |
| ----: | --------- | ------ | -------- | ---------- |
| 1 | [CM0: Split](#cm0-split) | Done (cleanup left) | Exit criteria met; 4 cleanup items left | — |
| 2 | [CM1: Model, writer and lint engine](#cm1-model-writer-and-lint-engine) | Done | Exit criteria met | CM0 |
| 3 | [CM2: Calibration, chart settings and 5-fret editing](#cm2-calibration-chart-settings-and-5-fret-editing) | **In progress** | Steps: 6 of 6 built. Exit criteria: 31 of 33 pass, 2 in part, waiting on hand checks ([tracker](#cm2-progress)) | CM1 |
| 4 | [CM3: Drums and GHL](#cm3-drums-and-ghl) | Not started | — | CM2 |
| 5 | [CM4: Audio assist](#cm4-audio-assist) | Not started | — | CM2 |
| 6 | [CM5: Authoring tools](#cm5-authoring-tools) | Not started | — | CM3, CM4 |

---

## CM0: Split

**Status: done, with cleanup left.**

Both apps build from one solution, share `ChartPacker.UI`, and publish and install together.
Chart Builder has its launcher, `BuilderArgs`, `BuilderSettings` and `JsonSettingsFile<T>`. Chart
Packer's **Edit chart** is in place, and was turned on in CM2 step 3.

**Built ahead of plan.** The wizard was replaced by the Editor window
([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)), and the Editor's shell already exists:

- the toolbar;
- the collapsible side panels;
- lane docking (`ChartPacker.Core/Charting/Layout/LaneDockLayout`);
- a placeholder timeline.

This counts towards [C3](charter-spec-v1.md#c3-editor-layout). The canvases, the Properties
panel and the tools came with CM2 step 5. The Chart Inspector is still a placeholder.

**Cleanup:**

- **The wizard's back end** (`ChartPacker.Core/Charting/NewChart*.cs`, with its tests). New chart
  now opens the Editor straight away, and uses its stem naming (`NewChartStems.TracksOf`). A new
  chart's first save writes its folder with `NewChartCreator.CreateAsync(NewSongFolder)`, from the
  chart and `song.ini` as any save writes them. The wizard's own overload, taking a
  `NewChartRequest`, and `NewChartMidi`, `NewChartIni` and `NewChartLength` behind it, are now used
  only by their tests, and can go.
- **Shared dialogs.** `ConfirmDialog` and `MessageDialog` are still in `ChartPacker/Views`. They
  move to `ChartPacker.UI` when Chart Builder first needs them.
- **`ViewLocator`.** Chart Builder still has none. Its lanes and canvases are built in code, so
  nothing needs one yet ([§4.5](charter-spec-v1.md#45-app-chartbuilder)).
- **The spec's CM0 row** still names the wizard, and should be restated.

## CM1: Model, writer and lint engine

**Status: done.**

Most of it is in `ChartPacker.Core/Charting/`, with its tests in the new `ChartBuilder.Tests`.

**What was built:**

- **The editable model.** `EditableChart`, `EditableTempoMap` with anchors, `EditablePart`,
  `EditableTrack`, `EditableNote`, `Phrase`, `TextEvent`, `RawEvent`, `PassthroughTrack`,
  `DrumMix` and `TickList<T>`, and `ChartModelComparer` to compare two models.
- **Loading.** `MidiChartLoader` reads a `notes.mid`, or a `.chart` through `ChartToMidi`, and
  reports what it couldn't take in as written as `ChartLoadIssue`s.
- **The game view.** `GameView` projects the model to the `ChartDocument` the games read, and
  `PartEventOrder` fixes the order a part's events are written in, which the games depend on.
- **Writing.** `MidiChartWriter` normalizes, writes and checks a save (`WriteChecked`) by reading
  it back both ways (`ChartReadBack`). `NewChartMidi` now goes through it.
- **Re-ticking.** `TickMaps.KeepTimes` and `ChartRetick`, for tempo edits that keep times.
- **Commands and undo.** `IChartCommand`, `CompositeCommand`, `ChartUndoStack` and 23 commands in
  `Commands/`.
- **The lint engine** in `Lint/`: rule scopes, `ChartLinter` with full and partial runs,
  `Finding`'s `ChartLocation`, and chart quick fixes (`IChartQuickFixStrategy`). It has no rules
  yet.
- **The reader.** `MidiChartReader` reads accents and ghosts as YARG does. `MidiTrackNotes` and
  `GameNotes` share the note pairing and the drum rules with the loader and the game view.
- **Saving.** `SongFolderFiles.ApplyAsync` now moves new files in before deleting
  ([AC C2.4](charter-spec-v1.md#c2-open-and-import)).
- **Logging.** Both apps log through Serilog (`ChartPacker.UI/AppLog.cs`), with a Logging setting
  that opens the log folder.

**Left for later:**

- **The CHR lint rules.** The timing, 5-fret and song rules came with CM2; the drums and phrase
  rules come with CM3.
- **Cymbal/tom toggling and drum conversion** (CM3).
- **Two re-save cases.** A mix kept as text before a standard mix on the same tick, and a tom
  marker before a fifth-pad note on the same tick. The game view reads both as the games do, but
  re-saving changes how such a file plays. Each is a candidate for a load issue or a lint rule.

**Why it came first.** Every editing feature works on the editable model, and every save goes
through the writer and its read-back check. CM1 needs no UI, so it is the cheapest place to get
correctness right: round trips, lossless passthrough and the game view. This needs to be settled
before any canvas depends on it.

**Contents** ([§13](charter-spec-v1.md#13-milestones)):

- [§4.1](charter-spec-v1.md#editable-model): `EditableChart` and the types beside it, and
  `MidiChartLoader`.
- [§4.2](charter-spec-v1.md#lossless-passthrough): passthrough and re-ticking.
- [§4.3](charter-spec-v1.md#43-writing-midichartwriter): `MidiChartWriter`, the game-view
  projection and the extended read-back check, and `MidiChartReader` reading dynamics.
- Commands and undo.
- The lint engine: rule scopes, `Finding`'s chart location, and chart quick fixes.
- The reorder of `SongFolderFiles.ApplyAsync` to move new files in first, then delete
  ([§4.6](charter-spec-v1.md#46-saving)). It used to delete first.

**Reused, not rewritten.** These existing Core internals carry over:

- `MidiFile`/`MidiEvent`, `MidiLayout` and `MidiChartReader`, for reading;
- `MidiWriter`/`MidiMessage`, for writing;
- `NoteBuilder`, for natural HOPOs;
- `NoteCheck` and `ChartMergeWarning`, for the comparison;
- `TempoMap`, for tick ↔ seconds;
- `ChartToMidi`, for `.chart` import.

## CM2: Calibration, chart settings and 5-fret editing

**Status: in progress.** Steps 1 to 6 are built and their tests pass; what's left is the hand
checks. It builds on CM1's model, writer and lint engine.

**Built out of order.** Step 3 came before the rest of step 2, so that calibration can be checked
against a real song: Chart Builder opens a song and plays it, with a metronome over the stems and a
timeline that draws the mix's waveform and the chart's beat lines under a playhead driven by
`SmoothPosition`. Latency compensation came with calibration. The rest of
[C10](charter-spec-v1.md#c10-playback) came with step 5: the transport bar, regions and the loop,
hit sounds and following.

This is the first milestone with a working editor. Its steps, in order:

1. **Specify Chart settings.** *Done.* It replaces the removed wizard
   ([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)), and restates C1's acceptance
   criteria. *Restated in step 4:* New chart opens the Editor straight away, with no dialog before
   it. Chart settings, a tab in the Editor's left panel, holds only the chart's timing and parts; everything
   else about the song is edited in the Chart Inspector, and a new chart's folder is chosen at its
   first save.
2. **Playback and calibration.** *Done.*
   - The playback changes ([§4.4.1](charter-spec-v1.md#441-playback-changes)): the post-stretch
     mix, the true source position and `SmoothPosition`, with AC C10.1's test. `StemPlayer` lays
     `Sounds` over the stretched mix, maps the grains played (`TimeStretcher.PlayedPosition`), and
     has `SmoothPosition`.
   - `Synth`, in `ChartPacker.Audio/Synthesis/`, with `MetronomeSchedule`.
   - [C11](charter-spec-v1.md#c11-calibration-tool) calibration, with the shared latency file:
     - the output's device;
     - `audio-latency.json`;
     - the tap-test estimator;
     - `CalibrationWindow`;
     - the latency applied in both apps ([C10](charter-spec-v1.md#c10-playback) item 5).

     It opens from `--calibrate`, either app's Settings and the first-use prompt. The transport
     bar's button comes with the transport bar (step 5).

   Calibration comes first because every timing judgement after it depends on it.
3. **Open and import** ([C2](charter-spec-v1.md#c2-open-and-import)). *Done*, but for the parts
   that need the editor: C2 items 5 to 7 (add and remove parts, and recovery) come with steps 4 to
   6, and the import's `delay` and `[Song]` values become pending changes with C17.
   - `SongLock`, and Chart Packer's refresh when its window is activated
     ([§4.8](charter-spec-v1.md#48-launching-and-coordination)). Chart Packer asks before saving
     metadata to, or deleting, a song Chart Builder has open. A merge that overwrites an existing
     `.sng` doesn't ask yet.
   - **Edit chart** turned on, asking to save or discard a song's unsaved drafts first.
   - The launcher's **Open chart**, recent songs and dropped songs, and `.chart` import with its
     warnings shown first (`SongOpener`).
4. **New chart and Chart settings** ([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)),
   without **Detect from audio**. *Done*, but for the first save and **New chart in this song**,
   which come with step 6's save.
   - **New chart**, from the launcher's tile, dropped audio and `--new`, opens the Editor straight
     away with an untitled chart (`EditorSong.New`): the default resolution, 120 BPM, 4/4 and a
     Lead part, with the audio files as its stems, played from where they are.
   - **Chart settings**, a tab in the Editor's left panel beside the Chart Inspector, for a new
     chart or a song opened: timing and parts, each change applied as it's made as one undoable
     command (typed numbers once the typing pauses), with the resolution change and its warning.
     ⚙ opens Program settings.
   - Undo and redo in the Editor, and a read-only list of the pending `song.ini` changes.
5. **The editor.** *Done*, but for what needs phrases or drums (CM3): the Phrase tool, cymbal
   and tom, accents and ghosts, and the 2× kick. Lint marks on the overview come with step 6.
   - [C3](charter-spec-v1.md#c3-editor-layout) both views.
     - Each lane's canvas is a highway or a timeline, and `V` switches the lane in focus.
     - Both views pass the pointer and the keys to Core's `EditorTools`, through `LaneProjection`.
     - Other C3 pieces: the difficulty letters and a ghost difficulty in each lane's header, and
       Properties for notes, tempo markers and a span.
     - The overview is the bottom timeline.
     - The highway's drawing (`GemPainter`, `HighwayPalette`, `LaneWaveformPainter`) moved to
       `ChartPacker.UI`. Chart Packer's player and highway view models stay where they are.
   - [C4](charter-spec-v1.md#c4-tempo-map-and-grid) tempo map and grid:
     - the snap;
     - the Tempo tool, and dragging anchored markers;
     - anchor absorption, with the offers to unanchor or to add a marker;
     - tap tempo;
     - Set BPM from the selected span;
     - measure numbers.
   - [C5](charter-spec-v1.md#c5-5-fret-notes) 5-fret notes:
     - placing, moving, erasing and sustaining notes;
     - the open note rule;
     - H, G and T on chords;
     - natural HOPOs, sustain cutoffs and star power, drawn from the game view.
   - [C10](charter-spec-v1.md#c10-playback) playback:
     - the transport bar: loop, hit sounds, stems and the latency;
     - playing from the cursor, and Shift+Space for the selection;
     - the loop region, in `StemPlayer`;
     - following.
6. **Saving and lint.** *Done*, in three slices: saving, then recovery, then lint.
   - **Save** ([C17](charter-spec-v1.md#c17-save-and-export)), Ctrl+S and the toolbar: the chart is
     written in memory and read back both ways (a difference stops it), then saved through the
     song's draft with its pending `song.ini` changes. Save and pack, gated on Error findings, and
     Export copy.
   - The **pending changes** are their own tab in the right panel, each with Revert and Restore, but
     for those the chart is read by.
   - A new chart's **first save** ([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)): a
     dialog with its name and artist and where its folder goes, written with `NewChartCreator`,
     then the song locked and on the recent songs.
   - **New chart in this song**, for a song with neither chart file.
   - The title's `*` for unsaved edits, and **Save, Discard or Cancel** on closing.
   - [C15](charter-spec-v1.md#c15-editing-ergonomics) **autosave and recovery**, with the offer on
     open and the home screen's **Unsaved new charts**.
   - The timing, 5-fret and song **lint rules** ([§6](charter-spec-v1.md#lint-rules)) with their
     fixes, the **Findings** tab, and lint marks on the bottom timeline.

### CM2 progress

As of 2026-10-07. Update this as each step lands, a test is added, or a hand check is done.

**Steps.** "Hand-checked" means the user has tried it in the running apps (see
[Where CM2 stands](#where-cm2-stands) for the checks).

| Step | Built | Tests | Hand-checked |
| ---- | ----- | ----- | ------------ |
| 1. Specify Chart settings | Done | — | — |
| 2. Playback and calibration | Done | Pass | Not yet |
| 3. Open and import | Done, but for C2 items 5 to 7 | Pass | Not yet |
| 4. New chart and Chart settings | Done, but for the first save and New chart in this song (step 6) | Pass | Not yet |
| 5. The editor (C3, C4, C5, C10) | Done, but for phrases and drum flags (CM3) | Pass | Not yet |
| 6. Saving and lint (C15, C17, rules) | Done | Pass | Not yet |

**Exit criteria** ([§13](charter-spec-v1.md#13-milestones): AC C1, C2, C3, C4, C5, C10, C11,
C15.2 and C17).

- **Passes:** the AC's own test passes, as the spec states it.
- **In part:** a test covers the back end or the model, but the editor or the save it also
  needs isn't built, or the AC's app flow is built but not tested.
- **Not yet:** nothing covers it.

| AC | Status | Covered by, or what's missing |
| -- | ------ | ----------------------------- |
| C1.1 | Passes | `ChartSaveTests` (a new chart's first save, through `ChartSaver`, validates, packs and reopens with the same tempo map, parts and metadata) |
| C1.2 | Passes | `ChartSaveTests` (cancelled, and a stem that can't be copied: no folder or temp folder left) |
| C1.3 | Passes | `ChartSaveTests`, `NewChartFolderTests` |
| C1.4 | Passes | `NewChartStemsTests` (`TracksOf`), `NewChartCreatorTests` |
| C1.5 | Passes | `NewChartIniTests`, `NewChartCreatorTests` (reopened with the same layout), `PendingSongIniChangesTests` |
| C1.6 | Passes | `ChartSettingsCommandTests` |
| C1.7 | Passes | `ChartSettingsCommandTests`, `PendingSongIniChangesTests`; the Editor's list waits on a hand check |
| C2.1 | Passes | `ChartSaveTests` (every fixture opened and saved to disk without edits), `MidiChartWriterTests` |
| C2.2 | Passes | `ChartSaveTests` (the `[Song]` values and `Offset` reach `song.ini`, and `notes.chart` is replaced) |
| C2.3 | Passes | `ChartSaveTests` (the mask and every other file kept) |
| C2.4 | Passes | `SongFolderFilesTests` (CM1) |
| C2.5 | In part | `SongLockTests`; the second Chart Builder's refusal is built, and waits on a hand check |
| C2.6 | In part | `ChartSaveTests` (a save changes the song's stamp, and its summary reads the saved chart); Chart Packer's refresh on activation waits on a hand check |
| C3.1 | Passes | `EditorViewportTests` (the session's cursor, span and selection, projected into both views) |
| C3.2 | Passes | `EditorToolsTests`, each tool run through `EditorTools` in both views |
| C4.1 | Passes | `TempoMapEditsTests` |
| C4.2 | Passes | `RetickTests`, `TempoMapEditsTests` (through the Tempo tool) |
| C4.3 | Passes | `EditableTempoMapTests` |
| C4.4 | Passes | `ChartSaveTests` (anchors through a save, a pack and a reopen), `MidiChartWriterTests` |
| C5.1 | Passes | `MidiChartWriterTests`, `FiveFretEditingTests` (made with the tools, in both views) |
| C5.2 | Passes | `NaturalHopoDisplayTests` (the session's game view against `ChartReader`, for every HOPO setting) |
| C10.1 | Passes | `StemPlayerTests` (slow: about 30 s) |
| C10.2 | Passes | `LatencyCompensationTests` |
| C11.1 | Passes | `CalibrationScheduleTests` |
| C11.2 | Passes | `TapTestTests` (the mean of the kept taps, not the median) |
| C11.3 | Passes | `LatencyCalibrationTests` |
| C11.4 | Passes | `CalibrationScheduleTests` |
| C11.5 | Passes | `LatencyCalibrationTests` |
| C15.2 | Passes | `RecoveryTests` (the chart as of the last autosave, with its reverts; the song untouched) |
| C17.1 | Passes | `ChartSaveTests` (what a kill at each step of a folder save leaves; a failed copy leaves `song.ini` as it was) |
| C17.2 | Passes | `ChartSaveTests` (randomly edited fixtures saved to disk read as the editor's game view), `MidiChartWriterTests` |
| C17.3 | Passes | `ChartSaveTests` (only the pending keys change, in a folder's `song.ini` and a `.sng`'s metadata) |
| C17.4 | Passes | `ChartSaveTests`, `MidiChartWriterTests` |

**Totals:** 31 pass, 2 in part (C2.5 and C2.6, which wait on hand checks), 0 not yet. AC C16.1 and
C16.2, CM5's, pass too (`ChartRuleTests`, `ChartLinterTests`).

### Where CM2 stands

As of 2026-10-07, for picking the work up again.

**Waiting on the user.** Steps 2 to 6 have been built and their tests pass, but the apps haven't
been tried by hand yet. The checks still to do:

- open a song folder and a `.sng` from **Open chart**, by drop and from **Recent**;
- play, pause and seek, change the speed with **Keep pitch** on and off, and see the metronome's
  clicks land on the beat lines;
- open a song with only `notes.chart`, and a song already open in another Chart Builder;
- in Chart Packer, **Edit chart** on a song with unsaved drafts (Save, Discard, Cancel), and the
  refresh when its window is activated after the song changed on disk;
- Chart Packer's inspector playback at 50%, 100% and 150%, with pitch correction on and off: a
  stretched `Position` now follows the grains played, so it can be up to about 12 ms from before;
- `ChartBuilder --calibrate`, and the tool from Chart Builder's and Chart Packer's Settings
  (**Audio/visual latency**, **Calibrate…**):
  - the light, the note and the beep line up at 0 ms on wired output, and each bar's first beat
    is marked;
  - the slider, the steps, the number box and the arrow keys (Shift for 5) move the beep and
    never the light;
  - the tap test, its result and **Use this value**;
  - Save, Cancel (closing the window too) and Reset, seen in `audio-latency.json`;
- switching the default output device (headphones in or out) while the tool is open: the device
  shown changes, and so does its value;
- the first-use prompt, which comes once per device when a song opens in the Editor;
- the Editor's playhead drawn earlier by the latency, at 50% and 100%;
- Chart Packer's highway, lyrics and background moved by the latency after its window is
  activated;
- **New chart** (step 4), from the launcher's tile, from audio files dropped on the launcher, and
  from `ChartBuilder --new <files>`:
  - the Editor opening straight away, titled **Untitled chart**, with no dialog first;
  - `Guitar.OGG`, `Drums.ogg` and `mix.mp3` played together, with their waveform on the timeline;
  - `mix.mp3` and `other.mp3`, and a file that isn't audio, with the notice saying which weren't
    added and why;
  - no audio: a silent timeline, and no "no audio" notice;
  - **Pending song.ini changes** listing `diff_guitar = -1`;
- **Chart settings** (the left panel's tab), on a new chart and on an opened song:
  - a change put back as it was: no undo step, nothing pending; typing a BPM while the song
    plays: one undo step once the typing pauses, and the beats follow;
  - the collapsed left panel's strip showing both tabs, each opening the panel on its tab;
  - a starting BPM change with **When a tempo changes** (Settings, **Editing**) on each choice;
  - adding a part, which shows `diff_<part> = -1` under **Pending song.ini changes**, and
    unticking a part with notes, which asks first;
  - the drum variant locked while the drums have notes;
  - 480 → 960 with no warning, and 480 → 192 on a chart with off-grid notes, with the warning,
    **Change anyway** and Cancel;
  - Undo and Redo (Ctrl+Z, Ctrl+Y) of each, with the timeline's beat lines and the metronome
    following the tempo map.
- **Chart Packer's highway**, whose drawing moved to `ChartPacker.UI`: the notes, HOPOs, taps,
  cymbals, opens, kicks, sustains and the lane waveforms look as they did, outlined too.
- **The editor (step 5)**, in a highway lane and again after `V` (or the lane's view button) in a
  timeline lane:
  - **Place**:
    - a click puts a note on the grid, and a drag draws out its sustain;
    - Shift-click puts an open note;
    - an open in a chord with frets, or a fret on an open, is refused, with the status line saying
      why;
    - the hover ghost shows where the note would go.
  - **Select**:
    - a click selects, and Shift-click adds;
    - dragging notes moves them on the grid and across lanes, and an open moves only in time;
    - dragging a tail sets a sustain;
    - a drag in an empty place boxes notes and shades the span;
    - a click in an empty place moves the cursor.
  - **Erase** and **Sustain** by click and drag; Escape mid-drag puts everything back.
  - The keys:
    - 1–5 and 0 at the cursor;
    - ↑/↓ (←/→ in a timeline) and PgUp/PgDn;
    - Alt+arrows on the selection;
    - `[` `]`, Ctrl+A, Delete, H, G and T;
    - Ctrl+1–4, and the header's E M H X letters, coloured where the difficulty has notes;
    - Ctrl+Shift+Z.
  - The **ghost difficulty** menu, drawn faintly behind.
  - What's drawn:
    - natural HOPOs, star power, and a dashed tail under the sustain cutoff;
    - H, S and T marks by forced chords;
    - measure numbers, tempo labels with ⚓ for anchored markers, and the snap's grid lines.
  - **Tempo**:
    - a click adds a marker, and dragging one moves it;
    - dragging an anchored one refits the tempos either side;
    - BPM, time signature and Anchored in Properties;
    - a BPM edit that would move an anchor offers Unanchor and Add marker before;
    - **When a tempo changes** on Keep times leaves the notes where they were heard.
  - **Tap tempo**: B in time with the song, while it plays, sets the tempo once the taps stop.
  - **Set BPM from span**: select a span with a box, give its beats in Properties.
  - **Properties** for one note, several (`<<multiple>>`), and markers: typed values apply when the
    field loses focus.
  - **Scrolling and look-ahead**:
    - The wheel moves the song past the strike line, which stays a quarter of the way in, a grid
      step a notch. The cursor moves with it, and the audio seeks there, playing or paused.
    - Past a tempo change and a time signature that starts off the grid, the strike line still
      lands on the grid lines and in time with the track.
    - Each lane's look-ahead (its header's button and slider; Ctrl+wheel, + and −) sets how far it
      shows beyond the strike line.
    - The look-ahead comes back with the lane in the next Editor window.
    - While playing, the strike line stays on the playhead.
- **The transport bar**:
  - Space plays from the cursor and pausing leaves the cursor there;
  - Shift+Space plays the selection and stops at its end;
  - L sets the loop region from the selection, and Loop repeats it at 50%;
  - Hit sounds on the notes shown, with the volume in Settings (**Audio**);
  - Stems mute, solo and volume;
  - the latency button opens calibration and shows the new value after Save.
- **The bottom timeline** shows the sections, the focused lane's note density, star power and solos,
  and a box round the span the lanes show. Clicking it moves the cursor and the view there.
- **Settings › Charter › Editing**: Default snap, Default view and Highway speed, each taken up by
  the next Editor window. Highway speed is the look-ahead of lanes whose own hasn't been set.
- **Saving (step 6)**:
  - Save (Ctrl+S, the toolbar) on a song folder, a `.sng` and an imported `notes.chart`: the prompt
    listing the `song.ini` changes only when there are some, **Remove notes.chart** and **Keep
    notes.chart** on the import, the chart and `song.ini` as saved, and the status line's Saved.
  - `[end]` moved after the last note as an undo step of its own when the chart has notes past it.
  - A new chart's first save: the dialog's Name and Artist (from the first stem's tags), the folder
    name following them until edited, Browse, the problem shown for a folder that's there, Save and
    Cancel; afterwards the title is the song's, it's on **Recent**, and a second Chart Builder
    won't open it.
  - **New chart in this song**, opening a folder with audio and `song.ini` but no chart.
  - The title's `*` while there are unsaved edits, gone after Save and after undoing back to it.
  - Closing with unsaved edits: Save, Discard, Cancel; and with none, no prompt.
  - **Pending changes**: Revert and Restore, reverted rows struck through, no Revert on the drum
    keys (tooltip); reverting an import's `delay` moves the beats and the notes against the audio.
  - **Save and pack**: the `.sng` in Chart Packer's output folder, Skip with one already there,
    and the button disabled with an Error finding (tooltip) or on a `.sng`.
  - **Export copy**: a folder picked, the copy in it, the song itself unchanged.
  - Chart Packer, activated after a save, showing the saved chart and `song.ini`.
- **Recovery (step 6)**:
  - Edit, wait two minutes or stop playback, then end Chart Builder from Task Manager. Reopening the
    song offers **Recover** or **Discard**; Recover opens it unsaved, with the notice, and the edits
    up to the autosave.
  - The same with the song's chart changed on disk meanwhile: the warning in the offer.
  - A new chart, never saved, killed the same way: **Unsaved new charts** on the home screen, with
    Recover and Discard (which asks).
  - **Autosave every** in Settings › Charter › Editing.
  - Nothing left in the recovery folder (`%APPDATA%\ChartPacker\Recovery`) after a save, or after
    closing with Discard.
- **Lint (step 6)**:
  - The **Findings** tab: the summary, rows coloured by severity, → going to the finding (its lane
    in focus, the cursor there, its notes selected), and Fix as one undo step.
  - Findings following edits within a moment, and not during a drag.
  - The marks on the bottom timeline's ruler for the lane in focus, changing with the focus.

**Where the code is.**

| Piece | Where |
| ----- | ----- |
| Grains played, the post-stretch sounds, `SmoothPosition` | `ChartPacker.Audio/Dsp/TimeStretcher.cs` (`PlayedPosition`, `FramesToNextGrain`), `Playback/StemPlayer.cs` (`Sounds`, `SoundVolume`, `SmoothPosition`), `Playback/PositionMap.cs` (`OutputAt`, 256 segments), `Playback/SoundSchedule.cs` (`SynthSound`, `ScheduledSound`, `ISoundSchedule`) |
| `Synth`, the metronome | `ChartPacker.Audio/Synthesis/` (`Synth`, `SynthHit`, `MetronomeSchedule`). Not `Synth/`: a namespace named like its class breaks lookups from beside it. |
| Opening a song | `ChartPacker.Core/Charting/SongOpener.cs` (`EntryOf`, `OpenAsync`, `OpenedSong`), `Scanning/SongLock.cs`, `Launching/RecentSongs.cs`, `UserConfigPaths.LockFolder` |
| Chart Builder | `ViewModels/MainWindowViewModel.cs` (open flow, import prompt, Recent), `ViewModels/PlaybackViewModel.cs` (player, peaks, beats, metronome), `Controls/TimelineView.cs` (replaced `TimelinePlaceholder`), `Services/BuilderWindows.cs` (`OpenSong`), `App.axaml.cs` (`SdlAudio`) |
| Chart Packer | `ViewModels/LibraryViewModel.cs` (`EditChartAsync`, `PrepareEditChart`, `RefreshEditedSongsAsync`), `ViewModels/MainWindowViewModel.cs` (`PrepareEditChartAsync`, `OnActivatedAsync`), `Services/ChartBuilderSongs.cs` (the lock prompt), `Views/MainWindow.axaml.cs` (`Activated`) |
| Shared UI | `ConfirmDialog`, `IConfirmationService` and its `ChooseAsync` (two actions and Cancel) moved to `ChartPacker.UI`; `IFilePickerService.PickSongAsync`; `ActionSettingViewModel` and its template in `SettingsWindow.axaml` |
| The output's device | `ChartPacker.Audio/Playback/AudioDevice.cs`, `IAudioOutput` (`Device`, `DeviceChanged`), `SdlAudioOutput.cs` (checked on `Resume` and once a second), `WindowsAudioEndpoint.cs` (the endpoint ID, by hand-made COM calls) |
| The latency file | `ChartPacker.Core/Settings/AudioLatencyFile.cs` (`Find`, `FindAndRemember`, `Save`, `RememberUnderId`), `Settings/JsonFiles.cs` (the temp-and-rename write, shared with `JsonSettingsFile`), `UserConfigPaths.AudioLatencyFile` |
| Calibration logic | `ChartPacker.Core/Calibration/` (`TapTest`, `LatencyCalibration`), `ChartPacker.Audio/Synthesis/` (`CalibrationBeats`, `CalibrationSchedule`, `CalibrationBeepsGiven`, `Synth.CalibrationBeep`), `Playback/LatencyCompensation.cs` |
| Settings rows | `ChartPacker.Core/Settings/` (`ActionSetting`, `ActionSettingEdit`) |
| New chart and Chart settings' Core | `ChartPacker.Core/Charting/` (`EditorSong`, `NewChartStems.TracksOf` and `NewChartAudio`, `ChartSettingsEdit` and `ChartSettingsValues`, `ResolutionChangeReport`, `PendingSongIniChanges`, `SongIniPartKeys`), `Commands/TempoMapCommands.cs` (`ChangeResolution`, `SetStartingTempo`, `ResolutionScaling`), `Commands/PartCommands.cs` (`SetDrumsVariant`), `EditablePart.HasNotes`; `ChartPacker.Audio` (`SongAudio.Open` and `StemPeaksCache.KeyOf` take no song for staged tracks) |
| Saving (Core) | `ChartPacker.Core/Charting/Saving/` (`ChartSaver`: `RequestFor`, `Check`, `SaveAsync`, `SaveNewAsync`, `ExportAsync`, `SavedSong`; `ChartSaveFixups.MoveEnd`), `Metadata/DraftWriter.cs` (`SaveAsync`, the folder save's order), `Scanning/SongFolderFiles.cs` (`afterMoves`, `StepReached`), `Charting/NewChartCreator.cs` (`NewSongFolder`), `Charting/EditorSong.cs` (mutable: `Saved`, `Revert`, `Restore`, `PendingChanges`, `EffectivePairs`, a live `Delay`), `Charting/PendingSongIniChanges.cs` (`PendingSongIniChange`, `PendingChangeReason`, `PendingSongIniInputs`, `Apply`), `Charts/ChartSongSection.cs`, `Metadata/SongAudioLength.cs`, `Charting/SongOpener.cs` (`ChartFileStamp`, `NoChartSong`, `NewChartIn`), `Commands/ChartUndoStack.cs` (`SavePoint`, `Mark`, `MarkSaved(SavePoint)`, `startsSaved`), `Editing/EditorSession.cs` (`SetSongSettings`, `SongSettingsChanged`), `Settings/ChartPackerSettingsFile.cs` (`PackSettings`, `ChartPackerPackSettings.PackOf`) |
| Saving (Chart Builder) | `ViewModels/EditorWindowViewModel.Saving.cs` (Save, the first save, Save and pack, Export copy, `ConfirmCloseAsync`), `ViewModels/FirstSaveViewModel.cs`, `Views/FirstSaveWindow.axaml`, `Services/IFirstSaveDialogService.cs`, `Services/EditorServices.cs`, `ViewModels/PendingChangeItem.cs`, `Services/BuilderWindows.cs` (the close prompt), `MainWindowViewModel` (New chart in this song), `BuilderSettings.LastNewChartFolder` |
| Autosave and recovery | `ChartPacker.Core/Charting/Recovery/` (`RecoveryStore`, `RecoveryManifest`, `RecoveryOffer`, `Autosave`), `UserConfigPaths.RecoveryFolder`; `ViewModels/EditorWindowViewModel.Autosave.cs`, `MainWindowViewModel` (`OfferRecoveryAsync`, `UnsavedCharts`), `IEditorWindowService.OpenRecovered`, `BuilderSettings.AutosaveMinutes` |
| Lint | `ChartPacker.Core/Charting/Lint/Rules/` (`TimingRules`, `FiveFretRules`, `SongRules`), `ChartLintRules.All`, `ChartFixes`, `PendingSongIniFixes`, `ChartLintScheduler`; `Validation/ChrRules.cs`, `Finding.Tick`, `ChartLintInput.Delay`, `IChartQuickFixStrategy.Propose(…, options)`, `Resources/FindingMessages.resx`; `ViewModels/FindingsViewModel.cs`, `TimelineView.FindingMarks` |
| New chart and Chart settings in Chart Builder | `BuilderWindows.OpenNewChart`, `EditorWindowViewModel` (`ForNewChart`, `ForSong`, the undo stack, `ApplyChartSettings`, `PendingChanges`), `ViewModels/ChartSettingsViewModel.cs`, `Views/ChartSettingsView.axaml`, `Controls/EditorSidePanel.cs` (the left panel's tabs), `PlaybackViewModel` (takes an `EditorSong`; `RefreshTempoMap`), `BuilderSettings.TempoEdits` |
| Calibration in Chart Builder | `Services/AudioLatencyService.cs`, `ViewModels/CalibrationViewModel.cs`, `Controls/CalibrationView.cs`, `Views/CalibrationWindow.axaml`, `Services/BuilderWindows.cs` (`ShowCalibrationAsync`, the first-use prompt), `BuilderSettingsCatalog` (the **Audio** category), `BuilderSettings` (`PromptForCalibration`, `CalibrationPrompted`), `PlaybackViewModel.Clock` |
| The latency in Chart Packer | `ViewModels/PlayerViewModel.cs` (`DrawnPosition`, `RefreshLatency`), which the highway, lyrics and background clocks use; `MainWindowViewModel.OnActivatedAsync`; `AppSettingsCatalog` (`playback.latency`); `App.axaml.cs` |
| The editor's state and tools (Core) | `ChartPacker.Core/Charting/Editing/`: `EditorSession` (the undo stack, the delay, the game view per track, previews and `ExecuteAsOne`), `EditorTools` (the gestures and the keys' actions, `HitTest`, `PlacementRefusal`, `ApartLane`), `EditorSelection`, `EditorViewport` (the cursor, which is the strike line, and each lane's span from its look-ahead), `LaneProjection` and `LaneSlots`, `SnapGrid`, `TempoMapEdits` (anchor absorption), `TapTempo`; `GameView.Project(chart, key, options)` and `GameView.IsFiveLaneDrums` |
| The editor (Chart Builder) | `ViewModels/ChartEditorViewModel.cs` (the session joined to playback, the tools and snap, hit sounds, the overview, tempo edits that ask about anchors), `PropertiesViewModel.cs`, `EditorLaneViewModel.cs` (the view, the letters, the ghost and the look-ahead, kept by `LaneDockLayout.SetLookAhead`), `StemRowViewModel.cs`; `Controls/Editing/` (`EditorCanvas`, `HighwayEditorCanvas`, `TimelineEditorCanvas`, `EditorKeys`); `Views/PropertiesView.axaml`; `Controls/TimelineView.cs` (the overview); `EditorWindow.axaml` (the toolbar's tools and snap, the status line, the transport bar) |
| Shared drawing | `ChartPacker.UI/Controls/Highway/` (`GemPainter`, `HighwayPalette`), `ChartPacker.UI/Controls/LaneWaveformPainter.cs` and `WaveformLevels.cs` (moved from Chart Packer); `ChartPacker.UI` now references `ChartPacker.Audio` |
| Regions, loop and hit sounds | `StemPlayer.PlayRegion`, `ClearRegion`, `Region`; `ChartPacker.Audio/Synthesis/HitSoundSchedule.cs` (`HitSoundSchedule`, `NoteHit`, `CombinedSchedule`); `PlaybackViewModel` (`PlayRegion`, `SetLoop`, `SetHits`, `Stems`, `LatencyText`, `SeekTo`, `Scrubbed`) |
| Step 6's tests | `ChartBuilder.Tests/Charting/Saving/` (`ChartSaveTests`, `SavePiecesTests`; the shared `SaveFolders`), `Charting/Recovery/RecoveryTests.cs`, `Charting/LintRules/` (`ChartRuleTests`, `ChartLintSchedulerTests`), `Charting/Lint/ChartLinterTests.cs` (the random edits and AC C16.2 with the CHR rules), `ChartPacker.Core.Tests/Settings/ChartPackerSettingsFileTests.cs` |
| Tests | `ChartBuilder.Tests/Editing/` (`SnapGridTests`, `EditorViewportTests`, `EditorToolsTests`, `TempoMapEditsTests`, `FiveFretEditingTests`, `NaturalHopoDisplayTests`; the shared `EditorSessions`); `ChartPacker.Core.Tests/Audio/` (`TimeStretcherTests`, `PositionMapTests`, `StemPlayerTests`, `SynthTests`, `CalibrationScheduleTests`, `LatencyCompensationTests`, `WindowsAudioEndpointTests`; the shared `FakeOutput` and `ManualClock`), `Settings/AudioLatencyFileTests.cs`, `Settings/ChartPackerSettingsFileTests.cs`, `Scanning/SongLockTests.cs`, `Launching/RecentSongsTests.cs`, `Charting/NewChart*Tests.cs`; `ChartBuilder.Tests/Charting/` (`SongOpenerTests`, `ChartSettingsCommandTests`, `PendingSongIniChangesTests`), `ChartBuilder.Tests/Calibration/` (`TapTestTests`, `LatencyCalibrationTests`) |

**Decided along the way.**

- The grains played are measured as the two grains a frame is made of, weighted as they're faded
  together ([§4.4.1](charter-spec-v1.md#441-playback-changes)). It's tested at each click's
  loudest copy, on average, as WSOLA can play a click twice where two grains are nearly as loud.
- Four `StemPlayerTests` assertions on a stretched `Position` were widened from 1 ms to 13 ms
  (`Stretched`): their stem is a ramp, on which every grain is taken nearly as far back as it can
  be.
- AC C10.1's test plays 5 minutes at three speeds and takes about 30 s. It's marked
  `[TestCategory("Slow")]`, but nothing filters on that yet. `StemPlayer` has an internal
  constructor that sets the queue's length, so the test doesn't wait on a 150 ms queue.
- Chart Packer is told whether Chart Builder has a song open by a `Func<string, bool>` built in its
  `App` from `SongLock.IsHeld`, passed like the existing `hasDrafts`. `SongLock.TryAcquire` tries
  twice, 50 ms apart, as `IsHeld` holds the file for a moment.
- Chart Packer re-reads a song opened in Chart Builder by `EntryStamps` (every file in the
  folder), for every song opened since it started.
- **The device is polled.** `SdlAudioOutput` checks its device when playback starts and once a
  second. SDL's device events need an SDL event loop the app doesn't run, and SDL's silent move to
  a new default raises none.
- **Only Windows has a device ID** (`WindowsAudioEndpoint`). Other platforms are keyed by SDL's
  name.
- **The tap test averages the kept taps.** The spec's median missed AC C11.2 (92% within ±20 ms);
  the mean passes (96.5%). Its spread is the larger of the two phases' IQRs.
- **The tap test takes clicks only.** Avalonia 12.1 keeps the raw input events internal, and key
  events have no timestamp of their own.
- **The latency is stored in whole milliseconds**, within ±500, and `audio-latency.json` is edited
  as JSON nodes, so a save changes only its own device.
- **The beeps are re-anchored once a second** (`CalibrationSchedule`), with a shared
  `CalibrationBeepsGiven` so no beep plays twice. A latency raised past what's queued skips one
  beep.
- **The latency rows.** Both apps' Settings show it as an `ActionSetting`: a row with a
  **Calibrate…** button and no value.
- **What Chart Packer moves.** Its highway, lyrics and background are drawn at `DrawnPosition`:
  `Position` less the latency times the speed, while playing.
  - The seek bar isn't moved, as its position is the seek position.
  - The merge window's resume point still uses the audio's own `CurrentPosition`.
- **Pending `song.ini` changes are worked out, not kept** (`PendingSongIniChanges`). They come from
  the chart as it is against `song.ini` as it was opened, so undoing an edit takes its change away,
  and a drum variant detected on load ([§4.1](charter-spec-v1.md#editable-model)) is listed too. A
  part's rating is listed only for parts added since the song was opened.
- **Changing the resolution.**
  - The tempo and time signature markers' ticks are scaled to the nearest tick, halves up.
  - Markers that land on one tick are all kept, and the warning says the games read only the last.
    An anchor that lands on another's tick is left out, and listed.
  - The warning's dry run applies `ChangeResolution` to a copy, and compares each item's time
    under the old and the new tempo map.
- **The starting tempo, keeping times,** re-ticks everything, but leaves the other tempo markers on
  their ticks, as any tempo edit does until anchor absorption comes with C4 (step 5).
- **When a tempo changes** is a Settings row (**Charter › Editing**), as §10 lists it. It's the
  first thing to use it.
- **No wizard.** New chart opens the Editor straight away, on defaults. The dialog-first flow built
  at first was taken out, and the spec's C1 restated: a new chart is untitled until its first save,
  which asks where its folder goes.
- **A new chart's audio** is staged tracks (`SongTrack.StagedSourcePath`), as Chart Packer's drafts
  add stems, so playback and the waveform need no song folder.
- **A new chart's files with no stem are left out**, with a notice, rather than kept unassigned, so
  every track has a stem. Choosing stems comes with the Chart Inspector.
- **A new chart's pending changes** are every part's rating and the drum keys, with its name,
  artist, charter and `song_length` once known; its first save writes them as its `song.ini`.
- **The pending changes were listed under Properties** until step 6, which gave them, and the
  findings, tabs of their own in the right panel.
- `NewChartSettings.MaxNumerator` is now 64, as C4 allows, rather than 32.
- **The editor's state is in Core** (`Charting/Editing/`): the session, the tools, the selection,
  the viewport, the snap and the tempo edits. So the tool tests (AC C3.2) run without a canvas, and
  `ChartBuilder.Tests` still references only Core. There are no view-model tests of the app's own
  classes yet.
- **Only the drawing moved to `ChartPacker.UI`.** Chart Packer's player and highway view models stay
  in Chart Packer, as they're built round the inspector's song, drafts and offsets. The spec's §4
  and CM2 row are restated.
- **Views per lane.** Each docked lane is a highway or a timeline. `V` switches the lane in focus,
  and the cursor, the selection and the tool are the session's, shared by all of them. Each lane
  keeps its own look-ahead across a switch (AC C3.1).
- **The overview is the bottom timeline**, which already spans the song: the focused lane's density,
  star power and solos, the sections, and the span on screen.
- **The cursor is the strike line and the playhead.**
  - Every lane draws its strike line on the cursor, a quarter of the way from its strike end.
  - Scrolling moves the song past it, so the cursor moves through the song with the scroll. The
    audio seeks there, playing or paused. This replaces the spec's "scrolling pauses following".
  - A notch of the wheel is a grid step, as an arrow key is, worked out through the tempo map from
    the time signature in force. A step a fixed number of seconds long drifted off the grid once
    the tempo or time signature changed. A touchpad's part-notches add up to steps.
  - Pausing leaves the cursor where playback stopped, and Space plays from it.
  - Clicking the bottom timeline moves the cursor there.
- **Each lane has its own look-ahead**: how many seconds it shows beyond the strike line. It's set
  from the lane's header, or with Ctrl+wheel, + and −, and is saved with the lane layout
  (`DockLane.LookAhead`). A lane that hasn't had one set uses the highway speed setting's: 2 s at
  1×.
- **Gestures preview.** A drag applies its command to the chart as it goes, and is put on the undo
  stack once, when it ends (`EditorSession.Preview`, `CommitPreview`). Undo during a drag takes the
  drag back first.
- **The snap counts from the time signature in force**, and ends at the next one. It rounds to the
  nearest tick. Switched off, notes go to the nearest tick, but the cursor keys still step by it.
- **Anchor absorption** is by the last segment before the first anchor after the edit, and only
  segments after the edited marker count (AC C4.1). Removing and moving markers are absorbed the
  same way, and a marker can't be moved past an anchor. Every tempo edit is one `ChangeTempoMap`.
- **Forcing keys**:
  - H, G and T set the flag on every selected chord, or clear it when all of them have it.
  - Force HOPO and force strum exclude each other; tap goes with either.
- **Open notes**: a highway draws them across the lanes and places one with Shift-click or 0; a
  timeline gives them a row.
- **Tap tempo** needs three taps. It sets the segment the first tap was in, once the taps have
  stopped for 2 s, as one step, with the anchor prompt if it needs one.
- **Loops and played spans are in `StemPlayer`**:
  - The mix carries on from the region's start without dropping the queue.
  - Played as mixed, it turns on the region's end frame; stretched, within a chunk (20 ms) of it.
  - A seek drops a region played once, but not a loop.
- **The metronome's and hit sounds' volumes** are settings (Audio), applied when playback starts. 50%
  plays each sound at the level `Synth` makes it.
- **Hit sounds** are from the game view of the lanes shown: a fret sound for 5-fret and GHL, and per
  pad for drums.
- **What's drawn while paused** is at the audio's position. `PlaybackViewModel.Clock` takes off the
  latency only while playing, as Chart Packer's `DrawnPosition` does.
- **The editor isn't reopened after a save.** `EditorSong` moves on to the saved song
  (`EditorSong.Saved`), so the undo stack, the cursor, the lanes and playback carry on. A new chart
  becomes a song opened from its folder; its audio keeps playing from the files it was given until
  it's next opened.
- **The folder save's order** is stage, move in, `song.ini`, delete (charter-spec §4.6, restated).
  It changes Chart Packer's saves too, but only in order.
- **Pending changes are still worked out, not kept.** Only the reverts are kept, in `EditorSong`:
  they aren't undo steps, and they stay across saves. Those the chart is read by (the drum keys, an
  import's sustain cutoff and `multiplier_note`) can't be reverted. The list is its own tab beside
  Properties.
- **An import's sustain cutoff is a pending change.** A `.chart` whose sustains would read as
  notes under MIDI's default cutoff needs `sustain_cutoff_threshold`, as `ChartConversion` says, or
  its first save would play differently. The spec's "never changed by Chart Builder" is restated.
- **Save points.** A save marks the undo stack where its snapshot was taken (`Mark`,
  `MarkSaved(SavePoint)`), so edits made while it runs stay unsaved.
- **Save asks only when needed**: when it changes `song.ini` or removes a file. An import's first
  save always asks, Remove or Keep `notes.chart`.
- **`[end]` is moved by an undo step** once the save is going ahead; Export copy moves it in its own
  snapshot only.
- **Lint reads what the save would write**: the chart with `EffectivePairs` and the session's read
  options, so pending changes count. CHR083 therefore shows for every new part until it's rated,
  which needs the Chart Inspector or C18; CHR084 shows only when the `song_length` change is
  reverted, and its Fix restores it.
- **CHR021 counts lengths from a sixteenth** up to the cutoff, as Rock Band-style charts hold every
  plain note a sixteenth (spec restated).
- **CHR023** flags a force only when it changes nothing at every threshold a MIDI chart is read
  with. **CHR003** leaves `[end]` out. **CHR081** compares chords per second between adjacent
  difficulties. **CHR082** takes `[section …]` and `[prc_…]` as sections.
- **Song-wide findings have a tick** (`Finding.Tick`), as `ChartLocation` needs a part.
- **Chart fixes are given the read options**, for CHR021's cutoff and CHR023's thresholds.
- **Lint runs a moment after the edits stop** (150 ms), one run at a time, never during a drag;
  edits made while it runs are linted against its result next (`ChartLintScheduler`).
- **The Findings tab's header doesn't count**: the side panel's strip reads headers once. The
  counts are at the top of the panel.
- **Autosave**: every *n* minutes (Settings, 1 to 60) and when playback stops, only when something
  changed since; deleted once nothing is unsaved. A save writes it first, and deletes it once it has
  gone through. The OS shutting down autosaves without asking. A recovered chart opens unsaved.
- **Recovery offers unless the chart on disk is the autosave's, byte for byte**, warning when the
  chart has changed since (spec C2 item 7 and C15 restated). Untitled charts are listed on the home
  screen.

**Loose ends.**

- A merge that overwrites an existing `.sng` doesn't ask when Chart Builder has it open
  (`MergeWindowViewModel` has no `IConfirmationService`).
- The import's `Offset` is used as the delay but isn't yet a pending `delay` change, and the
  `.chart`'s `[Song]` values aren't read (C2 item 3, with C17's pending changes).
- The chart's load issues are only counted in the Editor's notice. They become findings with the
  findings panel.
- The Chart Inspector is still a placeholder; the Editor's title is the only sign of the song.
- The spec's CM0 row still names the wizard (CM0's cleanup).
- **Device IDs on macOS and Linux.** A Core Audio device UID and a PulseAudio or PipeWire sink
  name would make those platforms' keys survive renames. They need testing on those platforms.
- **Space as a tap**, should a later Avalonia make raw input timestamps public.
- **Phrases and drums in the editor** (CM3):
  - the Phrase tool, solos and the `S` key;
  - cymbal and tom (`C`), accents and ghosts (`A`), and the 2× kick (Shift+0);
  - GHL's stacked columns. GHL lanes are six columns for now.
- **Record mode** shifts key presses by the latency ([C15](charter-spec-v1.md#c15-editing-ergonomics)),
  with CM5.
- **A new chart's name and artist** are asked for at its first save, from its first stem's tags;
  the Chart Inspector edits them later.
- **Ratings.** CHR083 can't be cleared in the editor until a part can be rated (the Chart Inspector,
  or C18's suggested rating); CHR080 has no fix until C9's reduction.
- **The chart's load issues** are still only counted in the Editor's notice, not listed as findings.
- **A new chart's audio** plays from the files it was given until the song is reopened from its
  folder.
- **The wizard's leftovers** (CM0's cleanup): `NewChartRequest`, `NewChartMidi`, `NewChartIni` and
  `NewChartLength` are used only by their tests now.

**Next:** the hand checks above. Then [CM3](#cm3-drums-and-ghl): drums, GHL, phrases and their lint
rules.

## CM3: Drums and GHL

**Status: not started.**

It covers:

- [C6](charter-spec-v1.md#c6-drums-4-lane-pro-and-5-lane), all three drum variants and the
  conversion;
- [C7](charter-spec-v1.md#c7-6-fret-ghl-notes), GHL;
- [C8](charter-spec-v1.md#c8-phrases-and-events), phrases and events;
- the drums and phrase lint rules.

It comes after CM2 because it adds instruments to the editor CM2 builds, and reuses its views,
tools and save path.

## CM4: Audio assist

**Status: not started.**

It covers:

- [C12](charter-spec-v1.md#c12-waveform), the waveform;
- [C13](charter-spec-v1.md#c13-spectrogram), the spectrogram;
- [C14](charter-spec-v1.md#c14-onset-and-beat-assist), onsets and tempo estimation;
- **Detect from audio**.

It needs CM2's views to draw on and its tempo map to propose against. It doesn't need CM3, so the
two could be built in either order. **Detect from audio** now belongs in Chart settings rather
than the wizard.

## CM5: Authoring tools

**Status: not started.**

It covers:

- [C9](charter-spec-v1.md#c9-difficulty-authoring), reduction;
- [C15](charter-spec-v1.md#c15-editing-ergonomics) record mode, the clipboard and transforms;
- [C16](charter-spec-v1.md#c16-chart-lint), lint in Chart Packer's library;
- [C18](charter-spec-v1.md#c18-preview), preview and suggested difficulty.

It comes last because these tools build on the rest:

- reduction needs every instrument family (CM3);
- record mode and snap to onset need the onsets (CM4);
- lint in the library needs the full rule catalogue.

---

## Keeping this current

When a milestone's exit criteria pass, update its status here and in the table at the top.

While a milestone is in progress, keep its progress tracker current:

- mark a step built when it lands, and hand-checked when the user has tried it;
- move an AC to **Passes** when a test of it, as the spec states it, passes;
- update the totals and the **Progress** column at the top to match.

A milestone gets its tracker when its work starts.
