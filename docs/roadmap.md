# Chart Builder — Roadmap

| | |
| --- | --- |
| **Status** | Current as of 2026-10-06 |
| **Companion docs** | [charter-spec-v1.md](charter-spec-v1.md) (what each milestone contains, and its exit criteria) |
| **Next up** | [CM1: Model, writer and lint engine](#cm1-model-writer-and-lint-engine) |

---

## Purpose

This file records the order in which Chart Builder's milestones are built, and where each one
stands. The spec's [§13](charter-spec-v1.md#13-milestones) owns what each milestone contains and
its exit criteria. This file owns only the order and the status.

## At a glance

| Order | Milestone | Status | Depends on |
| ----: | --------- | ------ | ---------- |
| 1 | [CM0: Split](#cm0-split) | Done (cleanup left) | — |
| 2 | [CM1: Model, writer and lint engine](#cm1-model-writer-and-lint-engine) | **Next** | CM0 |
| 3 | [CM2: Calibration, chart settings and 5-fret editing](#cm2-calibration-chart-settings-and-5-fret-editing) | Not started | CM1 |
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
([C1](charter-spec-v1.md#c1-new-chart-from-scratch)), and the Editor's shell already exists:

- the toolbar;
- the collapsible side panels;
- lane docking (`ChartPacker.Core/Charting/Layout/LaneDockLayout`);
- a placeholder timeline.

This counts towards [C3](charter-spec-v1.md#c3-editor-layout). The canvases, the Chart Inspector,
the Properties panel and the tools are still placeholders.

**Cleanup:**

- **The wizard's back end** (`ChartPacker.Core/Charting/NewChart*.cs`, with its tests) is used
  only by tests now. Keep it as the reference for Chart settings until that spec exists
  ([CM2](#cm2-calibration-chart-settings-and-5-fret-editing)), then remove it or reuse it.
- **Shared dialogs.** `ConfirmDialog` and `MessageDialog` are still in `ChartPacker/Views`. They
  move to `ChartPacker.UI` when Chart Builder first needs them.
- **`ViewLocator`.** Chart Builder doesn't have one yet. It arrives with `ChartEditorViewModel`
  ([§4.5](charter-spec-v1.md#45-app-chartbuilder)).
- **The spec's CM0 row** still names the wizard, and should be restated.

## CM1: Model, writer and lint engine

**Status: next.**

**Why it comes first.** Every editing feature works on the editable model, and every save goes
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
  ([§4.6](charter-spec-v1.md#46-saving)). Today it deletes first.

**Reused, not rewritten.** These existing Core internals carry over:

- `MidiFile`/`MidiEvent`, `MidiLayout` and `MidiChartReader`, for reading;
- `MidiWriter`/`MidiMessage`, for writing;
- `NoteBuilder`, for natural HOPOs;
- `NoteCheck` and `ChartMergeWarning`, for the comparison;
- `TempoMap`, for tick ↔ seconds;
- `ChartToMidi`, for `.chart` import.

## CM2: Calibration, chart settings and 5-fret editing

**Status: not started.** It depends on CM1's model, writer and lint engine.

This is the first milestone with a working editor. Its steps, in order:

1. **Specify Chart settings.** It replaces the removed wizard
   ([C1](charter-spec-v1.md#c1-new-chart-from-scratch)), and restates C1's acceptance criteria.
   It is a document, so it can be written while CM1 is being built. It blocks steps 3 and 4.
2. **Playback and calibration.**
   - The playback changes ([§4.4.1](charter-spec-v1.md#441-playback-changes)): the post-stretch
     mix, the true source position and `SmoothPosition`.
   - `Synth`.
   - [C11](charter-spec-v1.md#c11-calibration-tool) calibration, with the shared latency file.

   Calibration comes first because every timing judgement after it depends on it.
3. **Open and import** ([C2](charter-spec-v1.md#c2-open-and-import)).
   - `SongLock` and Chart Packer's refresh
     ([§4.8](charter-spec-v1.md#48-launching-and-coordination)).
   - **Edit chart** turned on (`CanOpenSongs`).
   - The launcher's **Open chart** and recent songs.
4. **New chart through Chart settings** ([C1](charter-spec-v1.md#c1-new-chart-from-scratch)),
   without **Detect from audio**.
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
