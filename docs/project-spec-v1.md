# Chart-Packer — Project Specification v1

| | |
| --- | --- |
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
  The exceptions are reading the `[Song]` section of `notes.chart` as fallback metadata
  (F1.4), and decoding audio to play it (F7).
- Editing charts, or managing or scanning a song library beyond what batch processing needs.
  Finding songs in the scanned list is covered by [F8](#f8-library-search-sort-and-filter).
- Reading pre-version-1 `.sng` files ([sng-spec.md §9.1](sng-spec.md#reference-behaviour)).
- A command-line interface. The Core library is designed so one can be added later.

## 3. Target users and platforms

| | |
| --- | --- |
| **Users** | Chart authors preparing releases, and players converting or inspecting their libraries |
| **Runtime** | .NET 10 (LTS), published as **Native AOT**: a self-contained native executable with no .NET runtime to install |
| **Platforms / RIDs** | `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64` |
| **UI framework** | [Avalonia](https://avaloniaui.net/) 12.x, Fluent theme, Inter font |
| **Language** | English for v1. All user-facing strings live in `.resx` resources so they can be localised later. |

---

## 4. Architecture

```txt
Chart-Packer.slnx
├── ChartPacker/              Avalonia desktop app (MVVM)            (exists)
├── ChartPacker.Audio/        Audio decoding, mixing and playback (F7)
├── ChartPacker.Core/         UI-free library: format, validation, pack/unpack pipelines
└── ChartPacker.Core.Tests/   MSTest tests for Core and Audio
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
| AOT-safe library | `<IsAotCompatible>true</IsAotCompatible>` on `ChartPacker.Core` and `ChartPacker.Audio`, which turns on the trim, AOT and single-file analyzers |
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
- `tools/DecodeBench` times the audio decoders against FFmpeg's, and uses `BenchmarkDotNet`
  (MIT) for warm-up, iterations and statistics, on the JIT and on Native AOT as the app ships.
  It's a developer tool that's never shipped or referenced by the app, so the trim and AOT rule
  below doesn't apply to it.
- Image preview uses Avalonia's built-in Skia `Bitmap`.
- The one exception to Core's budget: it uses `z440.atl.core` (ATL.NET, MIT) to read audio
  durations from file headers, for the library list's length when a song has no `song_length`.
- `ChartPacker.Audio` plays audio (F7). It keeps codecs and native code out of Core, and uses:
  - FFmpeg's `vorbis`, `opus` and `mp1float`/`mp2float`/`mp3float` decoders, through
    `ChartPacker.Ffmpeg`, for mono and stereo streams when the libraries have them. Decoding is
    nearly all of a waveform read's time, and they're about 2× (Opus), 3× (Vorbis) and 8× (MP3)
    faster per core than the managed decoders (`tools/DecodeBench/RESULTS.md`). Only packets go to
    FFmpeg: the Ogg container, the MP3 frame index and gapless trim, and WAV are read in-house on
    either decoder, so timing, trims and seeks are the same on both.
  - `NVorbis` (MIT) to decode Vorbis, `NLayer` (MIT) to decode MPEG audio frames, and
    `Concentus` (BSD) to decode Opus packets, all managed code, when FFmpeg can't: its libraries
    are missing or built for video alone, or the stream has more than two channels (FFmpeg
    reorders those, and the mixer takes the order the managed decoders give).
  - `ppy.SDL3-CS` (MIT, with SDL itself under zlib) for audio output on every platform. Only
    SDL's audio subsystem is initialised. It ships SDL's native library for each RID, which
    publishes beside the executable.
  - `NVorbis` is a 1.0 release candidate: 0.10.x lands seeks hundreds of samples late.
- `ChartPacker.Video` decodes background videos for the detail panel's background tile (F5). It
  keeps native code out of Core, and has no packages. No managed library decodes H.264, VP8/VP9,
  Theora and MPEG video. LibVLC was considered, but it's far larger and doesn't fit the lockstep
  stem mixer.
- `ChartPacker.Ffmpeg` binds, by hand with `[LibraryImport]`, the few functions Video and Audio
  need from FFmpeg 9.0's shared libraries (`avformat`, `avcodec`, `avutil`, `swscale` and
  `swresample`, LGPL 2.1+), and has no packages. Audio uses it through a safe wrapper,
  `FfmpegAudioDecoder`; Video calls the bindings directly.
  - The libraries are built by `build/ffmpeg/build.sh` with only the registered video formats'
    demuxers and decoders, the registered audio formats' decoders, and swresample (which the Opus
    decoder needs), nothing GPL or non-free. They ship beside the executable like SDL's. Without
    them the app still runs: videos aren't previewed, and audio is decoded by the managed
    decoders.
  - The bindings are written for one major version of each library, which is checked at load.
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
   - **Adding a row** offers a dropdown of the known keys (`KnownKeys`, with a short description
     of each). The key box filters the list as you type, and any other key can be typed instead.
     Keys the selection already has are left out of the list.
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
   - Time values are edited in milliseconds.
   - **Instrument difficulties.** The known `diff_*` keys are shown as a row of round instrument
     icons at the top of the metadata, not as rows, and aren't offered as new keys.
     - Each icon has its difficulty in a badge at its bottom right, tinted by tier from 0 to 6.
       The badge shows `?` when the key is missing, negative or not an integer, and `*` when the
       selected songs differ. Pro, 22-fret, six-fret (GHL), Phase Shift and co-op variants get a
       small tag.
     - Under each icon are the instrument's name and the difficulties its chart has notes for, as
       coloured letters, e.g. "Guitar - EMHX". An icon whose part the chart doesn't have is greyed
       out. With several songs selected, the charts aren't read, so no icon is greyed out and
       there are no letters.
     - Icons that aren't charted, have no difficulty set and have no pending change are hidden
       behind **Show all**.
     - Clicking an icon opens a flyout with `?` and 0–6 buttons, a number box for higher tiers,
       and a revert button while it's edited. `?` writes `-1` where a difficulty is stored and
       leaves the others unchanged. An edited icon is ringed in the accent colour.
   - **Album art.** A tile at the top of the panel shows the album art. Hovering it shows icon
     buttons in its bottom-right corner: replace (a png, jpg or jpeg up to 16 MB) and remove. The
     background tile sits to its right, and with one song selected, its file list is below them.
     - An art change is staged in the draft like a metadata edit. The tile is marked `*` and gets a
       revert button. The file list strikes through the `album.*` files it removes and marks the
       one it adds `+`. The library thumbnail shows the staged image, or no art, before saving.
     - With several items selected, the tile shows their art when every image is identical
       (compared by SHA-256), and "Album art differs" otherwise. Replace and Remove apply to all of
       them.
     - Saving replaces every top-level `album.png`/`album.jpg`/`album.jpeg` with `album.<ext>` of
       the chosen image, or removes them all. In a `.sng` this is part of the same rewrite as the
       metadata (item 5): the new image is masked with the existing mask. In a folder the old files
       are deleted permanently, and a folder without a `song.ini` doesn't get one for an art-only
       change.
   - **Background.** A 16:9 tile beside the art shows the background video (`video.mp4`, `.avi`,
     `.webm`, `.vp8`, `.ogv` or `.mpeg`) or background image (`background.png`, `.jpg` or `.jpeg`),
     or "No background set". A song can have both, as games play the video when video
     backgrounds are on and the image otherwise. Then the video is shown first, and an
     **Image | Video** switch on the tile picks which one is shown.
     - Hovering shows the same buttons as the art: new (one picker for every image and video
       type; the file's extension decides which kind it replaces), remove (the kind shown) and,
       while there's a change, revert. Changes are staged in the draft like the art's, with the
       same `*` marker and struck-through and `+` rows in the file list. Names of either kind
       added through **Add files** go through the same change.
     - The video follows the audio player: it shows the frame due at the player's position,
       shifted by `video_start_time`, held at `video_end_time` (or the end), and looped from the
       start when `video_loop` is true. A `.sng`'s video is decoded straight out of the
       container, without extracting it.
     - With several items selected, each kind is shown when every song's is identical, and
       "Backgrounds differ" otherwise. Images are compared by SHA-256. Videos are compared by
       length first, and by SHA-256 only when every length is the same. New and Remove apply to
       all of them.
     - Saving replaces, or removes, every top-level file of the changed kind only, the way the
       art is saved. Nothing is written before then.
     - Video needs FFmpeg's libraries beside the executable (§4). Without them the tile says
       "Video preview unavailable", and new and remove still work.
   - **Files.** With one song selected, **Add files** under the file list picks any number of
     files, and each row has a remove button. Changes are staged in the draft like the art, so they
     survive a change of selection, and **Discard** or closing without saving drops them.
     - Added files are marked `+`, and their remove button drops the add. Removed files are struck
       through and get a revert button.
     - Each file is added under its own name: in the folder's root, or in the `.sng` under its
       normalised stored name (A10). Adding a name that's already there replaces that file. The
       old row is struck through, and reverting either row undoes the replacement.
     - A name that breaks the `.sng` filename rules (A1) is refused, and so is `song.ini`, which
       is edited as metadata. A folder's own `song.ini` can't be removed.
     - `album.png`/`album.jpg`/`album.jpeg` rows go through the album-art change instead, so the
       tile and the list always agree.
     - Saving a `.sng` makes the file changes part of the same rewrite (item 5). In a folder,
       every added file is copied to a temp file before anything is deleted, so a copy that fails
       leaves the folder unchanged. Removed files are deleted permanently.
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
5. **Preview:** album, background and highway images are shown in the app, and the background
   video plays in the detail panel's background tile (F5). Other files show their size and a hex
   view of the first bytes.
6. **Findings panel:** the F3 report inline, with each finding linked to the row or field it
   refers to.
7. **Layout view:** a bar showing the section and file byte ranges to scale, so gaps and
   overlaps are easy to see (SNG041, SNG040).

Acceptance criteria:

- **AC6.1** Opening a 2 GB `.sng` shows the header, metadata and file list in under 500 ms on
  an SSD. File data is not read until it's needed.
- **AC6.2** Extracting a single file produces bytes identical to what a full unpack gives for
  that file.

### F7. Audio playback

**Input:** the one song selected in the library, a `.sng` or a folder.

Behaviour:

1. **Tracks.** The song's audio tracks are its registered audio stems
   ([sng-spec §8](sng-spec.md#filename-registry)) in any of `.ogg` (Vorbis), `.opus`, `.mp3` and
   `.wav`, in stem order. The draft's changes apply: a track it removes isn't listed, and a track
   it adds (marked `+`) plays from the file it will be copied from, so a change can be heard
   before it's saved.
2. **Playing.** Play, pause, stop, a seek bar, a volume slider, and **Preview** to play from
   `preview_start_time` when the song sets one. Every track plays together, sample-aligned: each
   step reads the same number of frames from every track, heard or not. Tracks at another sample
   rate are resampled to the rate most tracks share. The sum is clipped.
3. **Mute and solo.** Each track can be muted, or soloed; while any track is soloed only the
   soloed tracks are heard. `preview.*`, a separate clip, starts muted.
4. **Sync across formats.** Opus pre-skip and end trimming follow RFC 7845. An MP3's LAME tag
   encoder delay and padding are trimmed, plus the decoder's 529-sample delay, as mpg123 and
   FFmpeg do. Vorbis's start and end are trimmed by its granule positions (Vorbis I §A.2). Seeks
   are sample-exact: each decoder restarts before the target and discards up to it. The trims
   are made by the app's own container code, so a stream is the same length and lines up the
   same whether FFmpeg or a managed decoder decodes its packets (§4).
5. **Adding and removing tracks.** **Add track** lists every stem; one the song has already reads
   "Replace". It picks one audio file, decodes a little of it to check that it's what its
   extension says, and stages it as `<stem>.<ext>`, staging every other file of that stem, in any
   format, for removal. Each track row has a remove button. These are file changes (F5 item 1,
   Files): saved, discarded and shown in the file list the same way.
6. **Files stay free.** Nothing is opened until the first play. Changing the selection, or
   saving, stops playback and closes every file first, so saving can replace or delete them.
7. **Failures.** A track that can't be opened or decoded says why on its row and stays silent;
   the others play on. No audio device is reported without affecting the rest of the app.

Acceptance criteria:

- **AC7.1** A song whose tracks are in all four formats plays them in step: a signal present in
  every track is heard once, not smeared.
- **AC7.2** Seeking, muting, soloing and changing the selection while playing never leaves a
  file open: the song can be saved straight away.
- **AC7.3** Replacing `guitar.ogg` with an `.opus` file and saving leaves `guitar.opus` and no
  `guitar.ogg`, in a `.sng` and in a folder.

### F8. Library search, sort and filter

**Input:** the songs found by the last scan.

Behaviour:

1. **Search.** A search box above the list (Ctrl+F; Escape clears it) shows only songs where every
   word typed appears in the file or folder name, the title or the artist. Matching ignores case
   and accents.
2. **Sort.** A flyout from the filter button sorts by scan order (the default), title, artist,
   album, genre, charter, year, length, size, type (`.sng` files first) or file name, either
   way. Songs without the value sort last in both directions, and ties keep scan order. The sort
   is remembered (§8); the search and filters start empty each launch.
3. **Filters.** The same flyout filters by type (`.sng` files, song folders), text contained in
   the title, artist, album, genre or charter, a year, length (minutes) and size (MB) range, and
   having unsaved changes. Every filter must pass. A song without the filtered value doesn't
   pass. The button shows how many filters are on; **Reset filters** turns them all off.
4. **Content filters.** The flyout also filters by what the song has, with a chip per item that
   cycles **Either** (off), **Includes** and **Excludes**; right-clicking a chip puts it straight
   back to Either:
   - **Instruments**: every chart part in the MIDI part catalogue, from guitar to dance, that has
     at least one note, so a template's empty tracks don't count.
   - **Difficulties**: Easy, Medium, Hard and Expert, having notes on any part.
   - **Audio stems**: song, guitar, rhythm, bass, keys, drums (`drums` and `drums_1`–`drums_4`),
     vocals (`vocals`, `vocals_1`, `vocals_2`), crowd and preview.
   - **Backgrounds and art**: a background video, a background image, album art.

   A song with any excluded item doesn't pass. The first three groups each have an **All** /
   **Any** switch for their included items: All needs every one, Any needs at least one. So
   "only these" is the wanted items included and the rest excluded. Each group, and each
   background or art chip, counts as one filter.
5. **Unsaved changes count.** Values come from the metadata with the song's draft applied, so an
   edit re-sorts and re-filters the list before it's saved. The same goes for the stems,
   backgrounds and album art a draft adds or removes. A chart a draft adds hasn't been read, so
   its instruments and difficulties aren't known until it's saved. The year is the first
   four-digit number in `year`, which handles Clone Hero's `", 2004"`.
6. **While scanning.** Songs join the list as their metadata and chart are read. The list is
   re-sorted and re-filtered at most a few times a second.
7. **Selection.** A selected song the filters hide is deselected, so Pack and Unpack only act on
   songs that can be seen. Selected songs that still show stay selected.
8. **Counts.** While the search or a filter is on, the status line reads `shown/total songs`.
   When nothing matches, the list says so, with a button that clears the search and filters.

Acceptance criteria:

- **AC8.1** Searching for an artist shows only that artist's songs, and the status reads
  `n/total songs`. Clearing the search shows every song again, in the chosen sort.
- **AC8.2** Sorting by year in either direction lists songs without a year last.
- **AC8.3** Selecting three songs and then filtering one of them out leaves two selected, and
  Pack acts on those two.
- **AC8.4** Setting Drums to Includes and Vocals to Excludes, with All, shows only songs whose
  chart has drum notes and no vocal notes. Switching Instruments to Any with Guitar and Bass
  included shows songs with either part.
- **AC8.5** Adding a background video to a song without saving it takes the song out of the list
  while Video is set to Excludes.

---

`<a id="validation-rules"></a>`

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
| ---- | ------ | ------- | ------ | ----------- |
| SNG001 | Error | S | The first 6 bytes aren't `SNGPKG` | [§4.1](sng-spec.md#sec-header) |
| SNG002 | Error | S | `version` isn't `1` | [A2](sng-spec.md#a2) |
| SNG003 | Error | S | A section length, string length or file range goes past the end of the file (truncated file) | [§3](sng-spec.md#file-layout) |
| SNG010 | Error | S | `metadataLen` or `fileMetaLen` doesn't equal the bytes the entries actually use | [A3](sng-spec.md#a3) |
| SNG011 | Error | S | A negative `keyLen` or `valueLen` | [§4.2](sng-spec.md#sec-metadatapair) |
| SNG012 | Error | S | `metadataCount` or `fileCount` can't fit in the declared section length | [A3](sng-spec.md#a3) |
| SNG013 | Error | S F | Invalid UTF-8, or a `0x00` byte, in a key, value or filename | [A11](sng-spec.md#a11) |

### Metadata

| ID | Sev. | Scope | Rule | Reference |
| ---- | ------ | ------- | ------ | ----------- |
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
| ---- | ------ | ------- | ------ | ----------- |
| SNG030 | Error | S F | A path segment contains a forbidden character, a control character or DEL, `..`, ends with `.` or a space, is a Windows reserved name, or is empty | [§7](sng-spec.md#filename-rules), [A1](sng-spec.md#a1), [A12](sng-spec.md#a12) |
| SNG031 | Error | S F | The stored filename is longer than 255 UTF-8 bytes | [§7](sng-spec.md#filename-rules) |
| SNG032 | Error | S F | A duplicate filename, compared ignoring case | [A7](sng-spec.md#a7) |
| SNG033 | Error | S | The filename would resolve to a path outside the extraction folder | [A16](sng-spec.md#a16) |

### File data

| ID | Sev. | Scope | Rule | Reference |
| ---- | ------ | ------- | ------ | ----------- |
| SNG040 | Error | S | A file range is outside the FileData section, or two file ranges overlap | [A5](sng-spec.md#a5) |
| SNG041 | Warning | S | Gaps between files, or files not stored in index order | [A5](sng-spec.md#a5) |
| SNG042 | Warning | S | The sum of `contentsLen` values doesn't equal `fileDataLen` | [A5](sng-spec.md#a5) |
| SNG043 | Warning | S | There are bytes after the end of the FileData section | [A6](sng-spec.md#a6) |

### Registry and completeness

| ID | Sev. | Scope | Rule | Reference |
| ---- | ------ | ------- | ------ | ----------- |
| SNG050 | Info | S F | The filename isn't in the registry (this is allowed) | [§8](sng-spec.md#filename-registry) |
| SNG051 | Warning | S F | There is no `notes.chart` or `notes.mid` | [§8](sng-spec.md#filename-registry) |
| SNG052 | Warning | S F | There is no registered audio stem | [§8](sng-spec.md#filename-registry) |
| SNG053 | Warning | S | `song.ini` is stored as a file | [A17](sng-spec.md#a17) |
| SNG054 | Warning | S | A registered name isn't lowercase (e.g. `Song.ogg`). Packing always fixes this. | [A10](sng-spec.md#a10) |

### Source folder only

| ID | Sev. | Scope | Rule | Reference |
| ---- | ------ | ------- | ------ | ----------- |
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

```txt
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

```txt
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

```txt
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
| Library sort ([F8](#f8-library-search-sort-and-filter)) | Scan order, first to last |

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
| **M3: Inspector and metadata editor** | F5, F6, F7 | AC5.x, AC6.x, AC7.x pass |
| **M4: Batch** | F4 | AC4.x pass |
| **M5: Packaging and release** | Per-RID AOT builds: Windows zip, macOS `.app`/`.dmg`, Linux tarball or AppImage | CI builds all six RIDs. Native AOT can't cross-compile between operating systems, so there is one runner per OS (Windows, macOS, Linux). |

`<a id="future"></a>`

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
