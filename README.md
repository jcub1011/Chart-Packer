# Chart-Packer
A GUI application used to pack and unpack charts for Clone Hero, YARG, and other rhythm games that use the .SNG format.

It installs with **Chart Builder**, a separate app for creating and editing charts
([docs/charter-spec-v1.md](docs/charter-spec-v1.md)). The two share code and one installer.

## Releasing

1. Set `<Version>` in `Directory.Build.props`. Both apps share it. The release is tagged `v<Version>`.
2. Run **Actions → Release**. It builds FFmpeg (cached between runs), runs the tests, publishes
   both apps into one folder, and attaches an installer (`ChartPacker-<version>-win-x64-setup.exe`) and a portable zip to
   a new GitHub release. Both include FFmpeg, so users don't need to install anything else. Options:
   - **prerelease** marks the release as a pre-release.
   - **overwrite** replaces an existing release and tag for this version. Without it, the run fails
     straight away if the version has already been released.
   - **draft** creates no release. The installer and zip are uploaded as an artifact of the run instead.

Releases aren't code-signed, so Windows SmartScreen shows "Windows protected your PC" the first time
the installer runs. Choose **More info → Run anyway**.

To build the installer locally, install [Inno Setup](https://jrsoftware.org/isinfo.php) 7 or later, then run
`pwsh build/publish.ps1` followed by `iscc installer/ChartPacker.iss`. The output goes to `dist/`.

## Acknowledgements

The `.sng` format is defined by [mdsitton/SngFileFormat](https://github.com/mdsitton/SngFileFormat)
(MIT License, (c) Matthew Sitton). Chart-Packer is an independent implementation that follows its
specification and matches its reference tools' behaviour. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details and the license text.
