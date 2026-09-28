# Third-party notices

Chart-Packer includes no third-party source code. The first part of this file credits the work
its implementation is based on. The second lists the libraries that are built into a release of
the app, with their licenses.

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
  for the key names and types in `ChartPacker.Core/Metadata/KnownKeys.cs`.

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
  `ChartPacker.Core/Metadata/KnownKeys.cs` takes its key names and value types from the Standard
  Tags and the Clone Hero, Phase Shift, Frets on Fire X and Editor on Fire tag lists. No text is
  copied; the key descriptions are Chart-Packer's own.

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
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | 0.11.4 | MIT | © 2021 Nikita Tsukanov |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | 0.92.0 | MIT | © Tom Deseyn |
| [SkiaSharp](https://github.com/mono/SkiaSharp), with its native assets | 3.119.4 | MIT, plus the notices below | © Microsoft Corporation |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp), with its native assets | 8.3.1.3 | MIT, plus the notices below | © Microsoft Corporation |
| [ATL.NET](https://github.com/Zeugma440/atldotnet) (`z440.atl.core`) | 7.18.0 | MIT | © 2017 Zeugma440 |
| [ANGLE](https://github.com/google/angle) (`Avalonia.Angle.Windows.Natives`) | 2.1.27548 | BSD 3-Clause | © 2018 The ANGLE Project Authors |
| [Inter](https://github.com/rsms/inter) typeface, embedded in `Avalonia.Fonts.Inter` | — | SIL OFL 1.1 | © 2016 The Inter Project Authors |
| [Ude](https://github.com/yinyue200/ude) (`Ude.NetStandard`), a dependency of ATL.NET | 1.2.0 | MPL 1.1 (or GPL 2.0+ / LGPL 2.1+) | The Ude and Mozilla contributors |

ATL.NET reads audio durations in `ChartPacker.Core/Scanning/AudioLength.cs`, the library list's
fallback when a song has no `song_length`.

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
