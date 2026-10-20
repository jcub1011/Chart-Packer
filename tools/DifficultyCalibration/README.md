# DifficultyCalibration

Measures the five-fret difficulty model (`docs/difficulty-model.md`) against the `song.ini` ratings of a library, for
tuning its parameters. Never shipped; files are only read.

```
dotnet run -c Release --project tools/DifficultyCalibration -- "<library>" [--variant "label|Name=value,..."]...
    [--brief] [--fit] [--fit-on official] [--attributes] [--csv <file>] [--outliers N] [--song <text>] [--depth N]
```

The songs are read once, about ten seconds for 1,400. Each part rated in `song.ini` (`diff_guitar`, `diff_bass`,
`diff_rhythm`, `diff_guitar_coop`) is rated by the model on its highest charted difficulty, as
`DifficultyEstimator.EstimatePart` does. Songs present twice, as a folder and as a `.sng`, are counted once. The parts are
split by artist, 70% to fit and 30% held out, so no song is on both sides; choose changes on the fit set and check them
on the held-out one.

- **`--variant "label|Name=value,..."`** scores `DifficultyParameters.Default` with the named properties changed, beside
  the default. Numbers are invariant-culture; `RankThresholds` is written `0/8.5/12.5/...`. Give it several times.
- **Default output**, for each variant: per instrument, for all, official (Harmonix, Neversoft and the like, from the
  `charter` key) and community charts, fit and held out: the count, the Spearman correlation between rank and rated tier,
  how many are on their tier and within one, the mean tier error, and how many rated 5 or under rank 7 or more. Then the
  median rank and quartiles of each rated tier, and how many charts each attribute dominates.
- **`--brief`** prints one line per variant: the Spearman of guitar and bass, fit, held out and official, and the
  dominant-attribute counts. For sweeps.
- **`--attributes`** adds each attribute's own Spearman and its median for each rated tier.
- **`--fit`** refits the rank thresholds on the fit set and scores both sets with them: halfway between neighbouring
  tiers' median scores, the way §7.3 places them, and, for comparison, for the fewest tiers of error. It also prints the
  midpoints for guitar and bass apart. Tiers of fewer than five charts take the gap of the tier before.
- **`--fit-on official`** fits the thresholds to official charts only. The defaults are the fewest-error fit to all official
  charts (docs/difficulty-model.md §7.1 and §13).
- **`--csv <file>`** writes each part's rank, score and attribute scores under the last variant.
- **`--outliers N`** and **`--song <text>`** look inside the N parts furthest from their rating, and the songs whose
  path has the text, under the last variant: the shortest intervals, the hardest windows and what made them hard, and the
  notes of the hardest.
- `--depth N` sets how deep the library is searched (default 10, as `--difficulty-report`).
