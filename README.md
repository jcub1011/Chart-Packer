# Chart Packer
A GUI application used to build, pack, and unpack charts for Clone Hero, YARG, and other rhythm games.

Releases aren't code-signed, so Windows SmartScreen shows "Windows protected your PC" the first time
the installer runs. Choose **More info → Run anyway**.

To build the installer locally, install [Inno Setup](https://jrsoftware.org/isinfo.php) 7 or later, then run
`pwsh build/publish.ps1` followed by `iscc installer/ChartPacker.iss`. The output goes to `dist/`.

## Features

### Chart Packer

- Chart metadata editing
- Chart merging
- Chart validation
- Library deduplication
- Chart conversion to and from the `.sng` format

### Chart Builder

- Make charts with the `.mid` format
- Auto-difficulty generation
- Waveform visualization
- Audio transcoding to `.opus`
- Support for 5 fret and 6 fret instruments + drums
- Auto star-power generation

## Acknowledgements

The `.sng` format is defined by [mdsitton/SngFileFormat](https://github.com/mdsitton/SngFileFormat)
(MIT License, (c) Matthew Sitton). Chart-Packer is an independent implementation that follows its
specification and matches its reference tools' behaviour. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details and the license text.
