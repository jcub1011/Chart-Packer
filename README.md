# About

Chart Packer is a small suite of tools for building and managing song charts for Clone Hero and YARG. It supports folders and `.sng` files.

## Chart Builder

### Chart a Wide Variety of Instruments

Chart `.mid` files with 5-fret, 6-fret, and drum parts.

![5-fret, 6-fret, and drum note lanes](<screenshots/5 Fret, 6 Fret, and Drum Demo.webp>)

### Support for Multi-Stem Audio

Supports adding individual instrument stems to a chart.

![Multi-stem track list in Chart Builder](<screenshots/Multi-stem Demo.webp>)

### Lower Difficulty Generation

Quickly create good starting points for charting lower difficulties.

![Difficulty generator with reduction options](<screenshots/Difficulty Reduction Demo.webp>)

### Star Power Phrase Generation

Easily create star power phrases for the whole track.

![Automatic star power phrase generation](<screenshots/Auto Star Power Generation Demo.webp>)

### Waveform Visualization

See audio waveforms and spectrograms while charting.

![Waveform and spectrogram lanes beside the chart grid](<screenshots/Waveform Visualization Demo.webp>)

### Simple Export Management

Export projects to the `.sng` format within the app, with automatic transcoding.

![Export screen with automatic transcoding](<screenshots/Auto Transcode Demo.webp>)

## Chart Packer

Point it at a folder, and it finds your songs: `.sng` files, plus folders containing a `song.ini`.

### Inspect Songs

Inspect song metadata, notes, artwork, and more.

![Song preview with metadata and artwork](<screenshots/Preview Demonstration.webp>)

### Bulk Packing & Unpacking

You can pack multiple folders into the `.sng` format and unpack `.sng` files into folders.

![Bulk pack progress](<screenshots/Bulk Pack Demo.webp>)

### Bulk Metadata Editing

Edit metadata across many songs at once, or individually.

![Bulk metadata edit preview](<screenshots/Bulk Edit Demo.webp>)

### Chart Validation

Validate charts to get a report of issues, with quick fixes for common ones.

![Chart validation results](<screenshots/Validation Demo.webp>)

### Chart Merging

Merge multiple charts into one chart, picking the best of what each chart has to offer.

![Song merge comparison](<screenshots/Song Merge Demo.webp>)

### Library Deduplication

Deduplicate your entire library to weed out accidental duplicate downloads.

![Library deduplication results](<screenshots/Deduplication Demo.webp>)

### Bulk Generate Easier Song Difficulties

Generate lower difficulties for songs across your entire library.

![Bulk difficulty reduction screen](<screenshots/Bulk Difficulty Reduction Generation.webp>)

### Library Optimization

Make your library smaller. Recompress audio to Opus, background videos to VP8 WebM, and album art and background images to JPEG, mix stems down, and clean up metadata no game needs. Estimate the savings from a sample first, then optimize the selected songs or the whole library. Optimizing in place can't be undone, so back up your charts first or write the changed charts to an output folder. See the [Optimize Library help page](docs/help/Optimize.md).

## Good to Know

- Chart Builder opens `.sng` files directly and converts `.chart` to `.mid` on save. Back up the original first, because that conversion loses information.
- Lower difficulties are generated as drafts. Treat them as a starting point, not a finished chart.
- The installer isn't code-signed. Windows SmartScreen shows "Windows protected your PC" the first time; choose **More info → Run anyway**.

## Building from Source

Install [Inno Setup](https://jrsoftware.org/isinfo.php) 7 or later, then run:

```powershell
pwsh build/publish.ps1
iscc installer/ChartPacker.iss
```

The output goes to `dist/`.

## Acknowledgments

The `.sng` format is defined by [mdsitton/SngFileFormat](https://github.com/mdsitton/SngFileFormat)
(MIT License, © Matthew Sitton). Chart-Packer is an independent implementation that follows its
specification and matches the behavior of its reference tools. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details and the license text.