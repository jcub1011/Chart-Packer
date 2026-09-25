# SNG File Format Reference

A detailed overview of the `.sng` container format as used by Clone Hero, YARG and
other rhythm games, written as the working reference for Chart-Packer.

- **Upstream spec:** [`mdsitton/SngFileFormat`](https://github.com/mdsitton/SngFileFormat) (MIT licensed)
- **Pinned revision:** every citation in this document points at commit
  [`6683799`](https://github.com/mdsitton/SngFileFormat/tree/668379942a3790c1b47ddf56b71eb3a7ada449c2)
  (2024-06-24), so line numbers stay stable even if upstream changes.
- **How to read the citations:**
  - **[README Lx–y]** is the normative specification text.
  - **[SngSerializer.cs Lx–y]** and similar point at the *reference implementation* (SngCli/SngLib).
    It shows how the format is used in practice, but it is not the spec.

Where the spec and the reference implementation disagree or the spec is silent,
[§10 Ambiguities](#ambiguities) records the issue and what Chart-Packer does about it.

---

## Contents

1. [What `.sng` is](#what-sng-is)
2. [Data types](#data-types)
3. [File layout](#file-layout)
4. [Sections](#sections)
5. [Masking](#masking)
6. [Metadata rules](#metadata-rules)
7. [Filename rules](#filename-rules)
8. [Filename registry](#filename-registry)
9. [Reference implementation behaviour](#reference-behaviour)
10. [Ambiguities and Chart-Packer interpretations](#ambiguities)
11. [Worked example](#worked-example)
12. [Sources](#sources)

---

<a id="what-sng-is"></a>
## 1. What `.sng` is

> "`.sng` is a simple and generic binary container format that groups a list of files and
> metadata into a single file." — [README L3][readme-intro]

A song that would normally be a folder (`song.ini`, `notes.chart`/`notes.mid`, audio stems,
album art, …) becomes one file:

- **Metadata:** the `[song]` section of `song.ini` is stored as key/value pairs in the
  container. `song.ini` itself is not stored ([README L273][readme-registry-files]).
- **Files:** every other file is stored byte for byte, with no compression, behind a simple XOR
  mask ([README L139–150][readme-masking]).

The design goals, from [README L238–247][readme-design]:

- **No compression.** File offsets stay fixed, so audio can be streamed or memory-mapped
  straight from the container. This avoids ZIP's decompression cost and the 2 GB per-entry
  limit it has in C#.
- **Length-prefixed sections.** A reader can scan just the metadata or just the file index and
  skip everything else.
- **Masking.** Masking the file data stops programs that don't understand the format from
  detecting the contents directly. It is obfuscation, not security.
- **Little-endian.** Every integer is little-endian.
- **Short filenames.** Filenames are capped at 255 bytes, a limit most filesystems share.
- **INI-compatible metadata.** Metadata must be able to round-trip to a `song.ini`.

---

<a id="data-types"></a>
## 2. Data types

From [README L53–67][readme-types]. **All multi-byte integers are little-endian**
([README L246][readme-design]).

| Data type      | Definition |
| -------------- | ---------- |
| `byte`         | 8-bit unsigned integer |
| `uint32`       | 32-bit unsigned integer |
| `uint64`       | 64-bit unsigned integer |
| `int32`        | 32-bit **signed** integer (used only for metadata key/value lengths) |
| `string`       | UTF-8 bytes. **Must not contain `0x00`** (NUL), because many languages treat it as end-of-string. Strings are length-prefixed, not NUL-terminated. |
| `SngIdentify`  | The 6 ASCII bytes `SNGPKG`: `0x53 0x4E 0x47 0x50 0x4B 0x47` |
| `byte[]`       | A raw byte array |
| `MetadataPair[]`, `FileMeta[]`, `File[]` | Arrays of the structures defined in [§4](#sections) |
| `maskedByte[]` | File bytes after the mask in [§5](#masking) has been applied |

---

<a id="file-layout"></a>
## 3. File layout

The file is a fixed header followed by three length-prefixed sections, **in this exact
order** ([README L69–85][readme-overview]):

| # | Section   | Purpose |
|---|-----------|---------|
| 1 | Header    | Identifier, version, and the mask seed |
| 2 | Metadata  | Song metadata (key/value strings) |
| 3 | FileIndex | Name, size and offset of each stored file |
| 4 | FileData  | The masked file contents, concatenated |

Each of the three sections starts with a `uint64` giving the **number of bytes in the section
after that length field** ([README L73–76][readme-overview]).

### Byte map

Let `M = metadataLen`, `I = fileMetaLen` and `D = fileDataLen`.

```
Offset (dec)          Size  Field
─────────────────────────────────────────────────────────────── Header (26 bytes, fixed)
0                     6     fileIdentifier   "SNGPKG"
6                     4     version          uint32  (= 1)
10                    16    xorMask          byte[16]
─────────────────────────────────────────────────────────────── Metadata
26                    8     metadataLen   (M) uint64
34                    8     metadataCount     uint64
42                    M-8   metadataPairArray
─────────────────────────────────────────────────────────────── FileIndex
34+M                  8     fileMetaLen   (I) uint64
42+M                  8     fileCount         uint64
50+M                  I-8   fileMetaArray
─────────────────────────────────────────────────────────────── FileData
42+M+I                8     fileDataLen   (D) uint64
50+M+I                D     fileDataArray  ← first file's contentsIndex (in a canonical file)
─────────────────────────────────────────────────────────────── End
50+M+I+D                    total file size (canonical)
```

These offsets match what the reference writer produces: it writes everything before the file
data as one block and uses that block's length as the first file's `contentsIndex`
([SngSerializer.cs L341–361][ser-save]).

---

<a id="sections"></a>
## 4. Sections

<a id="sec-header"></a>
### 4.1 Header

[README L90–95][readme-header]

| Field            | Type          | Size | Description |
| ---------------- | ------------- | ---- | ----------- |
| `fileIdentifier` | `SngIdentify` | 6    | Must be `SNGPKG` |
| `version`        | `uint32`      | 4    | Format version. The only version defined is **1**. That value appears only in the reference source, `SngFile.CurrentVersion = 1` ([SngFile.cs L8][file-version]); see [A2](#a2). |
| `xorMask`        | `byte[16]`    | 16   | Random bytes used as the mask seed ([§5](#masking)) |

The reference reader rejects a wrong identifier with `"Invalid SNG file identifier"` and any
version other than 1 with `"Unsupported SNG file version"` ([SngSerializer.cs L122–137][ser-load-header]).

<a id="sec-metadata"></a>
### 4.2 Metadata

[README L98–103][readme-metadata]

| Field               | Type             | Size              | Description |
| ------------------- | ---------------- | ----------------- | ----------- |
| `metadataLen`       | `uint64`         | 8                 | Bytes in this section **after** this field. That includes the 8-byte `metadataCount`. |
| `metadataCount`     | `uint64`         | 8                 | Number of `MetadataPair` entries |
| `metadataPairArray` | `MetadataPair[]` | `metadataLen - 8` | The pairs, one after another |

The reference writer computes `metadataLen` as `8` (the count) plus, for each pair,
`4 + keyBytes + 4 + valueBytes` ([SngSerializer.cs L250–265][ser-meta-len]).

<a id="sec-metadatapair"></a>
#### MetadataPair

[README L105–111][readme-metadatapair]

| Field      | Type     | Size       | Description |
| ---------- | -------- | ---------- | ----------- |
| `keyLen`   | `int32`  | 4          | Key length in **bytes** (signed; a negative value is invalid) |
| `key`      | `string` | `keyLen`   | UTF-8 key |
| `valueLen` | `int32`  | 4          | Value length in **bytes** (signed; a negative value is invalid) |
| `value`    | `string` | `valueLen` | UTF-8 value |

The reference reader rejects a negative `keyLen` or `valueLen`
([SngSerializer.cs L145–160][ser-load-meta]). The character rules are in [§6](#metadata-rules).

<a id="sec-fileindex"></a>
### 4.3 FileIndex

[README L113–118][readme-fileindex]

| Field           | Type         | Size              | Description |
| --------------- | ------------ | ----------------- | ----------- |
| `fileMetaLen`   | `uint64`     | 8                 | Bytes in this section after this field, including the 8-byte `fileCount` |
| `fileCount`     | `uint64`     | 8                 | Number of `FileMeta` entries, which is also the number of `File` entries |
| `fileMetaArray` | `FileMeta[]` | `fileMetaLen - 8` | The entries, one after another |

The reference writer computes `fileMetaLen` as `8`, plus `1 + nameBytes + 8 + 8` for each file
([SngSerializer.cs L267–283][ser-file-len]).

<a id="sec-filemeta"></a>
#### FileMeta

[README L120–126][readme-filemeta]

| Field           | Type     | Size          | Description |
| --------------- | -------- | ------------- | ----------- |
| `filenameLen`   | `byte`   | 1             | Filename length in bytes (so at most 255) |
| `filename`      | `string` | `filenameLen` | Path relative to the song folder. Folders are separated by `/`, with the filename last. |
| `contentsLen`   | `uint64` | 8             | Size of the file in bytes |
| `contentsIndex` | `uint64` | 8             | Offset of the file's first byte, **counted from the start of the `.sng` file** (see [A4](#a4)) |

<a id="sec-filedata"></a>
### 4.4 FileData

[README L128–137][readme-filedata]

| Field           | Type     | Size          | Description |
| --------------- | -------- | ------------- | ----------- |
| `fileDataLen`   | `uint64` | 8             | Total bytes of all file contents. Unlike the other two sections, there is no count field. |
| `fileDataArray` | `File[]` | `fileDataLen` | The `File` entries, concatenated |

Each `File` is just `maskedFileBytes : maskedByte[contentsLen]`, the file's contents after
masking.

The reference writer stores files **contiguously, in the same order as the FileIndex**. Each
`contentsIndex` is the previous one plus the previous `contentsLen`
([SngSerializer.cs L313–331][ser-write-index]), and the data is written in the same dictionary
order ([SngSerializer.cs L363–377][ser-write-data]).

---

<a id="masking"></a>
## 5. Masking

[README L139–150][readme-masking]

Each file is masked on its own. The mask for the byte at position `i` **within that file**
(counting from 0 at the file's first byte, not from the start of the `.sng`) is:

```
xorKey       = xorMask[i % 16] XOR (i AND 0xFF)
fileBytes[i] = maskedFileBytes[i] XOR xorKey
```

Properties:

- **Symmetric.** XOR is its own inverse, so the same routine masks and unmasks. The
  reference implementation calls one `MaskData` function for both load and save
  ([SngSerializer.cs L208][ser-load-files], [L373][ser-write-data]).
- **Repeats every 256 bytes.** 16 divides 256, so the mask for `i` depends only on `i & 0xFF`.
  The reference implementation precomputes a 256-entry table,
  `lookup[i] = (byte)(i ^ seed[i & 0xF])` ([SngSerializer.cs L20–31][ser-mask-lookup]), and
  applies it with SIMD vectors ([SngSerializer.cs L50–108][ser-mask-apply]).
- **Any seed is valid.** The reference encoder fills the seed with `Random.Shared.NextBytes`
  ([SngEncode.cs L512][enc-mask]), which is not a cryptographic RNG and doesn't need to be. Even
  an all-zero seed still changes the data, because the `i AND 0xFF` term remains.
- **No integrity protection.** There is no checksum or MAC. The mask exists only so the
  contents can't be detected directly ([README L245][readme-design]).

C# equivalent of the reference behaviour, as a plain loop:

```csharp
static void ApplyMask(Span<byte> data, ReadOnlySpan<byte> xorMask, long startIndex = 0)
{
    // startIndex lets a caller mask a file in chunks: it is the chunk's offset within the file.
    for (int j = 0; j < data.Length; j++)
    {
        long i = startIndex + j;
        data[j] ^= (byte)(xorMask[(int)(i & 0xF)] ^ (byte)i);
    }
}
```

---

<a id="metadata-rules"></a>
## 6. Metadata rules

### 6.1 Forbidden characters

[README L151–161][readme-meta-strings]. These rules exist so metadata can round-trip through a
`song.ini`, which is an INI file ([README L241][readme-design]):

| Character | Keys | Values | Reason |
| --------- | ---- | ------ | ------ |
| `;` (semicolon) | ✗ | ✗ | Starts a comment in INI |
| `\r`, `\n` (newlines) | ✗ | ✗ | End an INI line |
| `=` (equals) | ✗ | allowed | Separates key from value. Only the *first* `=` on a line counts, so values may contain `=` (e.g. `<color=#00FF00>`). |
| `0x00` (NUL) | ✗ | ✗ | Excluded from every SNG `string` ([README L61][readme-types]) |

The README recommends replacing or removing these characters *before* converting to SNG
([README L152][readme-meta-strings]).

### 6.2 Value types

[README L163–194][readme-meta-values]. Every value is stored as a string. Applications decide
how to interpret it, but these baseline forms should be consistent everywhere:

| Type    | Canonical form | Examples |
| ------- | -------------- | -------- |
| bool    | `True` or `False`. The spec says these are **case-sensitive** ([README L167–173][readme-meta-values]). | `True` |
| integer | Optional `+`/`-`, then the digits `0–9` | `-150`, `2024` |
| float   | Optional `+`/`-`, digits, a `.`, then more digits | `1.25` |
| string  | Anything else that follows the rules in §6.1 and has no NUL | `Through the Fire and Flames` |

### 6.3 Key registry

The README has no SNG-specific keys yet. It says that every key listed in TheNathannator's
GuitarGame_ChartFormats tag lists should be treated as taken, and it asks applications to
register new keys, using an app prefix for app-specific ones ([README L249–261][readme-registry]):

- [song.ini – Typical Tags](https://github.com/TheNathannator/GuitarGame_ChartFormats/blob/main/doc/FileFormats/song.ini/Typical%20Tags.md)
- [song.ini – Game-Specific Tags](https://github.com/TheNathannator/GuitarGame_ChartFormats/blob/main/doc/FileFormats/song.ini/Game-Specific%20Tags.md)

SngCli has its own table of about 100 known keys with expected types (Integer, String, Boolean,
ColorCode, IntegerPair, ProGuitarTuning), in [KnownKeys.cs L10–143][kk-table]. Chart-Packer
uses this table for its type hints.

---

<a id="filename-rules"></a>
## 7. Filename rules

[README L196–236][readme-filenames]. These rules keep extraction safe on every OS.

A filename **must not** contain or match any of these:

| Rule | Detail |
| ---- | ------ |
| Reserved characters | `<` `>` `:` `"` `/` `\` `\|` `?` `*` |
| Control characters | `0x00`–`0x1F` (the README says "below 31", but its range `0x00 - 0x1F` includes 31; see [A12](#a12)) |
| DEL | `0x7F` |
| Dot-dot | `..` (two consecutive periods) anywhere |
| Trailing characters | The name must not end with `.` or a space |
| Windows reserved names | `CON`, `PRN`, `AUX`, `NUL`, `COM0`–`COM9`, `LPT0`–`LPT9`, **with or without an extension** (`CON` and `CON.txt` are both forbidden) |
| Length | At most **255 bytes** of UTF-8 ([README L247][readme-design]). The `filenameLen` field is one byte, so a longer name can't be encoded. |

**Folders.** A `filename` may contain folders separated by `/`
([README L124][readme-filemeta], [README L269][readme-registry-files]). The README also lists `/`
as a forbidden character. [A1](#a1) explains how Chart-Packer reconciles the two.

---

<a id="filename-registry"></a>
## 8. Filename registry

[README L263–292][readme-registry-files]. Registered names are **lowercase on purpose**. The
reference encoder converts registered names to lowercase and leaves unknown names as they are
([README L267][readme-registry-files]). No folders are registered.

| Category | Registered names |
| -------- | ---------------- |
| Chart    | `notes.chart`, `notes.mid` |
| Metadata | `song.ini`: reserved, and **not stored** in the `.sng`, because its contents become the Metadata section |
| Images   | `album`, `background`, `highway` + `.png` / `.jpg` / `.jpeg` |
| Video    | `video` + `.mp4` / `.avi` / `.webm` / `.vp8` / `.ogv` / `.mpeg` |
| Audio    | `guitar`, `bass`, `rhythm`, `vocals`, `vocals_1`, `vocals_2`, `drums`, `drums_1`, `drums_2`, `drums_3`, `drums_4`, `keys`, `song`, `crowd`, `preview` + `.mp3` / `.ogg` / `.opus` / `.wav` |

A `.sng` may also contain **any other file**. The format is a generic container
([README L242][readme-design]). Unregistered names are legal; they just aren't recognised by
name.

---

<a id="reference-behaviour"></a>
## 9. Reference implementation behaviour

SngCli is the de facto producer of `.sng` files, so its behaviour matters for compatibility.
None of the points below come from the README.

### 9.1 Reader (`SngSerializer.LoadSngFile`)

[SngSerializer.cs L118–215][ser-load]

- Reads the header, then **reads the sections one after another**. It never uses `metadataLen`
  or `fileMetaLen` to find the next section, so a wrong length value goes unnoticed as long as
  the counts are right ([L139–190][ser-load-meta]).
- Throws on a bad identifier, an unsupported version, or a negative key/value/filename length
  ([L126–137][ser-load-header], [L145–179][ser-load-meta]).
- Checks each declared length only against the **total file size**, using `<`
  ([L110–116][ser-ensure]). Its error message suggests that older, pre-version-1 `.sng` files
  exist in the wild.
- **Seeks to each file's `contentsIndex` from the start of the file** (`SeekOrigin.Begin`)
  before reading it ([L193–211][ser-load-files]).
- Decodes strings with `Encoding.UTF8.GetString`, so invalid UTF-8 quietly becomes U+FFFD
  instead of raising an error ([L154][ser-load-meta], [L183][ser-load-meta]).
- Does **not** check the filename or metadata character rules.

### 9.2 Writer (`SngSerializer.SaveSngFile`)

[SngSerializer.cs L333–380][ser-save]

- Refuses to write any version other than 1 ([L335–336][ser-save]).
- **Leaves out** metadata pairs whose key *or* value is empty ([L230–242][ser-count-meta],
  [L256][ser-meta-len], [L298][ser-write-meta]) and file entries that are `null`.
- Casts the filename length to `byte` with no check, so a name over 255 bytes produces a
  corrupt entry ([L323–326][ser-write-index]).
- Opens the output with `File.OpenWrite`, which **doesn't truncate**. Overwriting a larger
  existing file leaves stale bytes at the end ([L363][ser-write-data]).

### 9.3 Data model (`SngFile`)

[SngFile.cs][file-model]

- `CurrentVersion = 1`, and the mask is 16 bytes ([L8–10][file-version]).
- Metadata is held in a **case-sensitive** `Dictionary<string,string>`. `SetString` overwrites,
  so for a duplicate key the **last value wins** ([L14][file-version], [L123–129][file-setters]).
- Files are held in a `Dictionary<string, …>`. For a duplicate filename `AddFile` keeps the
  **first** one ([L15–23][file-addfile]).
- `TryGetBool` uses .NET `bool.TryParse`, which ignores case ([L63–73][file-getbool]).

### 9.4 Encoder (`SngCli encode`)

[SngEncode.cs][enc]

- **Finding song folders.** A folder counts as a song if it has a `.chart` or `.mid` **and**
  an audio file (`.wav`/`.ogg`/`.opus`/`.mp3`) **and** either a `song.ini` or a `.chart`
  ([L57–66][enc-valid]).
- **Mask.** Generated with `Random.Shared.NextBytes` ([L512][enc-mask]).
- **Metadata from `song.ini`.** The encoder loads the `[song]` section, matching the section
  name without regard to case ([IniParser.cs L112–133][ini-section]). Then it runs
  `KnownKeys.ValidateKeys` and copies every key ([L199–225][enc-parse-meta]). If `song.ini`
  exists but has no `[song]` section, the song fails.
- **Metadata from `notes.chart`.** If there is no `song.ini`, the encoder reads the first
  section of `notes.chart` and maps these keys:
  - `Name`, `Artist`, `Genre`, `Charter`, `Year`, `Album` go to the lowercase keys of the same
    name. A leading `", "` is removed from `Year`.
  - `Offset` goes to `delay` and `PreviewStart` goes to `preview_start_time`, both converted
    from seconds to milliseconds and rounded up ([L68–197][enc-chart-meta], [L432–439][enc-folder]).
- **`KnownKeys.ValidateKeys`.** Invalid values for known keys are **silently dropped**, and
  legacy keys are renamed ([KnownKeys.cs L147–153][kk-legacy], [L160–289][kk-validate]):
  - `frets` → `charter`
  - `track` → `album_track`
  - `star_power_note` → `multiplier_note`
  - `pro_drum` → `pro_drums`
- **Filenames.** Registered audio, image and video names are lowercased, and unknown names are
  kept as they are ([L385–388][enc-folder]). `notes.mid` and `notes.chart` are always stored
  under those exact lowercase names ([L428–439][enc-folder]).
- **Audio extensions.** Audio files get their extension rewritten to match the format detected
  from the content ([L307–308][enc-audio]).
- **Unknown files.** Unknown files are included by default. `--skipUnknown` leaves them out
  ([L464–471][enc-folder]).
- **Subfolders.** Subfolders that aren't themselves song folders are included under
  `folder/…` paths ([L517–543][enc-subfolders]).
- **Exclusion lists.** The encoder declares lists of files and folders to exclude
  (`desktop.ini`, `.DS_Store`, `ps.dat`, `ch.dat`; `__MACOSX`) ([L260–271][enc-exclude]).
  In practice the lists have **no effect**:
  - The top-level folder pass never checks the file list.
  - The subfolder pass compares the list against the *full path* ([L344][enc-subfolder-exclude]).
  - The folder check compares against the *parent* directory path ([L522–523][enc-subfolders]).
- **Nested `.sng` files** are always skipped ([L404–408][enc-folder]).

### 9.5 Decoder (`SngCli decode`)

[SngDecode.cs L18–66][dec]

- Writes a `song.ini` with one `[song]` section built from the metadata, in `key = value` form,
  after `KnownKeys.ValidateKeys` ([L18–33][dec], [IniParser.cs L90–110][ini-save]).
- Builds each output path with `Path.Combine(outputFolder, name.Split("/"))` and **no
  filename checks** ([L57][dec]). A crafted name with `..` segments or an absolute path could
  write outside the output folder.

---

<a id="ambiguities"></a>
## 10. Ambiguities and Chart-Packer interpretations

Each entry gives the issue, the evidence, and the rule Chart-Packer follows. The rule IDs
(`SNGxxx`) are defined in [project-spec-v1.md §6](project-spec-v1.md#validation-rules).

<a id="a1"></a>
### A1. `/` is both forbidden and the folder separator
- **Evidence:** `/` is on the forbidden-character list ([README L203][readme-filenames]), but it
  is also the folder separator ([README L124][readme-filemeta], [L269][readme-registry-files]).
- **Chart-Packer:**
  - Splits `filename` on `/` and applies the rules in §7 to **each segment**.
  - Empty segments (a leading `/`, a trailing `/`, or `//`) are errors.
  - The 255-byte limit applies to the **whole** `filename` field.

<a id="a2"></a>
### A2. The version number appears only in code
- **Evidence:** The README defines a `version` field ([L94][readme-header]) but never states a
  value. The reference code defines `CurrentVersion = 1` ([SngFile.cs L8][file-version]) and
  rejects anything else ([SngSerializer.cs L136–137][ser-load-header]).
- **Chart-Packer:** Writes `1`. Treats any other version as an **error** (SNG002), which
  matches the reference reader.

<a id="a3"></a>
### A3. Section lengths aren't checked by the reference reader
- **Evidence:** The README defines `metadataLen` and `fileMetaLen` as byte counts
  ([L101][readme-metadata], [L116][readme-fileindex]). The reference reader ignores them and
  parses sequentially ([SngSerializer.cs L139–190][ser-load-meta]).
- **Chart-Packer:**
  - Uses the lengths to find each section.
  - Reports an **error** (SNG010) if the bytes the pairs or entries actually use differ from
    the declared length.
  - Reports an **error** (SNG012) if the counts don't fit inside the declared lengths.

<a id="a4"></a>
### A4. What `contentsIndex` is measured from
- **Evidence:** The README says "the first byte index from the start of the file"
  ([L126][readme-filemeta]), which could mean the `.sng` or the FileData section. The reference
  writer starts at the byte count of everything before the file data
  ([SngSerializer.cs L317][ser-write-index], [L360][ser-save]), and the reader seeks with
  `SeekOrigin.Begin` ([L201–202][ser-load-files]).
- **Chart-Packer:** `contentsIndex` is an **absolute offset from byte 0 of the `.sng`**.

<a id="a5"></a>
### A5. Gaps, overlaps and ordering in FileData
- **Evidence:** The README only says "concatenated file sections"
  ([L132][readme-filedata]). The reference writer stores files contiguously and in index order
  ([SngSerializer.cs L317–329][ser-write-index]). The reader seeks to each offset on its own,
  so it would accept gaps and overlaps.
- **Chart-Packer:**
  - **Writes** files contiguously, in index order, starting at `50 + M + I`.
  - **Reading:**
    - A file range that extends outside `[fileDataStart, fileDataStart + fileDataLen)` is an
      **error** (SNG040).
    - Overlapping ranges are an **error** (SNG040).
    - A gap, or files stored out of index order, is a **warning** (SNG041).
    - A total that doesn't equal `fileDataLen` is a **warning** (SNG042).

<a id="a6"></a>
### A6. Trailing bytes after FileData
- **Evidence:** The reference reader only checks that the file is long *enough*
  ([SngSerializer.cs L110–116][ser-ensure]). The reference writer doesn't truncate existing
  files ([L363][ser-write-data]), so trailing bytes can appear in files it rewrites.
- **Chart-Packer:** Trailing bytes are a **warning** (SNG043). Chart-Packer always writes to a
  new temporary file and renames it into place, so it never produces them.

<a id="a7"></a>
### A7. Duplicate filenames and metadata keys
- **Evidence:** The README says nothing. The reference model keeps the first of two duplicate
  filenames ([SngFile.cs L17–23][file-addfile]) and the last of two duplicate metadata keys
  ([L123–129][file-setters]).
- **Chart-Packer:**
  - Duplicate filenames are an **error** (SNG032). "Duplicate" means compared **ignoring
    case**, because the files would collide on extraction to Windows or macOS.
  - Duplicate metadata keys are an **error** (SNG022), also compared **ignoring case**. They
    can't round-trip to a `song.ini`, and the reference INI parser matches keys without regard
    to case ([IniParser.cs L75][ini-load]).

<a id="a8"></a>
### A8. Strict vs. lenient booleans
- **Evidence:** The README says `True`/`False`, case-sensitive
  ([L167–173][readme-meta-values]). The reference implementation is more lenient in three
  places:
  - `SngFile.TryGetBool` uses `bool.TryParse`, which ignores case ([SngFile.cs L63–73][file-getbool]).
  - The INI parser accepts any integer and `true`/`false` in any case
    ([IniParser.cs L191–220][ini-bool]), a change made in commit [`1779d4c`][commit-bool] to
    match what Clone Hero accepts.
  - `KnownKeys` accepts `bool.TryParse` or `0`/`1` ([KnownKeys.cs L174–175][kk-validate]).
- **Chart-Packer:**
  - **Writes** only `True`/`False` for keys it knows are boolean.
  - When **reading or packing**, lenient forms are a **warning** (SNG060), and the metadata
    editor offers to normalise them.

<a id="a9"></a>
### A9. Empty metadata keys and values
- **Evidence:** The README says nothing. The reference writer drops any pair with an empty key
  or value ([SngSerializer.cs L230–242][ser-count-meta]).
- **Chart-Packer:**
  - An empty key is an **error** (SNG023).
  - An empty value is dropped when packing, with an **info** finding (SNG061), matching the
    reference behaviour. When found in an existing `.sng` it is a **warning** (SNG061).

<a id="a10"></a>
### A10. Filename case
- **Evidence:** Registered names are lowercase, and the reference encoder lowercases them
  ([README L267][readme-registry-files], [SngEncode.cs L385–388][enc-folder]). Nothing says
  what to do with a `.sng` that contains `Song.ogg`.
- **Chart-Packer:**
  - Matches names against the registry **ignoring case**.
  - A registered name that isn't lowercase is a **warning** (SNG054).
  - Packing always lowercases registered names. Unknown names are left as they are.

<a id="a11"></a>
### A11. Invalid UTF-8 and NUL bytes
- **Evidence:** SNG strings are UTF-8 with no NUL ([README L61][readme-types]). The reference
  reader replaces invalid sequences instead of failing ([SngSerializer.cs L154][ser-load-meta]).
- **Chart-Packer:** Invalid UTF-8, or a `0x00`, in any key, value or filename is an **error**
  (SNG013).

<a id="a12"></a>
### A12. "Below 31" vs `0x00–0x1F`
- **Evidence:** The README's wording "below 31" and its range `0x00 - 0x1F` disagree by one:
  `0x1F` is 31 ([L208][readme-filenames]).
- **Chart-Packer:** Forbids `0x00`–`0x1F` **inclusive**, following the explicit range.

<a id="a13"></a>
### A13. Subfolders: opt-in or default?
- **Evidence:** The README says the reference tool encodes folders "when explicitly enabled"
  ([L269][readme-registry-files]). At the pinned commit, the encoder always includes
  subfolders that aren't song folders ([SngEncode.cs L517–543][enc-subfolders]).
- **Chart-Packer:** Subfolders are **opt-in** when packing (a setting that is off by default).
  Files in subfolders are always **accepted** when reading.

<a id="a14"></a>
### A14. The reference exclusion lists don't take effect
- **Evidence:** See §9.4 ([SngEncode.cs L260–271][enc-exclude], [L344][enc-subfolder-exclude],
  [L522–523][enc-subfolders]).
- **Chart-Packer:** Leaves out `desktop.ini`, `.DS_Store`, `Thumbs.db`, `ps.dat`, `ch.dat`,
  `__MACOSX/` and nested `*.sng` **by name, ignoring case**, and shows each exclusion in the
  pack preview. `Thumbs.db` is a Chart-Packer addition.

<a id="a15"></a>
### A15. Metadata and filename rules the reference tool doesn't enforce
- **Evidence:** The reference tool checks neither the §6.1 characters nor the §7 filename rules
  (§9.1–9.2), and it truncates names over 255 bytes without warning (§9.2).
- **Chart-Packer:** Enforces all of these as **errors** when validating a `.sng` or a source
  folder, and refuses to pack until they are fixed:
  - SNG020, SNG021: forbidden metadata characters
  - SNG030: forbidden filename characters and reserved names
  - SNG031: names over 255 bytes

<a id="a16"></a>
### A16. Extraction safety
- **Evidence:** The reference decoder builds output paths straight from stored names
  ([SngDecode.cs L57][dec]).
- **Chart-Packer:**
  - Before writing any file during unpack, checks every segment against §7.
  - Resolves the full path and confirms it is inside the chosen output folder.
  - A name that fails either check is not extracted and is reported as an **error** (SNG033).

<a id="a17"></a>
### A17. `song.ini` stored as a file
- **Evidence:** `song.ini` is reserved and should not be stored ([README L273][readme-registry-files]).
- **Chart-Packer:** A stored `song.ini` is a **warning** (SNG053). Unpack still extracts it,
  but warns before letting it replace the `song.ini` generated from the metadata.

---

<a id="worked-example"></a>
## 11. Worked example

A minimal valid `.sng`, produced by a script that follows §3–§5:

- mask `10 11 12 … 1F`
- one metadata pair, `name = Test`
- one file, `notes.chart`, containing the ASCII bytes `ABC`

Here `M = 24`, `I = 36` and `D = 3`, so the file data starts at `50 + 24 + 36 = 110` (`0x6E`)
and the total size is 113 bytes.

```
Offset    00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F
00000000  53 4E 47 50 4B 47 01 00 00 00 10 11 12 13 14 15   SNGPKG | version=1 | xorMask…
00000010  16 17 18 19 1A 1B 1C 1D 1E 1F 18 00 00 00 00 00   …xorMask | metadataLen=24
00000020  00 00 01 00 00 00 00 00 00 00 04 00 00 00 6E 61   metadataCount=1 | keyLen=4 | "na
00000030  6D 65 04 00 00 00 54 65 73 74 24 00 00 00 00 00   me" | valueLen=4 | "Test" | fileMetaLen=36
00000040  00 00 01 00 00 00 00 00 00 00 0B 6E 6F 74 65 73   fileCount=1 | filenameLen=11 | "notes
00000050  2E 63 68 61 72 74 03 00 00 00 00 00 00 00 6E 00   .chart" | contentsLen=3 | contentsIndex=110…
00000060  00 00 00 00 00 00 03 00 00 00 00 00 00 00 51 52   …| fileDataLen=3 | masked data
00000070  53
```

Masking check for the file data:

| i | plain | `xorMask[i%16]` | `i & 0xFF` | key | masked |
|---|-------|-----------------|------------|-----|--------|
| 0 | `41` A | `10` | `00` | `10` | `51` |
| 1 | `42` B | `11` | `01` | `10` | `52` |
| 2 | `43` C | `12` | `02` | `10` | `53` |

Chart-Packer's tests include this file as a golden vector.

---

<a id="sources"></a>
## 12. Sources

All links point at `mdsitton/SngFileFormat`, commit `668379942a3790c1b47ddf56b71eb3a7ada449c2`
(MIT License, © Matthew Sitton), unless noted otherwise.

- Specification: [README.md](https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md)
- Reference library: [SngLib/SngFile.cs][file-model], [SngLib/SngSerializer.cs][ser]
- Reference CLI: [SngCli/SngEncode.cs][enc], [SngCli/SngDecode.cs][dec], [SngCli/KnownKeys.cs][kk]
- INI handling: [SongLib/IniParser.cs][ini]
- Lenient bool parsing change: commit [`1779d4c`][commit-bool]
- Metadata key registries (TheNathannator/GuitarGame_ChartFormats):
  [Typical Tags](https://github.com/TheNathannator/GuitarGame_ChartFormats/blob/main/doc/FileFormats/song.ini/Typical%20Tags.md),
  [Game-Specific Tags](https://github.com/TheNathannator/GuitarGame_ChartFormats/blob/main/doc/FileFormats/song.ini/Game-Specific%20Tags.md)

<!-- README permalinks (?plain=1 enables line anchors on markdown files) -->
[readme-intro]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L3
[readme-types]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L53-L67
[readme-overview]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L69-L85
[readme-header]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L90-L95
[readme-metadata]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L98-L103
[readme-metadatapair]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L105-L111
[readme-fileindex]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L113-L118
[readme-filemeta]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L120-L126
[readme-filedata]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L128-L137
[readme-masking]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L139-L150
[readme-meta-strings]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L151-L161
[readme-meta-values]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L163-L194
[readme-filenames]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L196-L236
[readme-design]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L238-L247
[readme-registry]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L249-L261
[readme-registry-files]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/README.md?plain=1#L263-L292

<!-- SngLib permalinks -->
[file-model]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngFile.cs
[file-version]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngFile.cs#L8-L15
[file-addfile]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngFile.cs#L15-L23
[file-getbool]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngFile.cs#L63-L73
[file-setters]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngFile.cs#L123-L129
[ser]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs
[ser-mask-lookup]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L20-L31
[ser-mask-apply]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L50-L108
[ser-ensure]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L110-L116
[ser-load]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L118-L215
[ser-load-header]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L122-L137
[ser-load-meta]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L139-L190
[ser-load-files]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L193-L211
[ser-count-meta]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L230-L242
[ser-meta-len]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L250-L265
[ser-file-len]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L267-L283
[ser-write-meta]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L292-L311
[ser-write-index]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L313-L331
[ser-save]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L333-L361
[ser-write-data]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngLib/SngSerializer.cs#L363-L377

<!-- SngCli / SongLib permalinks -->
[enc]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs
[enc-valid]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L57-L66
[enc-chart-meta]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L68-L197
[enc-parse-meta]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L199-L225
[enc-exclude]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L260-L271
[enc-audio]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L297-L310
[enc-subfolder-exclude]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L339-L347
[enc-folder]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L383-L480
[enc-mask]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L509-L512
[enc-subfolders]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngEncode.cs#L517-L543
[dec]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/SngDecode.cs#L18-L66
[kk]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/KnownKeys.cs
[kk-table]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/KnownKeys.cs#L10-L143
[kk-legacy]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/KnownKeys.cs#L147-L153
[kk-validate]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SngCli/KnownKeys.cs#L160-L289
[ini]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SongLib/IniParser.cs
[ini-load]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SongLib/IniParser.cs#L19-L88
[ini-save]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SongLib/IniParser.cs#L90-L110
[ini-section]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SongLib/IniParser.cs#L112-L133
[ini-bool]: https://github.com/mdsitton/SngFileFormat/blob/668379942a3790c1b47ddf56b71eb3a7ada449c2/SngTool/SongLib/IniParser.cs#L191-L220
[commit-bool]: https://github.com/mdsitton/SngFileFormat/commit/1779d4c4bf8fb293c6186055d310e2db0f10db8c
