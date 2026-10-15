# IconGen

Renders the program icons from the Bootstrap Icons sources in `Sources/`. Never shipped.

```
dotnet run --project tools/IconGen
```

Each source is drawn as it is, filled in a single colour, at 16 to 256 px, and written over its icon. The
colours are darker shades of the red, orange and blue frets. They aren't the app's theme colours, so a restyle
doesn't change them:

| Source | Colour | Icon | Used for |
|---|---|---|---|
| `box-seam-fill.svg` | `#B22222` | `ChartPacker/Assets/ChartPacker.ico` | Chart Packer |
| `bar-chart-line-fill.svg` | `#D2670A` | `ChartBuilder/Assets/ChartBuilder.ico` | Chart Builder |
| `disc-fill.svg` | `#1E64B4` | `installer/SngFile.ico` | `.sng` files without album art |

The sources are from [Bootstrap Icons](https://icons.getbootstrap.com) ([twbs/icons](https://github.com/twbs/icons)),
unchanged; see `THIRD-PARTY-NOTICES.md`. Commit the regenerated `.ico` files with any change here.
