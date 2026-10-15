# IconGen

Renders the program icons from the Material Symbols sources in `Sources/`. Never shipped.

```
dotnet run --project tools/IconGen
```

Each source is drawn in the accent colour (`#8B5CF6`) with a dark outline, at 16 to 256 px, and written over:

| Source | Icon | Used for |
|---|---|---|
| `inventory_2_fill1_48px.svg` | `ChartPacker/Assets/ChartPacker.ico` | Chart Packer |
| `hardware_fill1_48px.svg` | `ChartBuilder/Assets/ChartBuilder.ico` | Chart Builder |
| `album_fill1_48px.svg` | `installer/SngFile.ico` | `.sng` files without album art |

The sources are the rounded, filled Material Symbols from
[google/material-design-icons](https://github.com/google/material-design-icons) (`symbols/web/<name>/materialsymbolsrounded/`),
unchanged; see `THIRD-PARTY-NOTICES.md`. Commit the regenerated `.ico` files with any change here.
