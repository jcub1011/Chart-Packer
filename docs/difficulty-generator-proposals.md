# Learned Difficulty Generation — Proposals

| | |
| --- | --- |
| **Status** | Proposal, under discussion |
| **Scope** | Replacing the hand-tuned weights of Reduce ([C9](charter-spec-v1.md#c9-difficulty-authoring)) with a model trained on official charts, keeping `Reducer` as the fallback |
| **Companion docs** | [improved-difficulty-generator-spec.md](improved-difficulty-generator-spec.md) (the plan these proposals led to), [charter-spec-v1.md](charter-spec-v1.md) (C9, Reduce), [roadmap.md](roadmap.md#improved-reduce-90) (Improved Reduce), [difficulty-model.md](difficulty-model.md) (the difficulty rating, used here as an evaluation metric) |
| **Training data** | Official charts by Harmonix, Neversoft, FreeStyleGames, Vicarious Visions, Beenox and WaveGroup, collected privately outside the repository. None of it is committed. |

---

## 1. The problem

Reduce drafts Hard, Medium and Easy from Expert. Its drafts lose the **character** of the Expert
chart: syncopations and rhythmic figures flatten into notes on the beat, and the result plays
mechanically. The goal is lower difficulties that keep the rhythm and the feel of the Expert part,
and that are harder for harder songs: the Easy of a tier-6 song should be harder than the Easy of a
tier-1 song.

### 1.1 Why the current Reducer loses character

`Reducer.Scores` (`ChartShared.Core/Charting/Authoring/Reducer.cs`) gives each chord a score
fixed before any decision is made. Most of the score is **beat strength**, with bonuses for
sustains, chords, the first chord after a rest, phrase edges, direction changes and anticipations
(`ThinningWeights`). `Reducer.Thin` then keeps chords **greedily, best score first**, wherever the
spacing allows.

Two things follow:

1. **Survivors drift onto the strong beats.** Downbeats are taken first and use up the spacing
   budget, so off-beat chords rarely fit afterwards.
2. **Nothing judges the rhythm the kept chords form together.** A chord's score never depends on
   which of its neighbours were kept. The spacing check asks whether a chord is too close, never
   whether the figure it makes is a good one.

The missing context is on the **output** side (what the reduced chart is doing around a chord),
more than the input side. A replacement has to score **sequences of decisions**, not chords one at
a time. The study (§5) confirms this and shows the context needed spans several beats.

### 1.2 Framing

Official lower difficulties are subsets of Expert: 99% of their chords are on an Expert onset
(§5.1). Generation is therefore **labelling**. For each beat Expert plays in, choose which of its
onsets to keep, then voice the kept chords (frets and width), then settle attributes (sustain,
HOPO, tap).

### 1.3 Shared design choices

These apply to every option below.

- **The beat is the unit.** Each decision is the pattern of Expert's onsets kept in one beat
  (§2.1). Sequences are then about 600 beats a song rather than about 1,500 chords, and each choice
  is among a handful of patterns.
- **Rhythm first, voicing second.** Choosing the rhythm is where character is lost (§5.2–§5.3).
  Voicing (which frets, chord width) starts from the existing `Reducer` logic, which already keeps
  contour more strictly than official charts do (§5.5), and is learned later if needed.
- **Conditioning.** Target difficulty and studio are inputs. Studio is worth a lot on Easy (§5.9).
  Tier is an input too, but the study finds it adds little once the Expert beat is known (§5.7,
  §5.9). Harder songs get harder Easy charts mostly because their Expert is harder, and a faithful
  reduction carries that through. A separate "budget" model for density, proposed earlier, isn't
  needed: Easy and Medium density barely changes with tier.
- **Hard limits.** Playability is never learned. Lane range, chord size and minimum spacing are
  constraints in the decoder, as `FretReductionRules` has them now, so every draft is playable.
- **Riffs.** Measures charted the same in Expert are reduced the same, as `ShareRiffScores` does now.
- **Same output.** The model returns a `ReductionResult`, so the Reduce preview, diff and edit flow
  work unchanged, and `Reducer` remains the fallback. It is used when the model is missing, when a
  part family has too little training data, or when a draft fails a quality check (an empty phrase,
  or a density far from the official charts').

---

## 2. Options

### 2.1 Beat-pattern decoding (the structure every option shares)

For each beat Expert plays in, its onsets are written as a pattern in counting notation: `1 e & a`
for sixteenths, `t T` for triplets. A lower difficulty keeps a subset of them, or nothing (`-`).
Four patterns cover about 95% of official Easy beats: `1` (53%), `-` (29%), `&` (11%) and `1 &`
(3%). Medium and Hard are similar, with a few more.

A **scorer** gives the probability of each candidate pattern for a beat, given its context. A
**decoder** (Viterbi, the standard dynamic-programming search for the best sequence) then picks the
sequence of patterns with the highest total probability over the whole song. It does this inside
the hard limits, with riffs tied. Because each beat's probability depends on the patterns chosen
before it, the decoder can trade a downbeat now against a better figure over the next beats. That
is the judgement greedy thinning can't make.

The options below differ only in the scorer.

### 2.2 Count-based scorer

The scorer is a table of counts from the official charts.

**Training is counting.** For every beat in the training songs, the model records the **context**
and the **outcome**:

- the context is what's known about the beat: the Expert pattern there, the patterns kept on the
  previous one to three beats, the next beat's Expert pattern, and the studio;
- the outcome is the pattern the official chart kept.

Each distinct context gets a row of counts, one per outcome. Dividing by the row's total gives
probabilities. For example (illustrative numbers): in the context "Expert plays `1 &`, the previous
two Easy beats kept `1` and `-`", official Easy kept `1` 520 times, `-` 210 times and `&` 70 times.
That gives 65%, 26% and 9%.

**Rare contexts back off.** The more features a context has, the more rows there are and the fewer
beats each one has seen. Each row's probabilities are blended with those of the shorter context (one
feature fewer), weighted by how much data the row has. A context seen thousands of times trusts its
own counts; one seen twice leans almost entirely on the shorter one. The chain ends at the Expert
pattern alone, then the overall pattern frequencies. The prototype in §5.9 uses Witten–Bell
smoothing for this blend.

**Generating is decoding.** The decoder (§2.1) keeps the last few chosen patterns as its state,
because the scorer's context includes them. With three previous beats and about ten realistic
patterns per beat, that is about a thousand states a beat, which is trivial for 600 beats.

**Pros**

- The simplest system that scores sequences: no training procedure beyond counting, no neural
  framework, all of it in C#.
- Fully interpretable: every choice traces back to how often official charts made it in that context.
- Learns from the data what greedy thinning gets wrong: rests, off-beats played in place of the
  downbeat, and the figures they make over several beats.
- Measured already as a prototype (§5.9). Predicting held-out official Easy beats, it is right 74% of
  the time, against the Reducer's 62%.

**Cons**

- **Contexts don't share what they learn.** Each context is its own row. "`1 &` after `1 -`" and
  "`1 &` after `1 &`" learn nothing from each other, however similar. The number of rows grows
  multiplicatively with each feature, so sparsity sets in quickly. On Hard, a third previous beat
  already makes the predictions slightly *worse* (§5.9).
- Continuous features (sustain length, tempo, phrase position, a riff's repeat number) must be
  bucketed, and each bucket multiplies the rows again.
- It sees only the features in its contexts.

### 2.3 Gradient-boosted trees as the scorer

Trees (LightGBM or similar) predict the probability of each candidate pattern from engineered
features:

- the Expert pattern and its neighbours;
- the previous kept patterns, as categorical features;
- beat position, gaps, phrase edges, sustains, star power, riff membership;
- studio, tier and target.

Most of the input side is computed today by `Reducer.Scores`, `Reducer.Phrases` and
`ShareRiffScores`.

**Pros**

- Generalises across similar contexts, and handles continuous features natively, where counts can't.
- Interpretable. Per-feature explanations (SHAP values) say why a pattern was chosen, which helps
  tuning and could be shown in the Reduce preview.
- Trains in minutes on a CPU. Ships as a few MB of trees, evaluated by about 100 lines of C#, with no
  native dependency.

**Cons**

- Sequential context arrives only through features someone writes: "previous pattern", "pattern two
  beats back", and so on. The study shows useful context reaching at least three beats back and one
  ahead (§5.9), so that's many hand-made history features.
- Feature engineering is ongoing work.

### 2.4 Small BiGRU or TCN with a CRF as the scorer

A small bidirectional GRU (a recurrent network) or a temporal convolutional network reads the
sequence of Expert beat patterns, plus per-beat features. A linear-chain **CRF** (conditional random
field) layer then scores whole sequences of patterns. Viterbi decodes it inside the hard limits.

**Pros**

- Reads context in **both directions** over many beats without hand-made history features. That is
  the shape §5.9 finds: three previous beats and the next Expert beat each add information.
- Shares what it learns between similar contexts, so it doesn't suffer the count model's sparsity.
- The CRF learns the transitions between patterns jointly with the encoder.
- Still small: about 100–500k parameters, a file of 1–2 MB, a fraction of a second per part on a CPU.
  About 1.75 million training beats per target is ample for a model this size.

**Cons**

- Python (PyTorch) training, more tuning, and variation between runs.
- Inference in the app is a hand-written C# GRU, or ONNX Runtime (a native library of about
  10–15 MB per platform, AOT compatibility to be verified).
- Harder to explain why a choice was made.
- A GRU's memory fades over tens to a low hundreds of beats. Riffs and sections still come from
  features or the decoder.

### 2.5 Transformer (considered, deferred)

A small bidirectional transformer over the beat tokens learns long-range structure itself. It is the
most data-hungry option, and benefits from pre-training (masked-pattern modelling on community Expert
charts). The beat framing and riff tying remove most of its advantage over §2.4. It stays an option
if §2.4 plateaus on long-range errors.

### 2.6 Comparison

| | Counts | Trees | BiGRU/TCN + CRF | Transformer |
| --- | --- | --- | --- | --- |
| Data needed | Grows with every feature | Hundreds to low thousands of songs | Low thousands | Thousands+, likes pre-training |
| Context across beats | Only what's in the context (sparse past ~3 beats) | Hand-made history features | Learned, both directions | Learned, whole song |
| Generalises between similar contexts | No | Yes | Yes | Yes |
| Continuous features | Bucketed | Yes | Yes | Yes |
| Interpretability | Complete | High | Low | Low |
| Training | Counting | Minutes, CPU | Under an hour, GPU helps | Hours, GPU |
| Model size | Tens of MB of counts | A few MB | 1–2 MB | 5–20 MB |
| In the app | Lookups | ~100 lines of C# | C# GRU or ONNX Runtime | ONNX Runtime or hand-written |

---

## 3. Recommendation

1. **Count-based scorer with Viterbi decoding (§2.2), as the first working system and the
   baseline.** The study's prototype is most of it already. Context: three previous beats, the next
   Expert beat, place in the measure and studio. Its first job is the evaluation teacher forcing
   can't give (§5.9): decoding from its own choices, and checking the gains survive.
2. **BiGRU + CRF over beat tokens (§2.4) as the main model.** The study finds useful context spanning
   several beats in both directions, and the count model running out of data at three beats back.
   Both point to a sequence model that shares what it learns. It has to beat step 1 on held-out songs
   to replace it.
3. **Trees (§2.3)** are the alternative to step 2 if a neural training setup isn't wanted. They need
   the history features written by hand, and a higher-order decoder.
4. **Fixes to the fallback `Reducer`**, independent of the model (§5.10): Hard and Medium thin too
   little, keys need their own lane range, and Hard and Medium keep too many sustains.

## 4. Evaluation

All comparisons are on held-out songs, split by normalised artist and title (the same song appears
in several games). A second split holds out whole games, to test generalisation across studios.

- **Per-beat accuracy and bits per beat** against the official patterns, **decoded** (each beat's
  context is the model's own earlier choices), as well as teacher-forced (§5.9).
- **Onset F1** against the official chart's kept chords.
- **Syncopation and rests:** the share of kept chords off the beat, and of beats left empty, against
  the official chart, by tier.
- **Pattern distribution:** Jensen–Shannon divergence of pattern frequencies over 1, 2 and 4 beats,
  against the noise floor of two halves of the official charts (§5.8).
- **Contour agreement:** for consecutive kept chords, how often the direction matches Expert's.
- **Riff consistency:** repeated Expert measures reduced identically.
- **Difficulty fit:** the [difficulty model](difficulty-model.md) rank of the draft, against the
  official chart's, per tier (§5.7).
- **Play-testing:** blind comparisons of model, `Reducer` and official drafts.

The current `Reducer` is scored the same way. The model has to beat it before it becomes the default.

## 5. Alignment study

Run on 2026-10-23 over the 2,732 songs downloaded so far (of about 4,333): 5,363 guitar, bass and
keys parts with all four difficulties, all loaded with `MidiChartLoader`. "Reducer" is Reduce all
with the shipped rules, chained as the dialog runs it: Hard from Expert, Medium from that Hard, Easy
from that Medium. Positions are measured in 48ths of a beat. Songs that appear in several games are
kept together on one side of every split (§5.8, §5.9), but aren't otherwise de-duplicated. 702
parts have no tier (shown as −1). The tool and full report are kept with the corpus, outside the
repository.

### 5.1 Lower difficulties are subsets of Expert

| Target | On an Expert onset | Within a 32nd (shifted) | Added | Keep ratio, official | Keep ratio, Reducer |
| --- | --- | --- | --- | --- | --- |
| Hard | 99.1% | 0.6% | 0.2% | 0.82 | 0.97 |
| Medium | 99.3% | 0.5% | 0.2% | 0.58 | 0.83 |
| Easy | 99.3% | 0.5% | 0.2% | 0.41 | 0.50 |

The labelling framing (§1.2) holds. Every studio is above 98.5%, except Beenox at about 96%.
Shifted and added chords are rare enough to treat as noise.

The Reducer thins far too little on Hard and Medium. On Hard it keeps 97% of Expert, against the
official 82%.

### 5.2 The character is lost in rests and off-beats

The share of Expert's chords at each position that survive:

| Position | Share of Expert | Medium official | Medium Reducer | Easy official | Easy Reducer |
| --- | --- | --- | --- | --- | --- |
| On the beat | 43.4% | 87.4% | 99.8% | 67.0% | 96.7% |
| 8th off-beat | 34.4% | 38.8% | 93.1% | 20.2% | 12.1% |
| 16th | 14.4% | 18.0% | 20.1% | 9.9% | 4.0% |
| Triplet | 4.5% | 21.5% | 34.5% | 10.8% | 6.6% |

What each beat that Expert plays in becomes on Easy:

| Easy keeps | Official | Reducer |
| --- | --- | --- |
| `1` (the downbeat) | 52.6% | 79.4% |
| `-` (nothing) | 29.4% | 10.3% |
| `&` (the off-beat alone) | 10.6% | 5.9% |

From an Expert beat of `1 &`, official Easy keeps `1` 57% of the time, rests 28% and keeps only the
`&` 11%. The Reducer keeps `1` 91% of the time.

The Reducer's Easy is a **quarter-note pulse**. Official Easy makes its rhythm from **rests** (almost
a third of beats left empty) and from **off-beats played in place of the downbeat**. Greedy,
downbeat-first thinning (§1.1) can't produce either.

Medium fails the opposite way. The Reducer keeps 93% of 8th off-beats, where official Medium keeps
39% and is mostly `1` (57% of beats, against the Reducer's 29%).

### 5.3 The difference is in how beats follow each other

Jensen–Shannon divergence of the per-beat pattern frequencies, and of consecutive pairs of beats
(0 is identical, 1 is disjoint):

| Target | Beats, official vs Reducer | Beat pairs, official vs Reducer | For scale: Harmonix vs Neversoft, beats |
| --- | --- | --- | --- |
| Hard | 0.044 | 0.088 | 0.014 |
| Medium | 0.163 | 0.307 | 0.045 |
| Easy | 0.065 | 0.181 | 0.069 |

At every difficulty, the gap over beat pairs is two to three times the gap over single beats. The
Reducer's beats are individually plausible, but **the sequences they form are not**. On Medium, the
Reducer is further from the official charts than two studios' styles are from each other by a
factor of 3.6. §5.8 and §5.9 look at windows wider than two beats.

### 5.4 Contour, range, chords and sustains

| Target | Same direction as Expert, official | Reducer | 2+ fret chords, official | Reducer | Sustains kept, official | Reducer |
| --- | --- | --- | --- | --- | --- | --- |
| Hard | 96.2% | 99.9% | 20.6% | 21.1% | 83.0% | 98.4% |
| Medium | 90.3% | 96.8% | 16.0% | 22.3% | 56.7% | 98.5% |
| Easy | 84.2% | 91.1% | 1.4% | 0.0% | 48.5% | 39.0% |

- **Contour is not where character is lost.** The Reducer follows Expert's direction *more* strictly
  than the official charts do, which re-voice more freely (official Easy holds a repeated fret only
  85% of the time).
- **Keys use the whole range.** 40% of official Easy keys chords are above yellow, and 28% of Medium
  keys chords are on orange. On guitar and bass it's 0.3–0.4%. The Reducer applies the guitar range
  to keys.
- **Sustains are dropped by difficulty.** Official charts keep 83%, 57% and 49% of Expert's sustains
  on Hard, Medium and Easy. The Reducer keeps 98% on Hard and Medium.

### 5.5 Studio

Studios differ, mostly on Easy. Neversoft keeps 0.50 of Expert on Easy and Harmonix 0.35, and their
Easy patterns diverge by 0.069, about as much as the Reducer's do from the official charts. Studio is
also the most useful single input to the count model on Easy (§5.9).

### 5.6 Baseline

The Reducer's onsets against the official chart's:

| Target | Precision | Recall | F1 |
| --- | --- | --- | --- |
| Hard | 81.2% | 97.8% | 88.7% |
| Medium | 65.4% | 94.5% | 77.3% |
| Easy | 67.0% | 82.1% | 73.8% |

### 5.7 What the tier changes

**Density barely changes with tier.** Chords per beat, from Expert's first chord to its last:

| Tier | Expert | Hard official | Medium official | Easy official | Easy Reducer |
| --- | --- | --- | --- | --- | --- |
| 0 | 1.21 | 1.04 | 0.74 | 0.48 | 0.72 |
| 1 | 1.43 | 1.21 | 0.86 | 0.58 | 0.77 |
| 2 | 1.54 | 1.28 | 0.92 | 0.66 | 0.78 |
| 3 | 1.67 | 1.33 | 0.92 | 0.63 | 0.79 |
| 4 | 1.74 | 1.32 | 0.90 | 0.60 | 0.77 |
| 5 | 1.84 | 1.36 | 0.91 | 0.63 | 0.78 |
| 6 | 2.03 | 1.41 | 0.92 | 0.62 | 0.80 |

**But difficulty does.** Mean [difficulty model](difficulty-model.md) rank of guitar and bass:

| Tier | Expert | Hard official | Hard Reducer | Medium official | Medium Reducer | Easy official | Easy Reducer |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | 1.80 | 1.42 | 1.73 | 1.06 | 1.52 | 0.64 | 1.01 |
| 1 | 2.21 | 1.69 | 2.11 | 1.22 | 1.77 | 0.82 | 1.10 |
| 2 | 2.82 | 2.05 | 2.64 | 1.42 | 2.09 | 1.03 | 1.18 |
| 3 | 3.36 | 2.38 | 3.13 | 1.49 | 2.35 | 1.04 | 1.24 |
| 4 | 3.83 | 2.64 | 3.52 | 1.57 | 2.51 | 1.06 | 1.29 |
| 5 | 4.38 | 2.94 | 3.86 | 1.67 | 2.67 | 1.14 | 1.34 |
| 6 | 5.10 | 3.35 | 4.58 | 1.88 | 3.10 | 1.24 | 1.47 |

The model's Expert ranks track the tiers, which is a useful check on both. Official Easy rises from
0.64 to 1.24, so a tier-6 Easy *is* harder than a tier-0 Easy, as intended.

**What carries it is rhythm.** Official Easy's attributes by tier:

| Tier | Rhythm | Pattern | Strum | Note | Notes/s | Off-beat chords | Rests | Fret changes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | 2.30 | 2.51 | 0.77 | 0.54 | 0.97 | 17.4% | 37.8% | 62.7% |
| 1 | 4.06 | 3.55 | 1.10 | 0.74 | 1.18 | 19.2% | 32.5% | 54.0% |
| 2 | 6.99 | 5.18 | 1.62 | 1.12 | 1.43 | 23.3% | 26.7% | 53.8% |
| 3 | 7.87 | 5.21 | 1.56 | 1.20 | 1.41 | 24.8% | 31.3% | 58.2% |
| 4 | 8.29 | 5.33 | 1.50 | 1.25 | 1.36 | 25.5% | 33.1% | 62.2% |
| 5 | 9.41 | 6.08 | 1.74 | 1.47 | 1.44 | 26.8% | 30.5% | 61.5% |
| 6 | 11.24 | 7.07 | 2.00 | 1.83 | 1.55 | 24.6% | 32.1% | 66.0% |

From tier 1 to 6, rhythmic complexity nearly triples (×2.8), while notes per second rise by about a
third. The off-beat share and fret changes rise with it.

The Reducer's Easy keeps about 11% of its chords off the beat **at every tier**. It throws away
exactly the thing that makes a harder song's Easy harder. Its ranks are too high at low tiers (1.01
against 0.64 at tier 0) and too flat across them. Its Medium is far too hard everywhere: 3.10
against 1.88 at tier 6.

Section 5.9 adds that, given the Expert beat and the previous one, the tier tells the count model
almost nothing more. The official rule for reducing a beat is close to the same at every tier.
Harder songs get harder Easy charts because their Expert rhythms are harder, and a faithful
reduction carries that through.

### 5.8 Divergence over wider windows

Jensen–Shannon divergence of pattern frequencies over one beat, two consecutive beats, and whole
4-beat measures. The noise floor compares two halves of the official charts, split by song, and rises
with the window because wider windows have more distinct patterns to estimate.

| Target | 1 beat: Reducer | Noise | 2 beats: Reducer | Noise | 4-beat measure: Reducer | Noise |
| --- | --- | --- | --- | --- | --- | --- |
| Hard | 0.044 | 0.003 | 0.088 | 0.015 | 0.204 | 0.120 |
| Medium | 0.163 | 0.002 | 0.307 | 0.008 | 0.417 | 0.068 |
| Easy | 0.065 | 0.001 | 0.181 | 0.006 | 0.360 | 0.044 |

On Medium and Easy, the Reducer's measures are 6–8 times further from the official charts' than the
noise floor. On Hard, the measure-level gap is mostly noise.

### 5.9 Context test: what each kind of context is worth

A prototype of the count model (§2.2) predicts each beat's official pattern from its context. It
is trained on 80% of songs (about 1.75 million beats per target) and scored on the other 20%. Bits
per beat measure how surprised it is by the official choice: lower is better. Accuracy is how often
its best guess is the official pattern.

The previous beats' patterns given to it are the official ones (teacher forcing). That measures what
the context is worth; a decoder working from its own choices will do somewhat worse (§3, step 1).

| Context | Hard bits | Hard accuracy | Medium bits | Medium accuracy | Easy bits | Easy accuracy |
| --- | --- | --- | --- | --- | --- | --- |
| Expert beat only | 1.138 | 73.8% | 1.357 | 66.0% | 1.431 | 60.7% |
| + previous beat | 1.075 | 75.8% | 1.235 | 69.0% | 1.331 | 62.7% |
| + 2 previous beats | 1.032 | 76.7% | 1.143 | 72.8% | 1.143 | 71.5% |
| + 3 previous beats | 1.041 | 77.0% | 1.100 | 74.3% | 1.069 | 74.3% |
| + previous beat, place in measure | 1.068 | 75.8% | 1.233 | 69.4% | 1.226 | 65.9% |
| + previous beat, next Expert beat | 1.047 | 76.3% | 1.110 | 73.2% | 1.181 | 67.3% |
| + previous beat, tier | 1.099 | 75.5% | 1.256 | 69.1% | 1.332 | 63.1% |
| + previous beat, studio | 1.091 | 75.7% | 1.207 | 69.9% | 1.230 | 68.4% |
| + previous beat, studio and tier | 1.109 | 75.4% | 1.227 | 69.6% | 1.214 | 70.1% |
| *Reducer's pattern* | | *71.0%* | | *45.7%* | | *62.2%* |
| *Always the commonest pattern* | | *37.7%* | | *57.1%* | | *52.3%* |

- **Context reaches several beats back.** On Easy, the second previous beat is worth more than the
  first (+8.8 points against +2.0), and the third still adds 2.8. Official Easy works in figures
  across two to four beats (a half-note pulse, a rest every other beat, a measure-long cell), which
  a first-order transition can't represent.
- **Context ahead matters too.** The next Expert beat adds about as much as a second previous beat
  on Medium.
- **Studio matters on Easy** (+5.7 points over the previous beat alone). **Tier barely matters**
  (+0.4), as §5.7 explains.
- **The count model runs out of data at about three beats back.** On Hard, the third previous beat
  makes the bits slightly worse. A model that shares what it learns between contexts is needed to go
  further (§2.4).
- The Reducer is below "always guess `1`" on Medium (45.7% against 57.1%). Even the simplest count
  model beats it at every difficulty.

### 5.10 What this means

1. The labelling framing and beat-pattern decoding (§2.1) are supported: 99% of official onsets are
   Expert's, and the gap is in sequences of beats, out to whole measures.
2. The model must learn **rests** and **off-beat substitution**, in figures several beats long. Any
   approach that ranks chords one at a time, or looks only one beat back, will miss them.
3. Tier is carried by **rhythmic complexity**, not density, and mostly through the Expert chart
   itself. Faithful reduction, not tier-specific rules, is what makes a tier-6 Easy harder.
4. The count model is a good first system. The context it needs points to a bidirectional sequence
   model (§2.4) as the main one (§3).
5. Three things are rule-level fixes to the fallback `Reducer`, independent of the model:
   - Hard and Medium thin too little.
   - Keys need their own lane range.
   - Hard and Medium keep too many sustains.
