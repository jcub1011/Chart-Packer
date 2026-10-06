# Chart Builder — Roadmap

| | |
| --- | --- |
| **Status** | Current as of 2026-10-06 |
| **Companion docs** | [charter-spec-v1.md](charter-spec-v1.md) (what each milestone contains, and its exit criteria) |
| **Next up** | [CM2: Calibration, chart settings and 5-fret editing](#cm2-calibration-chart-settings-and-5-fret-editing), step 2: playback and calibration |

---

## Purpose

This file records the order in which Chart Builder's milestones are built, and where each one
stands. The spec's [§13](charter-spec-v1.md#13-milestones) owns what each milestone contains and
its exit criteria. This file owns only the order and the status.

## At a glance

| Order | Milestone | Status | Depends on |
| ----: | --------- | ------ | ---------- |
| 1 | [CM0: Split](#cm0-split) | Done (cleanup left) | — |
| 2 | [CM1: Model, writer and lint engine](#cm1-model-writer-and-lint-engine) | Done | CM0 |
| 3 | [CM2: Calibration, chart settings and 5-fret editing](#cm2-calibration-chart-settings-and-5-fret-editing) | **In progress** | CM1 |
| 4 | [CM3: Drums and GHL](#cm3-drums-and-ghl) | Not started | CM2 |
| 5 | [CM4: Audio assist](#cm4-audio-assist) | Not started | CM2 |
| 6 | [CM5: Authoring tools](#cm5-authoring-tools) | Not started | CM3, CM4 |

---

## CM0: Split

**Status: done, with cleanup left.**

Both apps build from one solution, share `ChartPacker.UI`, and publish and install together.
Chart Builder has its launcher, `BuilderArgs`, `BuilderSettings` and `JsonSettingsFile<T>`. Chart
Packer's **Edit chart** is in place, and stays disabled until CM2
(`ChartBuilderLauncher.CanOpenSongs`).

**Built ahead of plan.** The wizard was replaced by the Editor window
([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)), and the Editor's shell already exists:

- the toolbar;
- the collapsible side panels;
- lane docking (`ChartPacker.Core/Charting/Layout/LaneDockLayout`);
- a placeholder timeline.

This counts towards [C3](charter-spec-v1.md#c3-editor-layout). The canvases, the Chart Inspector,
the Properties panel and the tools are still placeholders.

**Cleanup:**

- **The wizard's back end** (`ChartPacker.Core/Charting/NewChart*.cs`, with its tests) is used
  only by tests now. Chart settings' **Create** reuses it
  ([CM2](#cm2-calibration-chart-settings-and-5-fret-editing) step 4), so it stays.
- **Shared dialogs.** `ConfirmDialog` and `MessageDialog` are still in `ChartPacker/Views`. They
  move to `ChartPacker.UI` when Chart Builder first needs them.
- **`ViewLocator`.** Chart Builder doesn't have one yet. It arrives with `ChartEditorViewModel`
  ([§4.5](charter-spec-v1.md#45-app-chartbuilder)).
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

- **The CHR lint rules.** The timing, 5-fret and song rules come with CM2, the drums and phrase
  rules with CM3.
- **`song.ini` pending changes from chart quick fixes** (CM2).
- **Anchor absorption** when a tempo marker is removed or moved (CM2).
- **Cymbal/tom toggling and drum conversion** (CM3).
- **The folder save's order.** `DraftWriter.SaveFolderAsync` still writes `song.ini` before the
  files. This is part of CM2's save work.
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

**Status: in progress.** Step 1 is done, and step 2 has started. It builds on CM1's model, writer
and lint engine.

This is the first milestone with a working editor. Its steps, in order:

1. **Specify Chart settings.** *Done.* It replaces the removed wizard
   ([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)), and restates C1's acceptance
   criteria. Chart settings is a dialog for the chart's timing and parts, plus what a new song
   folder needs; everything else about the song is edited in the Chart Inspector.
2. **Playback and calibration.**
   - The playback changes ([§4.4.1](charter-spec-v1.md#441-playback-changes)): the post-stretch
     mix, the true source position and `SmoothPosition`. *Built*, with AC C10.1's test: `StemPlayer`
     lays `Sounds` over the stretched mix, maps the grains played (`TimeStretcher.PlayedPosition`),
     and has `SmoothPosition`.
   - `Synth`. *Built*, in `ChartPacker.Audio/Synthesis/`, with `MetronomeSchedule`.
   - [C11](charter-spec-v1.md#c11-calibration-tool) calibration, with the shared latency file. Still
     to do: the output's device name, ID and `DeviceChanged`, `audio-latency.json`, the tap-test
     estimator and `CalibrationWindow`.

   Calibration comes first because every timing judgement after it depends on it.
3. **Open and import** ([C2](charter-spec-v1.md#c2-open-and-import)).
   - `SongLock` and Chart Packer's refresh
     ([§4.8](charter-spec-v1.md#48-launching-and-coordination)).
   - **Edit chart** turned on (`CanOpenSongs`).
   - The launcher's **Open chart** and recent songs.
4. **New chart and Chart settings** ([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)),
   without **Detect from audio**: New chart's **Create** (through `NewChartCreator`), and Chart
   settings on an open song, with the resolution change and its warning.
5. **The editor.**
   - [C3](charter-spec-v1.md#c3-editor-layout) both views. The player and highway view models
     move to `ChartPacker.UI` here.
   - [C4](charter-spec-v1.md#c4-tempo-map-and-grid) tempo map and grid.
   - [C5](charter-spec-v1.md#c5-5-fret-notes) 5-fret notes.
   - [C10](charter-spec-v1.md#c10-playback) playback.
6. **Saving and lint.**
   - [C15](charter-spec-v1.md#c15-editing-ergonomics) autosave and recovery.
   - [C17](charter-spec-v1.md#c17-save-and-export) save.
   - The timing, 5-fret and song lint rules ([§6](charter-spec-v1.md#lint-rules)).

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
