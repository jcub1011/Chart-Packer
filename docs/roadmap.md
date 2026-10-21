# Chart Builder — Roadmap

| | |
| --- | --- |
| **Status** | Current as of 2026-10-20 |
| **Companion docs** | [charter-spec-v1.md](charter-spec-v1.md) (what each milestone contains, and its exit criteria) |
| **Next up** | v1 is built: the manual checks of CM5, then the release |

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
| 4 | [CM3: Drums and GHL](#cm3-drums-and-ghl) | Done | Exit criteria met | CM2 |
| 5 | [CM4: Audio assist](#cm4-audio-assist) | Done | Exit criteria met | CM2 |
| 6 | [CM5: Authoring tools](#cm5-authoring-tools) | Done | Exit criteria met by tests; manual checks to do | CM3, CM4 |

---

## CM0: Split

**Status: done.**

Both apps build from one solution, share `ChartShared.UI`, and publish and install together.
Chart Builder has its launcher, `BuilderArgs`, `BuilderSettings` and `JsonSettingsFile<T>`. Chart
Packer's **Edit chart** is in place, and was turned on in CM2.

**No wizard.** New chart opens the Editor straight away
([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)), and the wizard and its back end are
gone. A new chart's first save writes its folder with `NewChartCreator.CreateAsync(NewSongFolder)`.

**Left for when it's needed:**

- **`MessageDialog`** is still in `ChartPacker/Views`. It moves to `ChartShared.UI` when Chart
  Builder first needs it, as `ConfirmDialog` did.
- **`ViewLocator`.** Chart Builder has none. Its lanes and canvases are built in code, so nothing
  needs one yet ([§4.5](charter-spec-v1.md#45-app-chartbuilder)).

## CM1: Model, writer and lint engine

**Status: done.**

Most of it is in `ChartShared.Core/Charting/`, with its tests in the new `ChartBuilder.Tests`.

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
- **Logging.** Both apps log through Serilog (`ChartShared.UI/AppLog.cs`), with a Logging setting
  that opens the log folder.

**Left for later:**

- **The CHR lint rules.** Done: the timing, 5-fret and song rules came with CM2, and the drums and
  phrase rules with CM3.
- **Cymbal/tom toggling and drum conversion.** Done in CM3.
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
  highway's drawing moved to `ChartShared.UI`.
- **Saving** ([C17](charter-spec-v1.md#c17-save-and-export)): Save, a new chart's first save,
  **New chart in this song**, Save and pack, Export copy, and the pending changes with Revert and
  Restore. [C15](charter-spec-v1.md#c15-editing-ergonomics) autosave and recovery.
- **Lint.** The timing, 5-fret and song rules ([§6](charter-spec-v1.md#lint-rules)) with their
  fixes, the Findings tab and the marks on the bottom timeline. The chart's load issues are listed
  there too, as CHR086.

**Left for later:**

- **Phrases and drums in the editor.** Done in CM3: the Phrase tool, solos and the `S` key; cymbal
  and tom (`C`), accents and ghosts (`A`) and the 2× kick (`Shift`+`0`); GHL's stacked columns.
- **Record mode**, which shifts key presses by the latency
  ([C15](charter-spec-v1.md#c15-editing-ergonomics)). Done in CM5.
- **Ratings.** CHR083 can't be cleared in the editor until a part can be rated (the Chart
  Inspector, or C18's suggested rating); CHR080 has no fix until C9's reduction. Done since: the
  Chart Inspector rates parts.
- **The Chart Inspector** was a placeholder. Done since: it edits the song's metadata, ratings,
  delay and album art, and lists its tracks and files, sharing Chart Packer's metadata section
  (`MetadataEditorViewModel` in `ChartShared.UI`). Adding, replacing and removing tracks and files
  there is still to do. A new chart's name and artist are set in it, and its first save asks only
  where the folder goes.
- **Device IDs on macOS and Linux.** A Core Audio device UID and a PulseAudio or PipeWire sink
  name would make those platforms' keys survive renames. They need testing on those platforms.
- **Space as a tap** in the tap test, should a later Avalonia make raw input timestamps public.

**Contents:** see [§13](charter-spec-v1.md#13-milestones).

## CM3: Drums and GHL

**Status: done.**

The editor now edits every v1 instrument family, with phrases and events. It was built in six
phases, each reviewed before the next, and its design decisions are recorded in the spec, as
*Settled in CM3*.

**What was built:**

- **Drums** ([C6](charter-spec-v1.md#c6-drums-4-lane-pro-and-5-lane)). Cymbal and tom (`C`), which
  keeps the tom markers for every difficulty; accents and ghosts (`A`, `Shift`+`A`), drawn as the
  games show them and saved with `[ENABLE_CHART_DYNAMICS]` when needed; the 2× kick (`Shift`+`0`);
  drum fields in Properties. The variant change in Chart settings, with the 4-lane ↔ 5-lane
  conversion (`ConvertDrums`) and its report in the Messages dialog.
- **Disco flip** (C6 item 7). `GameDiscoFlips` decides the flip once for the reader, the game view
  and the editor. Notes inside a range are drawn and edited on the pads the player hits, and ranges
  are drawn, moved and resized in the phrase rail.
- **GHL** ([C7](charter-spec-v1.md#c7-6-fret-ghl-notes)). Three stacked columns in both views, the
  black fret in each column's first half, barre chords drawn as in the game.
- **Phrases** ([C8](charter-spec-v1.md#c8-phrases-and-events) items 1, 2 and 4, C6 items 5–6). The
  Phrase tool and its kind picker, the phrase rail beside each lane (select, move, resize, delete),
  the star power and solo tints with the solo note count, the `S` key, and phrases in Properties.
  Fills and big rock endings follow `[coda]`.
- **Events** (C8 items 3, 5 and 6). The Sections tab, with every `EVENTS` text event and the focused
  part's own; **Insert section** and **Insert event**; `Ctrl`+`↑`/`↓` between sections; section
  labels on the lanes; `MeasureBeat` for measure and beat without walking the beat lines.
- **Generate fills** (C6 item 5). Proposals on measure downbeats that end on a crash (or any note)
  every charted difficulty has, after the second star power phrase and never inside one; kept or
  dropped as ghosts, and added as one undoable step.
- **Lint.** The drums and phrase rules ([§6](charter-spec-v1.md#lint-rules)) with their fixes.

**Left for later:**

- **Disco flip.** Properties' Lane field names the stored pad, not the pad drawn; the lanes have no
  tint behind a range, only its bar in the rail.
- **Events** move by their Tick field in the Sections tab, not by dragging on the lanes. Song events
  can only be moved or deleted. The tab's row matching has no unit tests, as the tests reach only
  Core.
- **GHL.** No key switches a note between its column's white and black frets; a drag into the other
  half does.
- **Phrases.** Resizing a bar's end resizes only that bar, even with several selected.
- **Generate fills.** Its options last only while the editor is open, and the fills added aren't
  selected.

**Contents:** see [§13](charter-spec-v1.md#13-milestones).

## CM4: Audio assist

**Status: done.**

AC C12.1 was checked by hand; AC C13.1, C13.2 and C14.1 to C14.3 have automated tests. Its design
decisions are recorded in the spec, as *Settled in CM4*.

**What was built:**

- **Analysis** (`ChartShared.Audio/Analysis/`). Audio read at its full rate, a stem or several
  mixed (`AnalysisInput`), on threads below normal priority that take the most wanted chunk next
  (`AnalysisWorkers`), with two frames to an FFT (`RealFftPair`). The plain data and beat arithmetic
  the editor uses are in Core (`Charting/Analysis/`: `BeatProposal`, `DetectedOnset`, `BeatMath`).
- **Waveform** ([C12](charter-spec-v1.md#c12-waveform)). Each lane's audio (the part's own stems,
  the mix or any stem), behind or beside, in mono or stereo, with a gain; read once at 5 ms and
  drawn at any zoom (`StemWaveforms`), within a frame budget (`PaintBudget`). Peaks by channel
  (`StemPeaksReader`'s `channels`). The options are in a flyout in each lane's header, kept with the
  lane layout (`LaneAudioOptions`).
- **Spectrogram** ([C13](charter-spec-v1.md#c13-spectrogram)). `SpectrogramBuilder` computes it
  from the view outwards; `SpectrogramCache` holds it within the memory budget; `SpectrogramPainter`
  draws it, with the band presets and contrast. `W` switches a lane; Settings has **Lanes show** and
  the budget.
- **Onsets** ([C14](charter-spec-v1.md#c14-onset-and-beat-assist) items 1–3). `OnsetDetector`, by
  band for drums, with a live sensitivity; ticks beside the lanes; snap to onset (`O`) and Nudge to
  onsets (`Shift`+`O`, `OnsetNudge`, `MoveNotes` by each note's own ticks).
- **Proposed tempo map** (C14 item 4). `TempoEstimator`: a tempogram, beats tracked along it, each
  moved onto its onset, and stretches of steady tempo with a confidence. The editor shows it as a
  ghost to adjust, double, halve and accept as one step (`TempoProposals`, `ProposedTempoMap`).
- **First downbeat** (C14 item 5). `TempoEstimator.FindFirstDownbeat`, applied as a lead-in
  (`TempoMapEdits.LeadIn`) or as a delay, a pending change (`EditorSong.DelayMs`) kept by autosave.
- **Detect from audio** ([C1](charter-spec-v1.md#c1-new-chart-and-chart-settings)), in Chart
  settings: the starting BPM from the mix, tidied (`BpmTidy`), and the proposal to review.

**Left for later:**

- **One STFT.** Onsets and the spectrogram each run their own, as they need different windows;
  sharing work between them, or a vectorised FFT, would make the first analysis of a song quicker.
- **A proposal's beats** are re-spaced evenly when one is dragged, rather than estimated again
  round it.
- **Snap to onset** applies to the mouse only; record mode (CM5) will use it for key presses. Done in
  CM5.
- **The bottom timeline** shows the mix's waveform, not the lane's audio, and not the proposal.
- **Tests of the view models** that draw and analyse (`StemWaveforms`, `AudioAnalysis`), which need
  the UI thread.

## CM5: Authoring tools

**Status: done.**

AC C9.1, C15.1 to C15.3, C16.1 and C16.2, and C18.1 have automated tests. The UI was built in eight
phases, each tested before the next, and its manual checks are still to do. Its design decisions are
recorded in the spec, as *Settled in CM5*.

**What was built:**

- **Copy difficulty and Reduce** ([C9](charter-spec-v1.md#c9-difficulty-authoring)). `CopyDifficulty`,
  and `Reducer` with the rules of `ReductionRules`: thinning by strong beats, chords kept to their
  outer columns and moved into range, forcing and short sustains, drums' pads, kick and 2× kick. A
  run (`ReduceProposals`) is drawn as a ghost over the target from the Reduce dialog and added as
  one `ReplaceTrack`. The defaults are in Settings (`BuilderSettings.ReductionRules`). Improved
  after v1; see [Improved Reduce](#improved-reduce-90).
- **Selection across lanes** ([C15](charter-spec-v1.md#c15-editing-ergonomics) item 2). A range over
  the lanes shown (Alt+drag), a section (Ctrl+Shift+A), a whole lane; delete, moves, H/G/T and the
  transforms act on every track a range covers (`EditorSelection.NotesByTrack`).
- **The clipboard** (C15 item 3). `ChartClip` and `ClipboardEdits`: passages in ticks, lanes as
  shown, overwrite or merge (`NoteMerge`, `ReplaceNotes`), 5-fret ↔ GHL through a lane mapping, drums
  only into the same variant.
- **Transforms** (C15 item 4). Lefty flip, shift lanes, sustains, and stretching the timing between
  two points (`TempoMapEdits.Stretch`, `TickMaps.Stretch`), which re-ticks the passthrough tracks.
- **Record mode** (C15 item 5). `RecordPass`: presses timed by the playback clock when handled,
  quantised to the grid or onsets, held for sustains, drawn as a ghost and added as one step;
  Merge or Overwrite on the transport bar.
- **Lint in the library** ([C16](charter-spec-v1.md#c16-chart-lint) item 5). `SongChartLint` and its
  session cache, on the Validator's workers; a song's chart findings in the Validator and the detail
  panel. Packing is never blocked. `SongOpener.LoadChart` reads a chart the same way in both apps.
- **Preview** ([C18](charter-spec-v1.md#c18-preview)). `NoteHighwayView` moved to `ChartShared.UI`;
  the Preview tab and window, practice over the loop, and the suggested rating
  (`DifficultyEstimator`), used through a pending change (`EditorSong.SetRating`) or CHR083's fix.
- **Difficulty analysis** (#108, [difficulty-model.md](difficulty-model.md)). Five-fret parts are
  rated by `DifficultyAnalyzer`, an online strain model. It has strum, note, rhythm, pattern and
  endurance attributes, window-peak scores, and ranks 0 to 6 with overcharts at 7 and up. The
  Difficulty tab and window show the rank, a spider graph (chart, selection or playhead) and the
  difficulty over the song.

**Left for later:**

- **Record mode** times a press when its key event is handled, as Avalonia's key events have no
  timestamp; a Win32 hook could take the time the key was pressed.
- **The library** has no fix for chart findings (CHR084's could be one), can't open Chart Builder at
  a finding (`BuilderArgs --at`), and some messages (CHR045, CHR085, CHR086) talk about saving, which
  fits Chart Builder better.
- **The clipboard** doesn't copy phrases or disco flip sections; a stretch always changes the
  tempos, never only the ticks.
- **Sections tab.** Select section is on Ctrl+Shift+A and the lane's Edit menu, not on the tab's rows.
- **The suggested rating's weights** are set by hand, to be fitted to rated charts (spec §14). So are
  the difficulty model's parameters, reference patterns and rank thresholds.
- **The difficulty analysis** reads the whole track again after an edit, rather than from a
  snapshot before it (difficulty-model.md §8). Drums and six-fret aren't analyzed.
- **The preview** rebuilds the whole track after an edit, not only the range it touched.

It covered:

- [C9](charter-spec-v1.md#c9-difficulty-authoring), reduction;
- [C15](charter-spec-v1.md#c15-editing-ergonomics) record mode, the clipboard and transforms;
- [C16](charter-spec-v1.md#c16-chart-lint), lint in Chart Packer's library;
- [C18](charter-spec-v1.md#c18-preview), preview and suggested difficulty.

It comes last because these tools build on the rest:

- reduction needs every instrument family (CM3);
- record mode and snap to onset need the onsets (CM4);
- lint in the library needs the full rule catalogue.

---

## Improved Reduce (#90)

**Status: done.** The fixtures pass; the manual checks of the dialog are to do.

[Issue #90](https://github.com/jcub1011/Chart-Packer/issues/90) improves C9's Reduce
([C9](charter-spec-v1.md#c9-difficulty-authoring), *Improved after v1*). `Reducer` stays a static
class, rules in and a `ReductionResult` out.

- **Reduce all.** `ReduceProposals.StartAll` drafts Hard, Medium and Easy, each from the one above
  it as it will be, on a copy of the chart, and `Accept` adds them as one step
  (`EditorSession.ExecuteAsOne`). A run holds `ReductionDraft`s.
- **The melody's shape.** `Reducer.Phrases` splits the chords at rests and sections, and
  `Reducer.FitPhrase` fits each phrase to the range as a whole, keeping each step's direction.
- **Thinning by time.** `MinGapMs` in both rule sets (100, 200 and 400 ms); the larger of it and the
  grid holds (`ThinningGap`). Settings saved before it get the shipped values. Later removed: thinning
  is by fractions of a beat alone (`ThinningSpacing`, charter-spec C9).
- **A score per chord.** `ThinningWeights`: beat strength, sustains, chords, after a rest, turns of
  the melody, and phrase ends.
- **Star power and solos.** Each keeps its best chord, seeded before thinning. CHR065 flags a phrase
  empty on one charted difficulty.
- **The check.** `ReductionCheck`: the draft's findings, suggested ratings and peak notes per
  second, and difficulties out of order, worked out off the UI thread.
- **A range.** The Reduce dialog's **Over**: the whole part, the selection or the section at the
  cursor.

AC C9.1 still passes; AC C9.2 and C9.3 are new, with fixtures in `ReducerTests` and
`ReduceProposalsTests`.

**Left for later:** the scoring weights and the contour's step costs are set by hand, like the
suggested rating's; an order warning shows when a draft is rated the same as the difficulty above,
which a slow song's Hard can be.

---

## Keeping this current

When a milestone's work starts, mark it **In progress** here and in the table at the top. When its
exit criteria pass, mark it **Done**, with a short summary of what was built and what was left for
later.
