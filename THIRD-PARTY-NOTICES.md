# Third-party notices

Chart-Packer includes no third-party source code. The notices below credit the work its
implementation is based on.

## SngFileFormat

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

## GuitarGame_ChartFormats

- **Project:** [TheNathannator/GuitarGame_ChartFormats](https://github.com/TheNathannator/GuitarGame_ChartFormats)
- **License:** [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (public domain dedication)
- **Used for:** the `song.ini` tag lists that sng-spec §6.3 names as the metadata key registry.
  `ChartPacker.Core/Metadata/KnownKeys.cs` takes its key names and value types from the Standard
  Tags and the Clone Hero, Phase Shift, Frets on Fire X and Editor on Fire tag lists. No text is
  copied; the key descriptions are Chart-Packer's own.
