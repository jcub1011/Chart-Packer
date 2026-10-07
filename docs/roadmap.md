# Chart Builder — Roadmap

| | |
| --- | --- |
| **Status** | Current as of 2026-10-07 |
| **Companion docs** | [charter-spec-v1.md](charter-spec-v1.md) (what each milestone contains, and its exit criteria) |
| **Next up** | [CM3: Drums and GHL](#cm3-drums-and-ghl) |

---

## Purpose

This file records the order in which Chart Builder's milestones are built, and where each one
stands. The spec's [§13](charter-spec-v1.md#13-milestones) owns what each milestone contains and
its exit criteria. This file owns only the order and the status.

## At a glance

| Order | Milestone | Status | Progress | Depends on |
| ----: | --------- | ------ | -------- | ---------- |
| 1 | [CM0: Split](#cm0-split) | Done | Exit criteria met | — |
| 2 | [CM1: Model, writer and lint engine](#cm1-model-writer-and-lint-engine) | Done | Exit criteria met | CM0 |
| 3 | [CM2: Calibration, chart settings and 5-fret editing](#cm2-calibration-chart-settings-and-5-fret-editing) | Done | Exit criteria met | CM1 |
| 4 | [CM3: Drums and GHL](#cm3-drums-and-ghl) | In progress | Core commands | CM2 |
| 5 | [CM4: Audio assist](#cm4-audio-assist) | Not started | — | CM2 |
| 6 | [CM5: Authoring tools](#cm5-authoring-tools) | Not started | — | CM3, CM4 |

---

## CM0: Split

**Status: done.**

Both apps build from one solution, share `ChartPacker.UI`, and publish and install together.
Chart Builder has its launcher, `BuilderArgs`, `BuilderSettings` and `JsonSettingsFile<T>`. Chart
Packer's **Edit chart** is in place, and was turned on in CM2.

**No wizard.** New chart opens the Editor straight away
([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)), and the wizard and its back end are
gone. A new chart's first save writes its folder with `NewChartCreator.CreateAsync(NewSongFolder)`.

**Left for when it's needed:**

- **`MessageDialog`** is still in `ChartPacker/Views`. It moves to `ChartPacker.UI` when Chart
  Builder first needs it, as `ConfirmDialog` did.
- **`ViewLocator`.** Chart Builder has none. Its lanes and canvases are built in code, so nothing
  needs one yet ([§4.5](charter-spec-v1.md#45-app-chartbuilder)).

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
  it back both ways (`ChartReadBack`).
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

**Status: done.**

This is the first milestone with a working editor. It builds on CM1's model, writer and lint
engine. Its design decisions are recorded in the spec, as *Settled in CM2* and *Restated in CM2*.

**What was built:**

- **Playback and calibration.** The post-stretch mix, the grains played and `SmoothPosition`
  ([§4.4.1](charter-spec-v1.md#441-playback-changes)); `Synth`, the metronome and hit sounds;
  [C11](charter-spec-v1.md#c11-calibration-tool) calibration with the shared latency file, the
  output's device, the tap test and the first-use prompt, with the latency applied in both apps.
- **Open and import** ([C2](charter-spec-v1.md#c2-open-and-import)). `SongLock`, Chart Packer's
  **Edit chart** and its refresh when activated, and its prompt before writing to a song Chart
  Builder has open (a merge that overwrites its `.sng` included). The launcher's **Open chart**,
  recent and dropped songs, and `.chart` import with its `Offset` and `[Song]` values as pending
  changes.
- **New chart and Chart settings** ([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)).
  New chart opens the Editor straight away; Chart settings edits the timing and parts as undoable
  commands, with the resolution change and its warning.
- **The editor.** [C3](charter-spec-v1.md#c3-editor-layout) highway and timeline lanes, the
  overview, Properties; [C4](charter-spec-v1.md#c4-tempo-map-and-grid) the tempo map, anchors,
  snap and tap tempo; [C5](charter-spec-v1.md#c5-5-fret-notes) 5-fret notes;
  [C10](charter-spec-v1.md#c10-playback) the transport bar, regions, the loop and following. The
  highway's drawing moved to `ChartPacker.UI`.
- **Saving** ([C17](charter-spec-v1.md#c17-save-and-export)): Save, a new chart's first save,
  **New chart in this song**, Save and pack, Export copy, and the pending changes with Revert and
  Restore. [C15](charter-spec-v1.md#c15-editing-ergonomics) autosave and recovery.
- **Lint.** The timing, 5-fret and song rules ([§6](charter-spec-v1.md#lint-rules)) with their
  fixes, the Findings tab and the marks on the bottom timeline. The chart's load issues are listed
  there too, as CHR086.

**Left for later:**

- **Phrases and drums in the editor** (CM3): the Phrase tool, solos and the `S` key; cymbal and
  tom (`C`), accents and ghosts (`A`) and the 2× kick (Shift+0); GHL's stacked columns, as GHL
  lanes are six columns for now.
- **Record mode**, which shifts key presses by the latency
  ([C15](charter-spec-v1.md#c15-editing-ergonomics)), with CM5.
- **Ratings.** CHR083 can't be cleared in the editor until a part can be rated (the Chart
  Inspector, or C18's suggested rating); CHR080 has no fix until C9's reduction.
- **The Chart Inspector** is still a placeholder. A new chart's name and artist are asked for at
  its first save.
- **Device IDs on macOS and Linux.** A Core Audio device UID and a PulseAudio or PipeWire sink
  name would make those platforms' keys survive renames. They need testing on those platforms.
- **Space as a tap** in the tap test, should a later Avalonia make raw input timestamps public.

**Contents:** see [§13](charter-spec-v1.md#13-milestones).

## CM3: Drums and GHL

**Status: in progress.**

It is built in six phases, each reviewed before the next:

1. **Core commands:** cymbal and tom, 2× kick, disco flip, the 5-lane conversion, text event changes, and
   keeping drum fills and big rock endings as `[coda]` makes them.
2. **Lint:** the drums and phrase rules, with their fixes.
3. **Drums in the editor:** the `C`, `A`, `Shift`+`A` and `Shift`+`0` keys, accents and ghosts, drum
   Properties, and the variant change with its conversion report.
4. **GHL's stacked columns.**
5. **Phrases and events:** the Phrase tool, the `S` key, phrase bands, and the Sections tab.
6. **Generate fills.**

Its design decisions are recorded in the spec, as *Settled in CM3*.

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

When a milestone's work starts, mark it **In progress** here and in the table at the top. When its
exit criteria pass, mark it **Done**, with a short summary of what was built and what was left for
later.
