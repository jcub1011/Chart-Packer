# DifficultyCalibration

Measures the difficulty model (`docs/difficulty-model.md`) against the `song.ini` ratings of a library, for tuning its
parameters. Never shipped; files are only read.

```
dotnet run -c Release --project tools/DifficultyCalibration -- "<library>" [--model <model>] [--variant "label|Name=value,..."]...
    [--brief] [--fit] [--fit-on official] [--attributes] [--csv <file>] [--outliers N] [--song <text>] [--depth N]
```

The songs are read once, about ten seconds for 1,400. Each part rated in `song.ini` is rated on its highest charted
difficulty, as `DifficultyEstimator.EstimatePart` does, by the model that rates its key. Pro drums are a part for
`diff_drums`, rated as 4-lane drums, and another for `diff_drums_real`, rated with the cymbals. Songs present twice, as a
folder and as a `.sng`, are counted once. The parts are split by artist, 70% to fit and 30% held out, so no song is on
both sides; choose changes on the fit set and check them on the held-out one.

- **`--model <model>`** picks the parts measured, and the parameters the variants start from
  (`DifficultyParameters.For`):
  - `fivefret`, the default: guitar, bass, rhythm and co-op;
  - `keys`;
  - `sixfret`: the GHL parts;
  - `drums`: `diff_drums`;
  - `prodrums`: `diff_drums_real`, which also says how often its tier is the same chart's 4-lane tier.
- **`--variant "label|Name=value,..."`** scores the model's parameters with the named properties changed, beside the
  default. Numbers are invariant-culture; `RankThresholds` is written `0/8.5/12.5/...`, and true or false, or an enum's
  name, set the others. Drums' kick and hands parameters can be named as such: `KickWeight` is `StrumWeight` and
  `HandsReferenceBpm` is `NoteReferenceBpm`. Give it several times.
- **Default output**, for each variant: per rating, for all, official (Harmonix, Neversoft and the like, from the
  `charter` key) and community charts, fit and held out: the count, the Spearman correlation between rank and rated tier,
  how many are on their tier and within one, the mean tier error, and how many rated 5 or under rank 7 or more. Then the
  median rank and quartiles of each rated tier, and how many charts each attribute dominates.
- **`--brief`** prints one line per variant: the Spearman of each rating (up to four), fit, held out and official, the
  official charts' tier accuracy, and the dominant-attribute counts. For sweeps.
- **`--attributes`** adds each attribute's own Spearman and its median for each rated tier.
- **`--fit`** refits the rank thresholds on the fit set and scores both sets with them: halfway between neighbouring
  tiers' median scores, the way §7.3 places them, and, for comparison, for the fewest tiers of error. It also prints the
  midpoints for each rating apart. Tiers of fewer than five charts take the gap of the tier before.
- **`--fit-on official`** fits the thresholds to official charts only. Five-fret's, drums' and Pro drums' are the
  fewest-error fit to official charts, and keys' to every rated keys chart (docs/difficulty-model.md §7.1, §13 and §14.4).
- **`--csv <file>`** writes each part's rank, score and attribute scores under the last variant.
- **`--outliers N`** and **`--song <text>`** look inside the N parts furthest from their rating, and the songs whose
  path has the text, under the last variant: the shortest intervals, the hardest windows and what made them hard, and the
  notes of the hardest.
- `--depth N` sets how deep the library is searched (default 10, as `--difficulty-report`).
