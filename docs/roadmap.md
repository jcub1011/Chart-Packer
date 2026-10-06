# Chart Builder — Roadmap

| | |
| --- | --- |
| **Status** | Current as of 2026-10-06 |
| **Companion docs** | [charter-spec-v1.md](charter-spec-v1.md) (what each milestone contains, and its exit criteria) |
| **Next up** | [CM2: Calibration, chart settings and 5-fret editing](#cm2-calibration-chart-settings-and-5-fret-editing), the rest of step 2: calibration. See [Where CM2 stands](#where-cm2-stands). |

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
Packer's **Edit chart** is in place, and was turned on in CM2 step 3.

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

**Status: in progress.** Steps 1 and 3 are done, and step 2 has started. It builds on CM1's model,
writer and lint engine.

**Built out of order.** Step 3 came before the rest of step 2, so that calibration can be checked
against a real song: Chart Builder opens a song and plays it, with a metronome over the stems and a
timeline that draws the mix's waveform and the chart's beat lines under a playhead driven by
`SmoothPosition`. That much of [C10](charter-spec-v1.md#c10-playback) (play and pause, speed, pitch
correction, the metronome) is in the timeline's header for now; the transport bar, hit sounds,
following and latency compensation come with step 5.

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

### Where CM2 stands

As of 2026-10-06, for picking the work up again.

**Waiting on the user.** The open and playback slice has been built and its tests pass, but the
apps haven't been tried by hand yet. The checks still to do:

- open a song folder and a `.sng` from **Open chart**, by drop and from **Recent**;
- play, pause and seek, change the speed with **Keep pitch** on and off, and see the metronome's
  clicks land on the beat lines;
- open a song with only `notes.chart`, and a song already open in another Chart Builder;
- in Chart Packer, **Edit chart** on a song with unsaved drafts (Save, Discard, Cancel), and the
  refresh when its window is activated after the song changed on disk;
- Chart Packer's inspector playback at 50%, 100% and 150%, with pitch correction on and off: a
  stretched `Position` now follows the grains played, so it can be up to about 12 ms from before.

**Where the code is.**

| Piece | Where |
| ----- | ----- |
| Grains played, the post-stretch sounds, `SmoothPosition` | `ChartPacker.Audio/Dsp/TimeStretcher.cs` (`PlayedPosition`, `FramesToNextGrain`), `Playback/StemPlayer.cs` (`Sounds`, `SoundVolume`, `SmoothPosition`), `Playback/PositionMap.cs` (`OutputAt`, 256 segments), `Playback/SoundSchedule.cs` (`SynthSound`, `ScheduledSound`, `ISoundSchedule`) |
| `Synth`, the metronome | `ChartPacker.Audio/Synthesis/` (`Synth`, `SynthHit`, `MetronomeSchedule`). Not `Synth/`: a namespace named like its class breaks lookups from beside it. |
| Opening a song | `ChartPacker.Core/Charting/SongOpener.cs` (`EntryOf`, `OpenAsync`, `OpenedSong`), `Scanning/SongLock.cs`, `Launching/RecentSongs.cs`, `UserConfigPaths.LockFolder` |
| Chart Builder | `ViewModels/MainWindowViewModel.cs` (open flow, import prompt, Recent), `ViewModels/PlaybackViewModel.cs` (player, peaks, beats, metronome), `Controls/TimelineView.cs` (replaced `TimelinePlaceholder`), `Services/BuilderWindows.cs` (`OpenSong`), `App.axaml.cs` (`SdlAudio`) |
| Chart Packer | `ViewModels/LibraryViewModel.cs` (`EditChartAsync`, `PrepareEditChart`, `RefreshEditedSongsAsync`), `ViewModels/MainWindowViewModel.cs` (`PrepareEditChartAsync`, `OnActivatedAsync`), `Services/ChartBuilderSongs.cs` (the lock prompt), `Views/MainWindow.axaml.cs` (`Activated`) |
| Shared UI | `ConfirmDialog`, `IConfirmationService` and its `ChooseAsync` (two actions and Cancel) moved to `ChartPacker.UI`; `IFilePickerService.PickSongAsync` |
| Tests | `ChartPacker.Core.Tests/Audio/` (`TimeStretcherTests`, `PositionMapTests`, `StemPlayerTests`, `SynthTests`), `Scanning/SongLockTests.cs`, `Launching/RecentSongsTests.cs`; `ChartBuilder.Tests/Charting/SongOpenerTests.cs` |

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

**Loose ends.**

- A merge that overwrites an existing `.sng` doesn't ask when Chart Builder has it open
  (`MergeWindowViewModel` has no `IConfirmationService`).
- The import's `Offset` is used as the delay but isn't yet a pending `delay` change, and the
  `.chart`'s `[Song]` values aren't read (C2 item 3, with C17's pending changes).
- The chart's load issues are only counted in the Editor's notice. They become findings with the
  findings panel.
- The Chart Inspector is still a placeholder; the Editor's title is the only sign of the song.
- The timeline's transport is a stopgap in its header; the spec's transport bar comes with C3.
- The spec's CM0 row still names the wizard (CM0's cleanup).

**Next: calibration (the rest of step 2).** In order:

1. **The output device.** `IAudioOutput` gains `DeviceName`, an optional `DeviceId` and
   `DeviceChanged`. `SdlAudioOutput` checks the device on SDL's audio device events and when
   playback starts ([C11](charter-spec-v1.md#c11-calibration-tool) item 3). Getting a stable ID
   may need a platform call beside SDL; without one, the name is used.
2. **`audio-latency.json`.** Two maps (device ID → ms, device name → ms) and a fallback, looked up
   in that order, with a name hit also saved under the ID
   ([§10](charter-spec-v1.md#10-settings)). In Core, with an explicit path and tests; written by
   temp file and rename. `UserConfigPaths` gets its path.
3. **The tap-test estimator**, in Core or Audio, with AC C11.2's seeded test (1,000 trials).
4. **`CalibrationWindow`** in Chart Builder, opened by `--calibrate`, Settings and (later) the
   transport. The beep is a `Synth.Beep` scheduled through `StemPlayer.Sounds` on a silent mix; the
   flash and note follow the monotonic clock; `PositionMap.OutputAt` and `SmoothPosition` place each
   beep (C11 item 1, ACs C11.1, C11.4 and C11.5).
5. **Applying it.** `PlaybackViewModel.Clock` becomes `SmoothPosition − latency × speed`
   ([C10](charter-spec-v1.md#c10-playback) item 5, AC C10.2). Chart Packer reads the file for its
   inspector and on activation, and its Settings gets a **Calibrate…** action
   (`SettingAction` in `AppSettingsCatalog`, running `BuilderArgs.ForCalibrate()`).

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
