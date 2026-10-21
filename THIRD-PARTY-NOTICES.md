# Third-party notices

Chart-Packer includes no third-party source code. The first part of this file credits the work
its implementation is based on. The second credits the icons it uses. The third lists the
libraries that are built into a release of the app, with their licenses.

## Implementation references

### SngFileFormat

- **Project:** [mdsitton/SngFileFormat](https://github.com/mdsitton/SngFileFormat)
- **Pinned revision:** [`6683799`](https://github.com/mdsitton/SngFileFormat/tree/668379942a3790c1b47ddf56b71eb3a7ada449c2)
- **Used for:** the `.sng` format specification, and the reference implementation (SngLib and
  SngCli). Chart-Packer's code is written independently from
  [docs/sng-spec.md](docs/sng-spec.md). The reference implementation was consulted only to
  confirm behaviour such as field order, which checks the reader makes and how `song.ini` is
  parsed. Source files whose behaviour follows the reference name the upstream file in their
  documentation comments. The known-keys table in `SngCli/KnownKeys.cs` was one of the sources
  for the key names and types in `ChartShared.Core/Metadata/KnownKeys.cs`.

```
Copyright (c) 2023 Matthew Sitton

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the “Software”), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### GuitarGame_ChartFormats

- **Project:** [TheNathannator/GuitarGame_ChartFormats](https://github.com/TheNathannator/GuitarGame_ChartFormats)
- **License:** [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (public domain dedication)
- **Used for:** the `song.ini` tag lists that sng-spec §6.3 names as the metadata key registry.
  `ChartShared.Core/Metadata/KnownKeys.cs` takes its key names and value types from the Standard
  Tags and the Clone Hero, Phase Shift, Frets on Fire X and Editor on Fire tag lists. No text is
  copied; the key descriptions are Chart-Packer's own. It is also the reference for the
  `notes.mid` layout in [docs/charter-spec-v1.md](docs/charter-spec-v1.md) §7, which `MidiLayout`
  (`ChartShared.Core/Charts/MidiChartReader.cs`), `MidiChartLoader` and `MidiChartWriter`
  (`ChartShared.Core/Charting/`) follow. No text is copied there either.

### YARG.Core

- **Project:** [YARC-Official/YARG.Core](https://github.com/YARC-Official/YARG.Core)
- **Used for:** how the games read a chart. The `.chart` and MIDI readers, the drum and note
  conversion, and Chart Builder's loader and game view (`ChartShared.Core/Charts/` and
  `Charting/`) follow YARG's reading behaviour, observed from its source. No code is copied.
  Source files whose behaviour follows YARG name the upstream type in their documentation
  comments.

### Audio formats

The containers and headers that `ChartShared.Audio` reads itself are written from their
specifications; no code from any implementation of them is used.

- **Ogg:** [RFC 3533](https://www.rfc-editor.org/rfc/rfc3533), The Ogg Encapsulation Format
  (`Decoding/OggPageReader.cs`).
- **Ogg Opus:** [RFC 7845](https://www.rfc-editor.org/rfc/rfc7845), Ogg Encapsulation for the Opus
  Audio Codec: the identification header, pre-skip, output gain, end trimming and seek pre-roll
  (`Decoding/OpusSource.cs`).
- **MPEG audio frames:** the frame header of ISO/IEC 11172-3 and 13818-3 (`Decoding/MpegFrame.cs`).
- **Gapless MP3:** the Xing/Info frame and the
  [LAME tag](http://gabriel.mp3-tech.org/mp3infotag.html)'s encoder delay and padding, and
  [ID3v2](https://id3.org/id3v2.4.0-structure)'s tag header, to skip the tag
  (`Decoding/Mp3Source.cs`).
- **WAV:** the RIFF WAVE format, including `WAVE_FORMAT_EXTENSIBLE` (`Decoding/WavSource.cs`).

## Icons

### game-icons.net

- **Project:** [game-icons.net](https://game-icons.net) ([game-icons/icons](https://github.com/game-icons/icons))
- **License:** [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/)
- **Used for:** the instrument icons of the difficulties in the details panel, the lyrics
  lane's waveform button, the pitch correction button, and Chart Builder's Playtest button
  (`ChartShared.UI/Styles/Icons.axaml`, the `Instrument*Geometry`, `WaveformIconGeometry`,
  `PitchCorrectionIconGeometry` and `PlaytestIconGeometry` resources). The SVG paths are
  used unchanged, except that each command is written out in full so that Avalonia's path
  parser reads it. They're drawn at a smaller size, and recoloured.

| Icon | Author | Used as |
|---|---|---|
| [Guitar](https://game-icons.net/1x1/lorc/guitar.html) | [Lorc](https://lorcblog.blogspot.com) | Guitar, co-op, pro and GHL guitar |
| [Guitar bass head](https://game-icons.net/1x1/delapouite/guitar-bass-head.html) | [Delapouite](https://delapouite.com) | Bass, pro and GHL bass |
| [Guitar head](https://game-icons.net/1x1/delapouite/guitar-head.html) | [Delapouite](https://delapouite.com) | Rhythm, GHL rhythm |
| [Drum](https://game-icons.net/1x1/delapouite/drum.html) | [Delapouite](https://delapouite.com) | Drums |
| [Drum kit](https://game-icons.net/1x1/caro-asercion/drum-kit.html) | Caro Asercion | Pro drums |
| [Musical keyboard](https://game-icons.net/1x1/delapouite/musical-keyboard.html) | [Delapouite](https://delapouite.com) | Keys |
| [Piano keys](https://game-icons.net/1x1/delapouite/piano-keys.html) | [Delapouite](https://delapouite.com) | Pro keys |
| [Microphone](https://game-icons.net/1x1/delapouite/microphone.html) | [Delapouite](https://delapouite.com) | Vocals |
| [Musical notes](https://game-icons.net/1x1/delapouite/musical-notes.html) | [Delapouite](https://delapouite.com) | Harmonies |
| [Footsteps](https://game-icons.net/1x1/skoll/footsteps.html) | Skoll | Dance |
| [Three friends](https://game-icons.net/1x1/delapouite/three-friends.html) | [Delapouite](https://delapouite.com) | Band |
| [Sound waves](https://game-icons.net/1x1/skoll/sound-waves.html) | Skoll | Lane waveforms |
| [Tune pitch](https://game-icons.net/1x1/lorc/tune-pitch.html) | [Lorc](https://lorcblog.blogspot.com) | Pitch correction |
| [Gamepad](https://game-icons.net/1x1/delapouite/gamepad.html) | [Delapouite](https://delapouite.com) | Playtest |

### Lucide

- **Project:** [Lucide](https://lucide.dev) ([lucide-icons/lucide](https://github.com/lucide-icons/lucide)),
  release [1.61.0](https://github.com/lucide-icons/lucide/tree/1.61.0)
- **License:** ISC, below. The icons Lucide took from [Feather](https://github.com/feathericons/feather)
  (arrow-left, arrow-right, check, chevron-down, download, info, minus, plus, search, square,
  trash, type and x) are also under the [MIT](#mit-license) license © 2013-present Cole Bemis.
- **Used for:** the line icons on the buttons, lists and menus of both apps
  (`ChartShared.UI/Styles/Icons.axaml`, the `*Stroke` resources). Each SVG's shapes are joined
  into one path and scaled from Lucide's 24×24 grid to the app's 16×16 one; nothing else is
  changed. They're drawn in the text colour.

| Icon | Used as |
|---|---|
| [arrow-down-wide-narrow](https://lucide.dev/icons/arrow-down-wide-narrow) | Sort last to first; generate lower difficulties; Reduce |
| [arrow-left](https://lucide.dev/icons/arrow-left) | Shift notes down a lane |
| [arrow-left-right](https://lucide.dev/icons/arrow-left-right) | Lefty flip |
| [arrow-right](https://lucide.dev/icons/arrow-right) | Songs folder to output folder; shift notes up a lane |
| [arrow-right-left](https://lucide.dev/icons/arrow-right-left) | Replace a file |
| [arrow-right-to-line](https://lucide.dev/icons/arrow-right-to-line) | Sustain until the next note |
| [arrow-up-narrow-wide](https://lucide.dev/icons/arrow-up-narrow-wide) | Sort first to last |
| [audio-lines](https://lucide.dev/icons/audio-lines) | Audio, in the Merge window |
| [check](https://lucide.dev/icons/check) | Validate; ticks |
| [chevron-down](https://lucide.dev/icons/chevron-down) | Opens a menu |
| [clipboard-paste](https://lucide.dev/icons/clipboard-paste) | Paste |
| [clipboard-plus](https://lucide.dev/icons/clipboard-plus) | Paste and merge |
| [copy](https://lucide.dev/icons/copy) | Copy |
| [copy-x](https://lucide.dev/icons/copy-x) | Deduplicate |
| [disc-3](https://lucide.dev/icons/disc-3) | A `.sng` file |
| [disc-album](https://lucide.dev/icons/disc-album) | Album art |
| [download](https://lucide.dev/icons/download) | Download a file |
| [ellipsis](https://lucide.dev/icons/ellipsis) | More tools |
| [eye](https://lucide.dev/icons/eye) | Ghost difficulty; what's shown |
| [file-clock](https://lucide.dev/icons/file-clock) | Open recent |
| [file-pen-line](https://lucide.dev/icons/file-pen-line) | Edit chart |
| [file-plus](https://lucide.dev/icons/file-plus) | New chart |
| [flag](https://lucide.dev/icons/flag) | Coda |
| [fold-horizontal](https://lucide.dev/icons/fold-horizontal) | Make notes shorter |
| [fold-vertical](https://lucide.dev/icons/fold-vertical) | Pack into one folder |
| [folder](https://lucide.dev/icons/folder) | A song folder |
| [folder-open](https://lucide.dev/icons/folder-open) | Choose the songs folder; open; show in the file manager |
| [folder-output](https://lucide.dev/icons/folder-output) | Choose the output folder; export |
| [funnel](https://lucide.dev/icons/funnel) | Filter |
| [image](https://lucide.dev/icons/image) | An image |
| [info](https://lucide.dev/icons/info) | More information |
| [magnet](https://lucide.dev/icons/magnet) | Quantize |
| [merge](https://lucide.dev/icons/merge) | Merge |
| [midi-port](https://lucide.dev/icons/midi-port) | A MIDI chart |
| [minus](https://lucide.dev/icons/minus) | Less, e.g. zoom out |
| [move-right](https://lucide.dev/icons/move-right) | Sustain for one grid step |
| [package](https://lucide.dev/icons/package) | Pack |
| [package-open](https://lucide.dev/icons/package-open) | Unpack |
| [panel-left-close](https://lucide.dev/icons/panel-left-close) | Collapse a side panel |
| [pause](https://lucide.dev/icons/pause) | Pause |
| [play](https://lucide.dev/icons/play) | Play |
| [plus](https://lucide.dev/icons/plus) | Add |
| [refresh-cw](https://lucide.dev/icons/refresh-cw) | Rescan |
| [replace](https://lucide.dev/icons/replace) | If the output exists |
| [rotate-ccw](https://lucide.dev/icons/rotate-ccw) | Revert |
| [save](https://lucide.dev/icons/save) | Save |
| [scissors](https://lucide.dev/icons/scissors) | Cut |
| [search](https://lucide.dev/icons/search) | Search |
| [settings](https://lucide.dev/icons/settings) | Settings |
| [skip-back](https://lucide.dev/icons/skip-back) | Previous |
| [skip-forward](https://lucide.dev/icons/skip-forward) | Next |
| [square](https://lucide.dev/icons/square) | Stop |
| [square-dashed](https://lucide.dev/icons/square-dashed) | Select |
| [step-forward](https://lucide.dev/icons/step-forward) | Play from the preview start |
| [text-cursor](https://lucide.dev/icons/text-cursor) | Stretch notes to the cursor |
| [timer](https://lucide.dev/icons/timer) | Audio, video and input offsets; auto-set song length |
| [trash](https://lucide.dev/icons/trash) | Remove a file |
| [triangle-alert](https://lucide.dev/icons/triangle-alert) | A warning |
| [type](https://lucide.dev/icons/type) | Text event |
| [undo-2](https://lucide.dev/icons/undo-2) | Undo; mirrored, redo |
| [unfold-horizontal](https://lucide.dev/icons/unfold-horizontal) | Make notes longer |
| [video](https://lucide.dev/icons/video) | A video |
| [volume-2](https://lucide.dev/icons/volume-2) | Audio |
| [wallpaper](https://lucide.dev/icons/wallpaper) | A background |
| [wand-sparkles](https://lucide.dev/icons/wand-sparkles) | Transform notes |
| [wrench](https://lucide.dev/icons/wrench) | Quick fix; fix a finding |
| [x](https://lucide.dev/icons/x) | Close, clear or remove |

```
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

### Bootstrap Icons

- **Project:** [Bootstrap Icons](https://icons.getbootstrap.com) ([twbs/icons](https://github.com/twbs/icons))
- **License:** [MIT](#mit-license) © The Bootstrap Authors
- **Used for:** the program icons of Chart Packer and Chart Builder, and the icon of `.sng` files
  without album art. The SVGs are kept unchanged in `tools/IconGen/Sources`. `tools/IconGen` fills
  each one in a single colour, and saves them as the `.ico` files below.

| Icon | Used as |
|---|---|
| [box-seam-fill](https://icons.getbootstrap.com/icons/box-seam-fill/) | Chart Packer (`ChartPacker/Assets/ChartPacker.ico`) |
| [bar-chart-line-fill](https://icons.getbootstrap.com/icons/bar-chart-line-fill/) | Chart Builder (`ChartBuilder/Assets/ChartBuilder.ico`) |
| [disc-fill](https://icons.getbootstrap.com/icons/disc-fill/) | `.sng` files (`installer/SngFile.ico`) |

## Bundled libraries

A release is a single native executable (.NET Native AOT) plus the native libraries below it
needs. Packages used only to build or test (MSTest, the ILCompiler and ILLink tasks,
`Avalonia.BuildServices`, and `AvaloniaUI.DiagnosticsSupport`, which is referenced in Debug
builds only) aren't part of a release and aren't listed.

| Component | Version | License | Copyright |
| --------- | ------- | ------- | --------- |
| [.NET runtime and libraries](https://github.com/dotnet/runtime) | 10.0 | MIT | © .NET Foundation and Contributors |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia): `Avalonia`, `.Desktop`, `.Themes.Fluent`, `.Fonts.Inter`, `.Skia`, `.HarfBuzz`, `.Win32`, `.X11`, `.Native`, `.FreeDesktop`, `.FreeDesktop.AtSpi`, `.Remote.Protocol` | 12.0.5 | MIT | © AvaloniaUI OÜ and The AvaloniaUI Project |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.1 | MIT | © .NET Foundation and Contributors |
| [Serilog](https://github.com/serilog/serilog) | 4.4.0 | Apache 2.0 | © Serilog Contributors |
| [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | 7.0.0 | Apache 2.0 | © Serilog Contributors |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | 0.11.4 | MIT | © 2021 Nikita Tsukanov |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | 0.92.0 | MIT | © Tom Deseyn |
| [SkiaSharp](https://github.com/mono/SkiaSharp), with its native assets | 3.119.4 | MIT, plus the notices below | © Microsoft Corporation |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp), with its native assets | 8.3.1.3 | MIT, plus the notices below | © Microsoft Corporation |
| [ATL.NET](https://github.com/Zeugma440/atldotnet) (`z440.atl.core`) | 7.18.0 | MIT | © 2017 Zeugma440 |
| [ANGLE](https://github.com/google/angle) (`Avalonia.Angle.Windows.Natives`) | 2.1.27548 | BSD 3-Clause | © 2018 The ANGLE Project Authors |
| [Inter](https://github.com/rsms/inter) typeface, embedded in `Avalonia.Fonts.Inter` | — | SIL OFL 1.1 | © 2016 The Inter Project Authors |
| [Ude](https://github.com/yinyue200/ude) (`Ude.NetStandard`), a dependency of ATL.NET | 1.2.0 | MPL 1.1 (or GPL 2.0+ / LGPL 2.1+) | The Ude and Mozilla contributors |
| [NVorbis](https://github.com/NVorbis/NVorbis) | 1.0.0-rc.2 | MIT | © 2020 Andrew Ward |
| [NLayer](https://github.com/naudio/NLayer) | 3.0.0 | MIT | © 2018 Mark Heath, Andrew Ward & Contributors |
| [Concentus](https://github.com/lostromb/concentus) | 2.2.2 | BSD 3-Clause (the Opus license) | © Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation and others |
| [ppy/SDL3-CS](https://github.com/ppy/SDL3-CS) (`ppy.SDL3-CS`) | 2026.722.0 | MIT | © 2024 ppy Pty Ltd |
| [SDL](https://github.com/libsdl-org/SDL), the native library in `ppy.SDL3-CS` | 3.5.0 | zlib | © 1997-2026 Sam Lantinga |
| [FFmpeg](https://ffmpeg.org): `libavformat`, `libavcodec`, `libavutil`, `libswscale`, `libswresample` | 9.0 | LGPL 2.1 or later | © 2000-2026 the FFmpeg developers |

ATL.NET reads audio durations in `ChartShared.Core/Scanning/AudioLength.cs`, the library list's
fallback when a song has no `song_length`.

Serilog writes both apps' logs, and Serilog.Sinks.File writes them to daily files in the per-user
log folder (`ChartShared.UI/AppLog.cs`).

For audio playback (`ChartShared.Audio`), FFmpeg's decoders decode Vorbis, Opus and MPEG audio
packets when its libraries are present. Otherwise NVorbis decodes Vorbis, NLayer decodes MPEG audio
frames, and Concentus decodes Opus packets, all in managed code. SDL plays the mixed result; only
its audio subsystem is used. Its native library (`SDL3.dll`, `libSDL3.so` or `libSDL3.dylib`)
ships beside the executable.

For background video (`ChartShared.Video`), FFmpeg's shared libraries decode the video and
scale its frames. They ship beside the executable, unmodified, as separate files; see
[FFmpeg](#ffmpeg) below.

### MIT License

This text applies to every component above marked MIT, each under its own copyright line.

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Apache License 2.0

This text applies to every component above marked Apache 2.0, each under its own copyright line.
Neither Serilog package ships a NOTICE file.

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS
```

### SkiaSharp and HarfBuzzSharp native libraries

`libSkiaSharp` and `libHarfBuzzSharp` are built from Skia and HarfBuzz and incorporate further
third-party code: ANGLE, HarfBuzz, Skia, etc1, gif, libpng, the DNG SDK, expat, FreeType, ICU,
imgui, jsoncpp, libjpeg-turbo, libwebp, libmicrohttpd, piex, SDL, sfntly, SPIR-V Headers,
SPIR-V Tools and zlib. Their notices and licenses are reproduced in full in the
`THIRD-PARTY-NOTICES.txt` that ships in both native-asset packages
(`SkiaSharp.NativeAssets.*` 3.119.4 and `HarfBuzzSharp.NativeAssets.*` 8.3.1.3; the two files are
identical).

The .NET runtime's own third-party notices are in the
[dotnet/runtime repository](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).

### ANGLE

`av_libglesv2.dll`, from `Avalonia.Angle.Windows.Natives`.

```
// Copyright 2018 The ANGLE Project Authors.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
//     Redistributions of source code must retain the above copyright
//     notice, this list of conditions and the following disclaimer.
//
//     Redistributions in binary form must reproduce the above
//     copyright notice, this list of conditions and the following
//     disclaimer in the documentation and/or other materials provided
//     with the distribution.
//
//     Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
//     Ltd., nor the names of their contributors may be used to endorse
//     or promote products derived from this software without specific
//     prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
// FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
// COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
// BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
// LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
// ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
```

### Inter

The Inter typeface, embedded in `Avalonia.Fonts.Inter` and used as the app's font.

```
Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL

-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION AND CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

### Ude

Used unmodified, as a dependency of ATL.NET. Chart-Packer uses it under the
[Mozilla Public License 1.1](https://www.mozilla.org/en-US/MPL/1.1/). The project may alternatively
be used under the GNU GPL 2.0 or later, or the GNU LGPL 2.1 or later. Its source code is available
from [yinyue200/ude](https://github.com/yinyue200/ude).

### Concentus

```
Copyright (c) by various holding parties, including (but not limited to):
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### SDL

```
Copyright (C) 1997-2026 Sam Lantinga <slouken@libsdl.org>

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

### FFmpeg

`avformat`, `avcodec`, `avutil`, `swscale` and `swresample`, from FFmpeg 9.0, ship as shared
libraries beside the executable (`avcodec-63.dll`, `libavcodec.so.63` or `libavcodec.63.dylib`, and
so on). They decode background video, and audio packets.
Chart-Packer uses them under the
[GNU Lesser General Public License, version 2.1 or later](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html);
no GPL or non-free parts of FFmpeg are enabled. They're built, unmodified, from the `n9.0.2` tag of
the [FFmpeg source](https://git.ffmpeg.org/ffmpeg.git) by
[build/ffmpeg/build.sh](build/ffmpeg/build.sh), which gives the full configure line. They can be
replaced with any other build of the same major versions. Chart-Packer's bindings to them
(`ChartShared.Ffmpeg/Interop/Native.cs`) are written from FFmpeg's public headers; no FFmpeg code
is copied.

```
FFmpeg is free software; you can redistribute it and/or
modify it under the terms of the GNU Lesser General Public
License as published by the Free Software Foundation; either
version 2.1 of the License, or (at your option) any later version.

FFmpeg is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
Lesser General Public License for more details.
```
