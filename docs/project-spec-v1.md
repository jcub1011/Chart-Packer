# Chart-Packer — Project Specification v1

| | |
|---|---|
| **Status** | Draft |
| **Scope** | Version 1.0 feature set |
| **Format reference** | [sng-spec.md](sng-spec.md). Every format rule below links into it. |

---

## 1. Overview and goals

Chart-Packer is a desktop GUI application for **packing** song folders into `.sng` files,
**unpacking** `.sng` files back into song folders, and **validating** both against the SNG
specification. It is for Clone Hero, YARG and other games that read `.sng`.

Goals:

1. **Friendly.** No command line. Drag and drop works everywhere, and every error explains
   itself and says how to fix it.
2. **Correct.** Chart-Packer never writes a `.sng` that breaks the spec. Where the spec is
   ambiguous, it follows the interpretations in
   [sng-spec.md §10](sng-spec.md#ambiguities).
3. **Transparent.** Before anything is written, the user can see what will go into (or come
   out of) a `.sng`.
4. **Lightweight.** It ships as a Native AOT executable per platform, with no runtime to
   install, a small memory footprint, and a minimal set of dependencies.
5. **Cross-platform.** Windows, macOS and Linux.

## 2. Non-goals for v1

- Audio transcoding (e.g. Opus), image re-encoding (JPEG) and album-art resizing. Files are
  stored exactly as they are. See [§12 Future](#future).
- Parsing or checking the *content* of `notes.chart`, `notes.mid`, audio, image or video files.
  The one exception is reading the `[Song]` section of `notes.chart` as fallback metadata
  (F1.4).
- Editing charts, or managing, searching or scanning a song library beyond what batch
  processing needs.
- Reading pre-version-1 `.sng` files ([sng-spec.md §9.1](sng-spec.md#reference-behaviour)).
- A command-line interface. The Core library is designed so one can be added later.

## 3. Target users and platforms

| | |
|---|---|
| **Users** | Chart authors preparing releases, and players converting or inspecting their libraries |
| **Runtime** | .NET 10 (LTS), published as **Native AOT**: a self-contained native executable with no .NET runtime to install |
| **Platforms / RIDs** | `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64` |
| **UI framework** | [Avalonia](https://avaloniaui.net/) 12.x, Fluent theme, Inter font |
| **Language** | English for v1. All user-facing strings live in `.resx` resources so they can be localised later. |

---

## 4. Architecture

```
Chart-Packer.slnx
├── ChartPacker/              Avalonia desktop app (MVVM)            (exists)
├── ChartPacker.Core/         UI-free library: format, validation, pack/unpack pipelines
└── ChartPacker.Core.Tests/   MSTest tests for Core
```

### 4.1 `ChartPacker.Core`

Core must not reference Avalonia or any UI type. Main components:

| Component | Responsibility |
| --------- | -------------- |
| `SngMask` | The XOR mask ([sng-spec §5](sng-spec.md#masking)). Works on spans, and chunked calls carry a starting file offset. Optionally vectorised. |
| `SngReader` | Opens a `.sng`, parses the header, metadata and file index **without reading the file data**, and gives streaming, unmasking access to each file. Uses `FileStream`/`RandomAccess`, or memory-mapped files for large reads. Supports files over 4 GB. Every structural problem becomes a finding instead of an exception. |
| `SngWriter` | Streams a `.sng` to disk: computes the index from source file sizes, writes the header, metadata and index, then streams each file through the mask in fixed-size chunks. Always writes a temp file and renames it into place. |
| `SongIni` | Reads and writes `song.ini` (`[song]` section, matched without regard to case). Behaves like the reference parser ([sng-spec §9.4](sng-spec.md#reference-behaviour)). |
| `ChartSongSection` | Reads the `[Song]` section of `notes.chart` as fallback metadata ([sng-spec §9.4](sng-spec.md#reference-behaviour)). |
| `KnownKeys` | Known metadata keys and their expected types, from the reference `KnownKeys` table ([sng-spec §6.3](sng-spec.md#metadata-rules)), plus the legacy key map. |
| `FilenameRules` | The filename and path-segment rules ([sng-spec §7](sng-spec.md#filename-rules), [A1](sng-spec.md#a1)) and the registry lookup ([§8](sng-spec.md#filename-registry)). |
| `SngValidator` | Runs the rule catalogue ([§6](#validation-rules)) against a `.sng` or a source folder and returns a `ValidationReport`. |
| `PackPlanner` / `Packer` | Turns a folder into a `PackPlan` (included and excluded files, final names, metadata, findings), then executes the plan. |
| `Unpacker` | Extracts a `.sng` safely into a folder ([A16](sng-spec.md#a16)). |
| `SongFolderScanner` | Finds song folders for batch mode, using the reference heuristic ([sng-spec §9.4](sng-spec.md#reference-behaviour)). |

All long-running Core operations are `async`, take a `CancellationToken`, and report progress
through `IProgress<T>`.

### 4.2 `ChartPacker` (app)

- Avalonia with MVVM using **CommunityToolkit.Mvvm** source generators
  (`[ObservableProperty]`, `[RelayCommand]`).
- Views bind only to view models, using **compiled bindings**.
- Services (settings, dialogs, file pickers, logging) are wired by hand in a single composition
  root. There is no DI container.
- Views are resolved **without reflection**. The Avalonia template's `ViewLocator`, which
  builds view types by name with `Type.GetType`/`Activator.CreateInstance`, must be replaced by
  an explicit view-model-to-view mapping in code before any feature work starts.

### 4.3 Native AOT constraints (required)

| Constraint | How it is enforced |
| ---------- | ------------------ |
| AOT publish | `<PublishAot>true</PublishAot>` on `ChartPacker` (already set in the scaffold) |
| AOT-safe library | `<IsAotCompatible>true</IsAotCompatible>` on `ChartPacker.Core`, which turns on the trim, AOT and single-file analyzers |
| No warnings | Trim and AOT warnings (`IL2xxx`, `IL3xxx`) are **errors** in Release builds |
| Compiled bindings only | `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`. Reflection bindings are not used. |
| Source-generated MVVM | CommunityToolkit.Mvvm generators only. No reflection-based `INotifyPropertyChanged` helpers. |
| Source-generated JSON | Settings and report export use `System.Text.Json` with a `JsonSerializerContext`. Reflection-based serialization is not used. |
| No dynamic code | No `dynamic`, `Assembly.Load`, `Reflection.Emit`, reflection-driven DI or plugins, or `Regex` construction from runtime strings. `[GeneratedRegex]` is allowed. |

**Dependency budget.**

- `ChartPacker.Core` has **zero** third-party packages; it uses only the BCL.
- `ChartPacker` may use `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
  `Avalonia.Fonts.Inter` and `CommunityToolkit.Mvvm`. It may also use
  `AvaloniaUI.DiagnosticsSupport`, but only in **Debug** builds (the scaffold already excludes
  it from other configurations).
- Tests may use MSTest (via `MSTest.Sdk`) and Microsoft.Testing.Platform, opted into for
  `dotnet test` in `global.json`.
- Image preview uses Avalonia's built-in Skia `Bitmap`.
- Any new dependency must be trim- and AOT-compatible, and the reason for it must be added to
  this section.

---

## 5. Features

Every feature lists numbered acceptance criteria (AC). "Findings" are
`ValidationReport` entries ([§6](#validation-rules)).

### F1. Pack (song folder → `.sng`)

**Input:** a song folder, chosen by drag and drop or a folder picker.
**Output:** `<folder name>.sng` in the chosen output folder. The default is the song folder's
parent.

Behaviour:

1. **Scan.** Chart-Packer scans the folder and builds a **Pack Plan**: every file with its
   stored name, size, whether it's included, and the reason for any exclusion.
2. **Name normalisation.** Registered names ([sng-spec §8](sng-spec.md#filename-registry)) are
   lowercased. Unknown names are left as they are ([A10](sng-spec.md#a10)).
3. **Exclusions.**
   - Always excluded:
     - `desktop.ini`, `.DS_Store`, `Thumbs.db`, `ps.dat`, `ch.dat`
     - `__MACOSX/`
     - nested `*.sng`
     - `song.ini`, whose contents become the metadata instead
   - Excluded by setting:
     - subfolders (off by default, [A13](sng-spec.md#a13))
     - unknown files (included by default)
     - video (included by default)
   - Every exclusion shows up in the plan as an info finding (SNG072, SNG073).
4. **Metadata.**
   - Taken from the `[song]` section of `song.ini`.
   - If there is no `song.ini`, taken from the `[Song]` section of `notes.chart`, using the same
     key mapping as the reference tool (SNG070).
   - The user can edit the result in the metadata editor (F5) before packing.
   - Empty values are dropped (SNG061).
5. **Pre-flight validation.** The full rule catalogue runs against the plan. **Errors block
   packing.** Warnings are shown, and the user can go ahead anyway.
6. **Write.**
   - A fresh random 16-byte mask is generated.
   - The file is streamed to `<name>.sng.tmp` and atomically renamed over the target.
   - If the target already exists, the overwrite policy applies (Ask, Overwrite, Skip or
     Rename).
7. **Post-write verify** (setting, on by default). The written file is re-read and validated
   structurally.

Acceptance criteria:

- **AC1.1** Packing the [sng-spec §11](sng-spec.md#worked-example) example inputs with that
  example's mask produces a byte-identical file.
- **AC1.2** Packing then unpacking any valid folder reproduces every included file byte for
  byte and reproduces the `[song]` key/value pairs, apart from dropped empty values.
- **AC1.3** A folder with an Error-level finding can't be packed. The Pack button is disabled
  and the finding is highlighted.
- **AC1.4** A folder containing `Song.OGG` and `desktop.ini` produces a plan with `song.ogg`
  included and `desktop.ini` excluded, with the reason shown.
- **AC1.5** Cancelling mid-write leaves no partial or `.tmp` file, and leaves any existing
  target untouched.
- **AC1.6** Peak memory while packing a 3 GB folder stays roughly constant; it does not grow
  with file size.
- **AC1.7** A `.sng` packed by Chart-Packer loads in the reference `SngCli decode`, which is
  checked in CI.

### F2. Unpack (`.sng` → song folder)

**Input:** one `.sng` file.
**Output:** a folder `<sng name>/` in the chosen output folder. The default is next to the
`.sng`.

Behaviour:

1. The `.sng` is validated structurally first. **Structural errors** (SNG001–SNG013, SNG040)
   stop the unpack. Other findings are shown.
2. `song.ini` is generated from the metadata as a single `[song]` section in `key = value`
   form ([sng-spec §9.5](sng-spec.md#reference-behaviour)).
3. Files are extracted with unmasking done as a stream. Stored folder paths are recreated.
4. **Path safety** ([A16](sng-spec.md#a16)): every segment is checked, and the resolved path
   must stay inside the output folder. A file that fails is skipped (SNG033), and the unpack
   finishes as *Completed with errors*.
5. When a file already exists, the overwrite policy applies (Ask, Overwrite, Skip or Rename).
   If a stored `song.ini` would replace the generated one, the user is warned first (SNG053).

Acceptance criteria:

- **AC2.1** Unpacking the §11 example produces `notes.chart` containing `ABC` and a
  `song.ini` containing `name = Test`.
- **AC2.2** A crafted `.sng` with filename `../evil.txt` or `/etc/x` writes nothing outside the
  output folder and reports SNG033.
- **AC2.3** A `.sng` over 4 GB unpacks correctly, and memory use doesn't grow with file size.
- **AC2.4** Unpacking a `.sng` produced by the reference `SngCli encode` reproduces the source
  files byte for byte. This is covered by golden-file tests.

### F3. Validate

**Input:** a `.sng` file, or a song folder (validated as a would-be pack).
**Output:** a **Validation Report**.

Behaviour:

1. Runs every applicable rule in [§6](#validation-rules).
2. Findings are grouped by severity (**Error**, **Warning**, **Info**). Each finding shows:
   - the rule ID
   - a plain-language message
   - the location (section, byte offset, metadata key or filename)
   - a "Why?" link to the relevant part of `sng-spec.md`
   - a suggested fix where one exists
3. The overall verdict is:
   - **Valid:** no errors or warnings
   - **Valid with warnings**
   - **Invalid:** one or more errors
4. The report can be copied as text or exported to JSON or Markdown.

Acceptance criteria:

- **AC3.1** Every rule in the catalogue has at least one test fixture that triggers it, and
  the valid fixture triggers none.
- **AC3.2** Structural validation of a `.sng` reads only the header, metadata and index plus
  O(1) extra bytes per file. It does not unmask file contents. A 2 GB `.sng` validates in under
  a second on an SSD.
- **AC3.3** A malformed `.sng` (truncated, random bytes, or huge length fields) never crashes
  the app and never allocates memory in proportion to a declared length. It produces findings.

### F4. Batch

**Input:** a library root folder (for packing) or a folder of `.sng` files (for unpacking).
**Output:** the files, arranged to mirror the input's folder structure under the output root,
plus a summary.

Behaviour:

1. **Discovery.**
   - Pack mode finds song folders with the reference heuristic
     ([sng-spec §9.4](sng-spec.md#reference-behaviour)): a `.chart` or `.mid`, **and** an audio
     file, **and** either a `song.ini` or a `.chart`.
   - Unpack mode finds `*.sng` recursively.
2. **Review list.** Each item shows its path, its pre-flight status (Ready, Warnings or
   Errors) and a checkbox. Items with errors are unchecked by default.
3. **Run options:**
   - parallelism, from 1 to the logical core count. The default is half the cores, and at
     least 1.
   - *Skip existing outputs*
   - *Stop on first error* (off by default)
4. **Progress.** An overall progress bar, a status for each item, elapsed and estimated time
   remaining, and **Cancel**. Cancelling finishes or rolls back the items in flight and leaves
   no partial outputs.
5. **Summary.**
   - counts of succeeded, succeeded with warnings, failed and skipped items
   - a table of results you can sort
   - export to CSV or JSON
   - "Open output folder"

Acceptance criteria:

- **AC4.1** A 500-song library can be packed without the UI freezing. The UI thread is never
  blocked for more than 100 ms.
- **AC4.2** One corrupt song doesn't stop the batch. It shows up as *Failed* with its findings.
- **AC4.3** With *Skip existing* on, re-running a finished batch does no writes.

### F5. Metadata editor

**Input:** the metadata of a Pack Plan, or of an existing `.sng`. In the library, the metadata
of one or more selected `.sng` files and song folders (whose metadata is their `song.ini`).

Behaviour:

1. A grid of key/value rows with add, remove and reorder, plus a raw text (INI-style) view that
   stays in sync with the grid.
   - **Implemented so far:** click a value to edit it, and add and remove rows. Still to come:
     reorder, the raw view, and items 2 and 4.
   - **Bulk editing.** With several items selected, the grid shows the union of their keys,
     compared ignoring case. A row whose value differs between the items, or that some of them
     lack, shows `<<contains distinct values>>`.
     - Editing a row sets that key on every selected item, adding it where it's missing.
     - Removing a row removes the key from every item that has it.
     - Nothing else changes: values that differ in other rows are kept.
   - **Drafts.** Edits are held in memory for each item, and survive a change of selection,
     until **Save** writes the selected items. **Discard** throws them away. Closing the app with
     drafts asks first.
     - Edited rows show `*`, added rows `+`, and removed rows are struck through. Each has a
       revert button.
     - The library list marks items that have drafts.
   - **Saving writes only the edited keys.** A draft records changes per key, and Save re-reads
     each file and applies them to what's on disk.
   - **Saving a song folder edits its `song.ini` in place.**
     - Comments, other sections, unedited lines, the encoding and the line endings are kept.
     - New keys go at the end of `[song]`.
     - A folder without a `song.ini` gets one.
   - Time values are edited in milliseconds. Unset difficulties (`diff_* = -1`) stay hidden, and
     adding one edits its stored value.
2. **Known keys** ([sng-spec §6.3](sng-spec.md#metadata-rules)):
   - a friendly label, the expected type, and a matching editor (a number box for integers, a
     checkbox for booleans, text otherwise)
   - common keys pinned at the top: `name`, `artist`, `album`, `genre`, `year`, `charter`,
     `song_length`, `preview_start_time`, `delay`, `diff_*`
3. **Live validation** as the user types:
   - forbidden characters (SNG020, SNG021)
   - duplicate or empty keys (SNG022, SNG023)
   - type mismatch for known keys (SNG024)
   - non-canonical booleans (SNG060)
4. **Quick fixes:**
   - Normalise booleans to `True`/`False`
   - Remove forbidden characters
   - Rename legacy keys (SNG025)
5. **Saving to an existing `.sng`** rewrites the file with the new metadata:
   - The existing mask is kept. Masking depends only on each byte's position within its own
     file ([sng-spec §5](sng-spec.md#masking)), so the masked file data is **copied without
     unmasking**.
   - Only `contentsIndex` values change.
   - The rewrite is atomic (temp file, then rename).

Acceptance criteria:

- **AC5.1** Editing `name` in an existing `.sng` and saving produces a file whose file
  contents, after unmasking, are byte-identical to the original's, and that passes validation.
- **AC5.2** A key containing `=` can't be saved; the finding is shown inline on that row.
- **AC5.3** The grid and raw views always agree after any edit.

### F6. SNG inspector

**Input:** one `.sng` file.

Behaviour:

1. **Header panel:** identifier, version, and the mask as hex.
2. **Metadata panel:** a read-only table with an **Edit** button that opens F5.
3. **Files panel:** name, size, `contentsIndex`, whether the name is registered or unknown,
   and a per-row status icon. The panel can be sorted and filtered.
4. **Actions:**
   - *Extract selected…* (the same safety rules as F2)
   - *Extract all* (runs F2)
   - *Copy details*
5. **Preview:** album, background and highway images are shown in the app. Other files show
   their size and a hex view of the first bytes.
6. **Findings panel:** the F3 report inline, with each finding linked to the row or field it
   refers to.
7. **Layout view:** a bar showing the section and file byte ranges to scale, so gaps and
   overlaps are easy to see (SNG041, SNG040).

Acceptance criteria:

- **AC6.1** Opening a 2 GB `.sng` shows the header, metadata and file list in under 500 ms on
  an SSD. File data is not read until it's needed.
- **AC6.2** Extracting a single file produces bytes identical to what a full unpack gives for
  that file.

---

<a id="validation-rules"></a>
## 6. Validation rule catalogue

Severity meanings:

- **Error:** breaks the spec or can't be read safely. Blocks packing and marks a `.sng` Invalid.
- **Warning:** allowed by the spec, but likely to cause problems or differs from the
  reference tool's behaviour.
- **Info:** for the user's awareness only.

Scope: **S** means the rule applies to an existing `.sng`, **F** means it applies to a source
folder or Pack Plan.

### Structure

| ID | Sev. | Scope | Rule | Reference |
|----|------|-------|------|-----------|
| SNG001 | Error | S | The first 6 bytes aren't `SNGPKG` | [§4.1](sng-spec.md#sec-header) |
| SNG002 | Error | S | `version` isn't `1` | [A2](sng-spec.md#a2) |
| SNG003 | Error | S | A section length, string length or file range goes past the end of the file (truncated file) | [§3](sng-spec.md#file-layout) |
| SNG010 | Error | S | `metadataLen` or `fileMetaLen` doesn't equal the bytes the entries actually use | [A3](sng-spec.md#a3) |
| SNG011 | Error | S | A negative `keyLen` or `valueLen` | [§4.2](sng-spec.md#sec-metadatapair) |
| SNG012 | Error | S | `metadataCount` or `fileCount` can't fit in the declared section length | [A3](sng-spec.md#a3) |
| SNG013 | Error | S F | Invalid UTF-8, or a `0x00` byte, in a key, value or filename | [A11](sng-spec.md#a11) |

### Metadata

| ID | Sev. | Scope | Rule | Reference |
|----|------|-------|------|-----------|
| SNG020 | Error | S F | A key contains `;`, `\r`, `\n` or `=` | [§6.1](sng-spec.md#metadata-rules) |
| SNG021 | Error | S F | A value contains `;`, `\r` or `\n` | [§6.1](sng-spec.md#metadata-rules) |
| SNG022 | Error | S F | A duplicate key, compared ignoring case | [A7](sng-spec.md#a7) |
| SNG023 | Error | S F | An empty key | [A9](sng-spec.md#a9) |
| SNG024 | Warning | S F | A known key's value doesn't parse as its expected type (e.g. `diff_guitar = hard`) | [§6.3](sng-spec.md#metadata-rules) |
| SNG025 | Info | S F | A legacy key (`frets`, `track`, `star_power_note`, `pro_drum`). The replacement key is suggested. | [§9.4](sng-spec.md#reference-behaviour) |
| SNG060 | Warning | S F | A known boolean key isn't exactly `True`/`False` (e.g. `true`, `1`) | [A8](sng-spec.md#a8) |
| SNG061 | Warning (S) / Info (F) | S F | An empty value. It is dropped when packing. | [A9](sng-spec.md#a9) |

### Filenames

| ID | Sev. | Scope | Rule | Reference |
|----|------|-------|------|-----------|
| SNG030 | Error | S F | A path segment contains a forbidden character, a control character or DEL, `..`, ends with `.` or a space, is a Windows reserved name, or is empty | [§7](sng-spec.md#filename-rules), [A1](sng-spec.md#a1), [A12](sng-spec.md#a12) |
| SNG031 | Error | S F | The stored filename is longer than 255 UTF-8 bytes | [§7](sng-spec.md#filename-rules) |
| SNG032 | Error | S F | A duplicate filename, compared ignoring case | [A7](sng-spec.md#a7) |
| SNG033 | Error | S | The filename would resolve to a path outside the extraction folder | [A16](sng-spec.md#a16) |

### File data

| ID | Sev. | Scope | Rule | Reference |
|----|------|-------|------|-----------|
| SNG040 | Error | S | A file range is outside the FileData section, or two file ranges overlap | [A5](sng-spec.md#a5) |
| SNG041 | Warning | S | Gaps between files, or files not stored in index order | [A5](sng-spec.md#a5) |
| SNG042 | Warning | S | The sum of `contentsLen` values doesn't equal `fileDataLen` | [A5](sng-spec.md#a5) |
| SNG043 | Warning | S | There are bytes after the end of the FileData section | [A6](sng-spec.md#a6) |

### Registry and completeness

| ID | Sev. | Scope | Rule | Reference |
|----|------|-------|------|-----------|
| SNG050 | Info | S F | The filename isn't in the registry (this is allowed) | [§8](sng-spec.md#filename-registry) |
| SNG051 | Warning | S F | There is no `notes.chart` or `notes.mid` | [§8](sng-spec.md#filename-registry) |
| SNG052 | Warning | S F | There is no registered audio stem | [§8](sng-spec.md#filename-registry) |
| SNG053 | Warning | S | `song.ini` is stored as a file | [A17](sng-spec.md#a17) |
| SNG054 | Warning | S | A registered name isn't lowercase (e.g. `Song.ogg`). Packing always fixes this. | [A10](sng-spec.md#a10) |

### Source folder only

| ID | Sev. | Scope | Rule | Reference |
|----|------|-------|------|-----------|
| SNG070 | Warning | F | There is no `song.ini`, so metadata comes from the `[Song]` section of `notes.chart` | [§9.4](sng-spec.md#reference-behaviour) |
| SNG071 | Warning | F | `song.ini` has no `[song]` section. Metadata comes from `notes.chart` or the editor. (The reference tool fails the song in this case.) | [§9.4](sng-spec.md#reference-behaviour) |
| SNG072 | Info | F | A file or folder was excluded as OS or tool junk | [A14](sng-spec.md#a14) |
| SNG073 | Info | F | A subfolder was skipped because including subfolders is turned off | [A13](sng-spec.md#a13) |

Rule IDs are **stable**. A rule may be retired, but its ID is never reused.

---

## 7. UI outline

The main window has a navigation rail on the left and a content area on the right. Every screen
accepts drag and drop: dropping a folder offers Pack or Validate, dropping a `.sng` offers
Inspect, Unpack or Validate, and dropping several items offers Batch. Light and dark themes
follow the OS. Everything can be reached by keyboard, and controls have automation names for
screen readers.

```
┌──────────┬───────────────────────────────────────────────────────────────┐
│  Pack    │  PACK                                                         │
│  Unpack  │  Source:  [ D:\Songs\Artist - Title        ] [Browse]         │
│  Validate│  Output:  [ D:\Songs                       ] [Browse]         │
│  Inspect │ ┌───────────────────────────────────────────────────────────┐ │
│  Batch   │ │ ✓ notes.chart        42 KB                                │ │
│          │ │ ✓ song.ogg          4.1 MB   (renamed from Song.OGG)      │ │
│          │ │ ✓ album.jpg          210 KB                               │ │
│          │ │ – desktop.ini                 excluded: OS junk (SNG072)  │ │
│          │ └───────────────────────────────────────────────────────────┘ │
│          │  Metadata: 14 keys  [Edit…]                                   │
│          │  Findings: 0 errors · 1 warning · 2 info   [Show]             │
│ Settings │                                        [ Pack → .sng ]        │
└──────────┴───────────────────────────────────────────────────────────────┘
```

```
┌──────────┬───────────────────────────────────────────────────────────────┐
│          │  INSPECT  Artist - Title.sng            [Extract all] [Edit]  │
│          │  Header: SNGPKG · v1 · mask 3F 9A … 01                        │
│          │  Layout: [H|Meta|Index|■■■■■■■■■■■■ file data ■■■■■■■■■■■■]  │
│          │ ┌ Metadata ─────────────┐ ┌ Files ───────────────────────────┐│
│          │ │ name     Title        │ │ notes.chart   42 KB  @0x0000A1  ││
│          │ │ artist   Artist       │ │ song.ogg     4.1 MB  @0x00AB20  ││
│          │ │ charter  Someone      │ │ album.jpg    210 KB  @0x3FF100  ││
│          │ └───────────────────────┘ └──────────────────────────────────┘│
│          │  Findings ▾  ⚠ SNG060 pro_drums = "true" → use "True" [Fix]   │
└──────────┴───────────────────────────────────────────────────────────────┘
```

```
┌──────────┬───────────────────────────────────────────────────────────────┐
│          │  BATCH  Mode: (•) Pack  ( ) Unpack    Parallel: [4 ▾]         │
│          │  Input: [ D:\Songs ]   Output: [ D:\SNG ]   [x] Skip existing │
│          │ ┌───────────────────────────────────────────────────────────┐ │
│          │ │ [x] Artist A - Song 1     Ready            ██████████ 100%│ │
│          │ │ [x] Artist B - Song 2     1 warning        ████░░░░░░  40%│ │
│          │ │ [ ] Artist C - Song 3     2 errors         —              │ │
│          │ └───────────────────────────────────────────────────────────┘ │
│          │  Overall ██████░░░░ 61%  ·  ETA 0:42          [Cancel]        │
└──────────┴───────────────────────────────────────────────────────────────┘
```

The Unpack and Validate screens reuse the same source/output header, the findings panel and the
file list used by Pack.

## 8. Settings

Settings are stored as JSON (source-generated serialization) in the per-user config folder:
`%APPDATA%\ChartPacker`, `~/Library/Application Support/ChartPacker`, or
`$XDG_CONFIG_HOME/ChartPacker`.

| Setting | Default |
| ------- | ------- |
| Default pack output | The source folder's parent |
| Default unpack output | Next to the `.sng` |
| Overwrite policy | Ask |
| Include subfolders when packing | Off ([A13](sng-spec.md#a13)) |
| Include unknown files | On |
| Include video | On |
| Verify after write | On |
| Batch parallelism | Half the logical cores, at least 1 |
| Theme | Follow OS |

## 9. Non-functional requirements

| Area | Requirement |
| ---- | ----------- |
| Memory | File data is always streamed in fixed-size chunks (e.g. 1 MiB). A whole file is never loaded into memory. Peak working set doesn't grow with the size of any song file. |
| Large files | Every offset and length is a 64-bit value. Files over 4 GB are supported for packing, unpacking and inspection. |
| Responsiveness | All I/O and validation run off the UI thread. The UI thread is never blocked for more than 100 ms. |
| Durability | Every write goes to a temp file first and is then atomically renamed into place. A crash or cancel never leaves a half-written `.sng` or damages an existing one. |
| Robustness | Malformed input produces findings, never crashes. Allocations are bounded by the real file size, not by declared lengths. |
| Privacy | No telemetry and no network access. The only files the app writes are its settings file and log files. |
| Logging | A small built-in rolling file logger in the config folder (no logging framework, per the dependency budget), plus "Open log folder" in Settings. |
| Footprint | Fast cold start, low idle memory, small binary. The concrete targets are **TBD**: they will be measured from the first AOT build (M2) and then written here as regression budgets. |

## 10. Testing strategy

| Layer | Tests |
| ----- | ----- |
| Mask | Known vectors (the [§11 example](sng-spec.md#worked-example)); chunked masking at odd offsets gives the same result as one pass; mask ∘ mask = identity |
| Reader / Writer | Byte-exact golden files (the §11 example); round trips (pack → unpack → compare bytes and metadata); files over 4 GB using sparse temp files where the OS supports them |
| Interop | Golden `.sng` files produced by the reference `SngCli` at the pinned commit, which Chart-Packer must read. CI also runs `SngCli decode` on Chart-Packer output. |
| Validation | At least one fixture per rule ID that triggers it, and a clean fixture that triggers nothing |
| Fuzzing | Randomly mutated and truncated `.sng` inputs never throw an unhandled exception and never allocate more than the real file size |
| Path safety | Traversal, absolute paths, reserved names and mixed-case collisions on each OS |
| AOT smoke | CI runs `dotnet publish -c Release -r <rid>` on each OS. The build must produce **zero** trim or AOT warnings, then CI launches the published binary with `--self-test` (a headless mode that packs, validates and unpacks the §11 example and exits 0). |

UI view models are unit-tested against Core using fake services. v1 has no automated UI tests.

**Test isolation (required).** Tests must never read, create, modify or delete the real user
preferences and settings files, or anything else in the per-user config folder (§8). They use
temp files and folders only. Two things enforce this:

- **Explicit paths.** Services that persist state (such as `SettingsService`) take their file
  path as an argument and have no default. Only the app's composition root passes the real
  location, from `UserConfigPaths`. Tests pass a temp path.
- **Run guard.** Every test project includes a guard (`ChartPacker.Core.Tests/Isolation/UserConfigGuard.cs`
  is the reference). It snapshots the real config folder before the run and fails the run if
  anything in it was created, modified or deleted.

## 11. Milestones

| Milestone | Contents | Exit criteria |
| --------- | -------- | ------------- |
| **M1: Core** | `SngMask`, `SngReader`, `SngWriter`, `SongIni`, `FilenameRules`, `SngValidator` (full catalogue), tests. AOT analyzers on from the first commit. | Every Core test passes; zero AOT warnings; interop golden files pass |
| **M2: Pack / Unpack / Validate UI** | App shell, F1–F3, settings, logging, `--self-test` | AC1.x–AC3.x pass; first footprint baselines recorded in §9 |
| **M3: Inspector and metadata editor** | F5, F6 | AC5.x, AC6.x pass |
| **M4: Batch** | F4 | AC4.x pass |
| **M5: Packaging and release** | Per-RID AOT builds: Windows zip, macOS `.app`/`.dmg`, Linux tarball or AppImage | CI builds all six RIDs. Native AOT can't cross-compile between operating systems, so there is one runner per OS (Windows, macOS, Linux). |

<a id="future"></a>
## 12. Future (after v1)

- Opus audio transcoding and JPEG image re-encoding, plus album-art resizing, like
  SngCli's `--opusEncode`, `--jpegEncode` and `--albumResize`. This needs AOT-compatible
  codecs or a bundled native encoder.
- A `ChartPacker.Cli` front end built on Core.
- Sniffing audio and image content to catch extensions that don't match
  ([sng-spec §9.4](sng-spec.md#reference-behaviour)).
- Basic chart checks, such as a `notes.chart` that parses and has at least one track.
- Localisation.

## 13. Open questions

1. **Linux packaging:** AppImage, a plain tarball, or both? Should Flatpak be considered?
2. **macOS distribution:** signing and notarisation need an Apple Developer account. Should v1
   ship unsigned, with instructions?
3. **Windows distribution:** a zip only, or also an MSIX or installer?
4. **Upstream registry:** should Chart-Packer register any metadata keys it introduces with the
   upstream registry ([sng-spec §6.3](sng-spec.md#metadata-rules))? v1 plans to introduce none.
