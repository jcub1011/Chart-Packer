# Improved Difficulty Generator — Specification

| | |
| --- | --- |
| **Status** | Draft, not started. Design questions settled (§11). |
| **Scope** | Three difficulty generators side by side in both apps: the existing rules, a count-based pattern model and a neural model. A way to compare their drafts. Swappable trained models, published separately, with a trainer for making them. |
| **Companion docs** | [difficulty-generator-proposals.md](difficulty-generator-proposals.md) (the options, the study and the reasoning behind this spec), [charter-spec-v1.md](charter-spec-v1.md) ([C9](charter-spec-v1.md#c9-difficulty-authoring) Reduce, [C16](charter-spec-v1.md#c16-chart-lint) the Validator's generated difficulties), [difficulty-model.md](difficulty-model.md) (the difficulty rating, used to check drafts) |
| **Language** | C# throughout: the apps, the trainer and the evaluation tools |

---

## 1. Overview and goals

Reduce drafts lower difficulties from a higher one. Its drafts lose the character of the source
chart: the [proposals' study](difficulty-generator-proposals.md#5-alignment-study) found they flatten
syncopation into a pulse on the beat, never rest, and keep the same off-beat share at every tier,
where official charts don't.

This spec adds two learned generators beside the existing one, without replacing it:

| Generator | What it is | Where its knowledge comes from |
| --- | --- | --- |
| **Rules** | Today's Reduce, with three fixes (§3.1) | Hand-set rules in Settings |
| **Patterns** | A count-based beat-pattern model with sequence decoding (§3.2) | A pattern pack: counts from a set of charts |
| **Neural** | A bidirectional recurrent network with a sequence layer (§3.3) | A neural pack: weights trained on a set of charts |

**No pack ships with the apps.** Packs are published in a separate repository (§5.5). A fresh
install has Rules only, and the learned generators appear once the user imports a pack. That's why
Rules is the fallback everywhere (§3.7).

### 1.1 Goals

- **G1 — Keep character.** The learned generators choose rhythm over whole sequences of beats, so
  drafts keep rests, off-beats and figures as official charts do.
- **G2 — Compare.** The charter can switch between the three generators' drafts of the same target
  instantly, see how each differs from the others and from an existing chart, and apply any of them.
- **G3 — Rules stays.** Rules is always available. It's the fallback whenever a learned generator
  can't run, including on every fresh install, and its behaviour doesn't change except for the fixes
  in §3.1.
- **G4 — Swappable models.** Trained models are files. Users can install packs from the pack
  repository or from others, or train their own from their own charts, to target a charting style or
  use a larger data set.
- **G5 — C#.** Everything is C#. The apps run the models in managed code with no new native
  dependency.
- **G6 — Measurable.** Every generator and every pack can be scored the same way against reference
  charts, so "is it good enough" has an answer beyond play-testing.

### 1.2 Out of scope for this spec

- Learned generators for **drums and GHL**. They keep using Rules. The design allows packs for them
  later (§5.2).
- Generating from audio, or from a chart with no higher difficulty.
- Training inside the apps. Training is a separate tool (§6).
- Downloading packs from inside the apps. Users download a pack and import it (§5.5).

Learned **voicing** (frets and chord width) and **sustains** are a later milestone (M7). Until then,
the learned generators choose rhythm, and voicing and sustains come from Rules' logic (§3.4).

---

## 2. Terms

| Term | Meaning |
| --- | --- |
| **Source** | The difficulty a draft is made from: Expert, or the one above the target in Reduce all. |
| **Beat pattern** | The onsets in one beat, in counting notation: `1 e & a` for sixteenths, `t T` for triplets, `-` for none. Positions are measured in 48ths of a beat, so sixteenths and triplets are both exact. |
| **Candidate** | A pattern a draft may keep in a beat: a subset of the source's onsets there, including none. |
| **Scorer** | What gives each candidate a score in its context: counts (Patterns) or a network (Neural). |
| **Decoder** | What picks the best sequence of candidates over the part, inside the hard limits. |
| **Voicing** | Choosing frets and chord width for the kept chords. |
| **Pack** | A trained model in one file: a pattern pack or a neural pack (§5). |
| **Pack repository** | The separate repository where packs are published, with their evaluation reports (§5.5). |
| **Style** | A label a pack was trained to imitate, such as a studio or a charter, chosen per run. **Any** is a blend of all of a pack's styles (§3.3). |
| **Reference** | A chart's existing notes on the target difficulty, when it has them, shown for comparison. |

---

## 3. The generators

### 3.1 Rules

Rules is today's Reduce ([C9](charter-spec-v1.md#c9-difficulty-authoring)) and keeps all of its
behaviour. The proposals' study found three of its shipped defaults far from official charts.
This spec retunes them:

1. **Hard and Medium thin more.** Rules keeps 97% of Expert's chords on Hard and 83% on Medium.
   Official charts keep 82% and 58%. The Hard and Medium defaults are retuned so that, on the
   reference corpus, their keep ratios fall within 0.05 of the official ones.
2. **Keys get their own range.** Official Easy keys use all five lanes (40% of Easy keys chords are
   above yellow) and Medium keys use orange. Keys get their own default sets in Settings, beside
   5-fret and GHL, whose lane ranges follow the official charts. Existing settings without keys
   sets take the new keys defaults.
3. **Sustains are dropped by difficulty.** Official charts keep about 83%, 57% and 49% of Expert's
   sustains on Hard, Medium and Easy. Rules keeps 98% on Hard and Medium. The defaults are retuned to
   within 10 points of the official shares.

The retuned values are measured on the reference corpus with the evaluation command (§4.5), and
recorded in C9's defaults table.

### 3.2 Patterns

Patterns chooses each beat's kept pattern with a table of counts from a pattern pack, and decodes
the whole part as a sequence.

**Context.** Each beat's context can include:

- the source's pattern in the beat;
- the patterns already chosen for up to three previous beats;
- the source's pattern in the next beat;
- the beat's place in its measure;
- the style;
- the source's difficulty rank, when the pack uses it (§3.3);
- the target, source and instrument.

Which features a pack uses, and in what order of importance, is part of the pack (§5.2). The
trainer's default configuration follows the study: three previous beats, the next source beat,
place in measure and style.

**Probabilities.** A context's probability for each candidate comes from how often the pack's charts
kept that pattern in that context. Rare contexts are blended with the same context minus its least
important feature, weighted by how much data each has, down to the source pattern alone and then
the overall pattern frequencies. A candidate never seen in any context still gets a small,
non-zero probability, so decoding never fails for lack of data.

**Requirements.**

- Every candidate for a beat gets a probability, including patterns the pack has never seen.
- The same input and pack always give the same draft.
- A pack's counts are the only learned state. Nothing is learned while the app runs.

### 3.3 Neural

Neural chooses each beat's kept pattern with a small bidirectional recurrent network (a GRU) and a
sequence layer (a linear-chain CRF), from a neural pack.

**Inputs.** The network reads the part as a sequence of beats. For each beat it reads:

- the source pattern, as a learned embedding;
- per-beat features: place in measure, gap since the previous onset, whether the beat starts a
  phrase or section, sustains in the beat, star power and solo membership, riff membership with
  its repeat number (counted in measures), the beat's length in seconds (the tempo: a sixteenth at
  80 BPM is not one at 200), how many onsets it has, whether it has a chord, and whether the melody
  turns in it (Rules' turn: the fretted chords either side both higher or both lower);
- conditioning, shared by every beat of a run: the source–target pair (Hard→Medium is not
  Expert→Medium), the instrument and the style, each a learned embedding.

All the per-beat features are written by one encoder that both the apps and the trainer use, so they
can't scale them differently.

**The source's difficulty rank** is optional. The trainer can include it, and the pack's manifest says
whether it does (§5.2). It is the source difficulty's rank by the difficulty model
([C18](charter-spec-v1.md#c18-preview)), worked out the same way in training and in the apps, not the
song.ini tier: charters' and studios' house rules for tiers differ, and community charts' tiers are
less reliable still, while the model, if imperfect, is consistent. A source the model can't rate
(fewer than two chords) reads as the middle of the range.

**Any style.** A pack trained with styles also learns a blend of them. During training, the style is
hidden on a share of the examples, so the model learns what to do without one. **Any** is then a
real style of the pack, and its default. The same applies to Patterns, whose contexts back off to
"no style" (§3.2).

**Outputs.** For each beat, a score for each candidate. The sequence layer adds learned scores for
each pattern following another. Together they give the decoder the score of any whole sequence.

**Requirements.**

- **Size.** The trainer's default architecture stays under about 500,000 parameters, about 2 MB as a
  pack. Packs may be larger, up to the size limit in §5.3.
- **Determinism.** The same input and pack always give the same draft. No sampling.
- **Managed inference.** Inference is managed C# with no native dependency, and works where the
  shared core library works, including AOT builds.
- **Parity.** The apps' inference matches the trainer's own (§6.5) within a stated tolerance on a
  fixed set of synthetic inputs. This is checked in tests.
- **Lengths.** Any number of beats works, from a selection of one measure to a whole song.

### 3.4 What the learned generators share

Patterns and Neural run the same pipeline. Only the scorer differs.

1. **Beats.** The source part is split into beats by the chart's time signatures, and each beat's
   onsets are written as a pattern. Beats the source doesn't play in are passed over, but still count
   as context (an empty previous beat is information).
2. **Candidates.** Each onset is placed on the nearest 48th of its beat, so a note a tick or two off
   the grid is on it. Each beat's candidates are the subsets of its onsets that the pack's vocabulary
   contains, plus keeping nothing. Vocabulary v2 (beat schema 2) has every subset of the beat's four
   sixteenths and every subset of its triplet with or without the downbeat, so a full triplet can be
   kept whole. A beat with more onsets than the vocabulary covers uses its candidates' nearest
   supported shapes.
3. **Scoring** by the pack (§3.2, §3.3).
4. **Decoding.** The decoder finds the highest-scoring sequence over the part inside the hard limits
   (§3.5). Its state is the last few chosen patterns, as many as the scorer's context uses. The
   network always reads the whole part, so a range is drafted with the song around it as context.
5. **Voicing.** Kept chords are voiced by Rules' existing logic, the same code Rules' own draft goes
   through after thinning: phrases fitted to the target's lane range with their shape kept, chords
   narrowed to the target's width, opens handled as the rules say. Given the chords Rules keeps, the
   voicing is Rules' note for note. The keys ranges of §3.1 apply. Learned voicing replaces this in M7.
6. **Attributes.** HOPOs and taps are played as the source plays them, and sustains follow the
   target's rules, as in Rules. Learned sustains replace this in M7.
7. **Result.** The draft is the same kind of result Rules produces, so the preview, diff, check and
   Apply work unchanged.

**The density control.** The dialog offers a single **fewer notes ↔ more notes** slider for the
learned generators. It shifts every candidate's score by an amount proportional to how many chords
the candidate keeps, so the decoder leans sparser or denser without changing which figures it
prefers. It's centred by default, and centred has no effect. Its range is limited so that its ends
stay within the pack's evaluated density: about the official spread for the target, as the pack's
evaluation report measures it.

### 3.5 Hard limits

Learned generators are never trusted with playability. Their drafts always meet:

- the target's **lane range** and **largest chord**, as in Rules for the part's family;
- a **minimum gap** between kept chords for each target. It's a floor, not a grid: official charts
  have closer pairs than Rules' thinning grid allows, so the floor is set below the grid. Each pack
  carries defaults for it, measured from its training charts, and the dialog can change them per
  run;
- **at least one note in every star power phrase and solo** that has notes on the source, as Rules
  guarantees ([AC C9.3](charter-spec-v1.md#c9-difficulty-authoring)): one per phrase, not one per
  beat of it. Where the decoded draft leaves a phrase empty, its best-scoring single onset that keeps
  the minimum gap is kept;
- **riffs reduced the same:** measures charted the same on the source (the same chords at the same
  places, as Rules' riffs are keyed) get the same patterns, as Rules does now. Each beat of a riff
  scores as the mean over its repeats, the part is decoded beat by beat, and each riff place then
  takes the choice most of its repeats have.

A limit the decoder can't meet (for example, a star power phrase whose only notes are closer than
the floor) is met as nearly as it can be, and reported in the dialog's check.

### 3.6 Ranges and Reduce all

- **Range.** Reducing a selection or a section decodes only inside it. The target's notes just
  outside the range are context: they're the previous beats at the range's start, and the decoder
  keeps the minimum gap against them at both ends.
- **Reduce all** can run in two ways:
  - **chained**: Hard from Expert, Medium from the new Hard, Easy from the new Medium. Each lower
    difficulty is a subset of the one above, as with Rules;
  - **from Expert**: each difficulty straight from Expert, as the official charts may have been
    written.
- **The default is decided by measurement.** Before M3, the study measures how far official
  difficulties nest: the share of official Easy onsets that are on official Medium onsets, and of
  Medium on Hard. If they nest (nearly every onset is on the one above), learned generators chain
  by default; if not, they reduce from Expert by default. The result and the threshold used are
  recorded here.
- **Both ways are available either way.** The trainer trains packs on both kinds of pair (Expert to
  each target, and each difficulty to the one below), and a pack lists the pairs it supports. A run
  needing a pair the pack lacks uses the nearest one it has, and the dialog says so.

### 3.7 When a learned generator can't run

A learned generator falls back to Rules, saying why in the dialog and in the Validator's report, when:

- no pack of its kind is installed (the state of every fresh install), or the chosen pack failed to
  load (§5.3);
- the part's family isn't one the pack supports (drums and GHL, for now);
- the run's source–target pair has no usable fallback in the pack.

The fallback is per part. Reduce all over a song can use Neural for guitar and Rules for drums in
one run.

---

## 4. Choosing and comparing

### 4.1 The Reduce dialog

The Reduce dialog gains a **generator** choice, **Rules**, **Patterns** or **Neural**, at its top.

- **Rules** shows the rules table as today.
- **Patterns** and **Neural** show:
  - the pack, from those installed for that kind;
  - the style, from the pack's styles (**Any** first), with each style's description;
  - the density slider (§3.4);
  - the hard limits (§3.5), prefilled from the pack's defaults;
  - for Reduce all, chained or from Expert (§3.6), prefilled from the default.
- A learned generator with no pack installed is still listed. It shows that no pack is installed, a
  **Get packs** link to the pack repository (§5.5), and that its draft is Rules'.
- A generator that can't run for this part shows why, and its draft is Rules' (§3.7).
- Choices made in the dialog apply to that run, as today. The defaults come from Settings (§4.4).

### 4.2 Compare

The dialog keeps a draft from **each** generator for the current target and range, computed in the
background as soon as the dialog opens. Changing a generator's settings recomputes that generator's
draft only.

- **Switching.** The ghost over the target shows one generator's draft at a time. Switching (by the
  generator choice, or keys **1**, **2** and **3**) shows another's at once, from its kept draft. A
  draft still being computed shows a progress mark until it's ready.
- **Differences.** A **compare with** choice overlays a second generator's draft, or the reference
  (§4.3), on the one shown. Their differences are marked in the preview's existing change colours:
  notes only in the shown draft, notes only in the other, and notes in both but voiced differently.
- **Summary.** Beside the preview, a table shows each draft's figures together, so they can be
  compared at a glance:

  | Figure | What it is |
  | --- | --- |
  | Chords kept | Count, and as a share of the source's |
  | Off-beat | Share of kept chords off the beat |
  | Rests | Share of the source's beats the draft leaves empty |
  | Sustains kept | Share of the source's sustains still held |
  | Suggested rating | The difficulty model's tier and rank ([C18](charter-spec-v1.md#c18-preview)) |
  | Findings | The draft's lint findings, as the check shows them now |

- **Apply** applies the draft being shown, as one undoable step, as today. Nothing else changes in
  the chart.

### 4.3 The reference

When the target already has notes (an official chart, or the charter's own), they're the
**reference**. The dialog can then show, for each draft:

- **onset agreement** with the reference: precision, recall and F1;
- **pattern agreement:** the share of the source's beats where the draft keeps the same pattern as
  the reference;
- the reference's own figures, as a row in the summary table.

This is how the charter judges whether Patterns is "decent enough": on songs that already have good
lower difficulties, it shows directly how close each generator gets.

### 4.4 Settings and the Validator

- Settings gains, per part family, a **default generator**, a **default pack** for each learned
  kind, a **default style**, and the **Reduce all** default (§3.6). Families with no learned packs
  (drums, GHL) show Rules only.
- On a fresh install, every family's default generator is Rules. After the first pack of a kind is
  imported, Settings offers to make its generator the default for the families it supports.
- These are shared by both apps, beside the default reduction rules: `generation-defaults.json`
  holds the method, the pack, the style, the density and the Reduce all and lane-range defaults.
  Chart Builder's Reduce defaults (in Settings) write it; a run in the Reduce dialog starts from it and
  its changes are its own.
- Chart Packer's Generate Lower Difficulties, which fills a song's missing difficulties
  ([C16](charter-spec-v1.md#c16-chart-lint)), uses the default generator, with the same fallback
  (§3.7). Each song's row, and the preview's findings, say where a difficulty fell back to Rules or
  was drafted from a pair the pack lacks.

### 4.5 The evaluation command

A command-line evaluation, part of the trainer tool (§6), runs every generator (and any number of
packs) over a folder of songs that already have lower difficulties. It reports, per generator and
pack, per target, per style and per tier:

- the figures and agreements of §4.2–§4.3;
- the pattern divergences of the proposals' §4, against the noise floor of two halves of the
  references;
- the difficulty model's ranks against the references';
- how far the references' difficulties nest (§3.6).

It writes a report a person can read, and a data file the next run can be compared with. It's how
packs are judged before they're published, and how §3.1's retuned rules are checked.

---

## 5. Packs

### 5.1 What a pack is

A pack is **one file** holding one trained model: the counts of a pattern pack, or the weights of a
neural pack, with a manifest describing it. A pack is **data only**. It contains no code, and
nothing in it is ever executed. That's what makes packs safe to share and to install from others.

### 5.2 The manifest

Every pack describes itself:

| Field | Purpose |
| --- | --- |
| Format version | Which pack format it is (§5.4) |
| Kind and architecture | Pattern or neural, and for neural the architecture and its dimensions |
| Beat schema version | How beats, patterns and features were encoded when it was trained; must match the app's |
| Name, author, description | Shown in Settings and the dialog |
| Styles | Each style label with a description, and whether it offers **Any** (§3.3) |
| Part families and instruments | Which parts it can reduce |
| Source–target pairs | Which reductions it was trained on (§3.6) |
| Inputs | Which context features it uses, including whether it reads the source's difficulty rank, and its layer sizes |
| Hard-limit defaults | Minimum gaps per target (§3.5) |
| Density range | The ends of the density slider for each target (§3.4) |
| Pattern vocabulary | The beat patterns it knows |
| Training summary | Song and part counts, the charters or studios, the date, the trainer version; never any chart content |
| Evaluation summary | Its held-out figures against Rules (§6.6) |
| License | Under what terms it may be shared |
| Checksum | Of the model data, to detect damage |

### 5.3 Loading

- A pack is validated completely before it's used: format version, schema version, every dimension,
  every value finite, the checksum, and a size limit of 64 MB.
- A pack that fails validation is never half-used. It's listed with its problem, its generator falls
  back to Rules (§3.7), and nothing else is affected.
- Problems are reported as findings, the way the `.sng` reader reports a damaged file, never as a
  crash.
- A pack is loaded once and shared, read-only, by every run that uses it.

### 5.4 Versions

- The apps read the current pack format and the one before it. Format 2 (beat schema 2) added the
  pair and instrument embeddings, the tempo, chord, turn and onset-count features, the source rank in
  place of tier, and vocabulary v2; format 1 packs are refused, and must be trained again. A pack's
  vocabulary must be the app's exactly.
- A pack whose **beat schema** differs from the app's is refused with an explanation: its features
  would mean something else. A model can be moved to a new schema only by training it again.
- Every format change is described in this spec's change notes, with what it means for existing
  packs.
- The pack repository lists, for each pack, the app versions it works with.

### 5.5 Getting, installing and sharing packs

- **The pack repository.** Packs are published in a separate repository, as release files, each
  with its evaluation report (§6.6) and model card. The apps never ship a pack, and never download
  one themselves.
- **Get packs.** Settings, and the dialog when no pack is installed (§4.1), have a **Get packs** link
  that opens the pack repository's releases page in the browser.
- **Import.** Settings imports a downloaded pack file, copying it into the user's packs folder after
  validation (§5.3). A pack that fails validation isn't copied.
- **Listing.** Settings lists the installed packs, each with its manifest shown as a model card:
  name, author, styles, what it was trained on, its evaluation summary and license.
- **Remove.** Settings removes an installed pack. If it was a default, the default falls back
  (AC M6.3).
- **Sharing.** A pack file is shared as is. Packs from anyone other than the pack repository are
  imported the same way.

---

## 6. Training

### 6.1 The trainer

A command-line tool, written in C#, makes packs. It lives in **this repository**, as a project the
installer leaves out. Building it from the same code as the apps keeps the beat schema it writes in
step with the apps that read it. The pack repository holds only packs and their reports.

The neural trainer depends on a large native library (§6.5), which is another reason it isn't
installed with the apps. Its commands:

| Command | What it does |
| --- | --- |
| **Prepare** | Scans a folder of songs and writes a training set: each usable part's beats, patterns and features, with the song's style label |
| **Train patterns** | Counts a prepared set into a pattern pack |
| **Train neural** | Trains a neural pack from a prepared set |
| **Evaluate** | The evaluation command of §4.5 |
| **Inspect** | Prints a pack's manifest and validates it |

**Publishing a pack** is: Prepare, Train, Evaluate against Rules (and against Patterns, for a neural
pack), then release the pack file and its report in the pack repository.

### 6.2 Training data

- **Sources.** Song folders and `.sng` files, read by the apps' own chart readers, so the trainer
  sees charts exactly as the apps do.
- **Usable parts.** A part is used when its source and target difficulties are all charted. Parts
  whose lower difficulties look generated rather than hand-made (identical to Rules' draft, or
  failing basic checks) can be excluded.
- **Styles.** By default, a song's style label is its charter field, matched against a list the user
  gives. A mapping file can assign styles to songs or charters directly, so a user can train "my
  style", one charter's style, or a studio's.
- **The source's difficulty rank** is included as an input unless turned off (§3.3). Charts are read
  with their song.ini's settings, as the apps read them.
- **Rules' draft** of every part is kept beside the official one, labelled the same way, as the
  evaluation's baseline (§6.6).
- **Splitting.** A held-out share of songs is set aside for evaluation, split by song (normalised
  artist and title), so the same song charted in two places is never on both sides. Songs are picked
  by a stable hash of that key, not alphabetically.
- **Duplicates.** Parts whose source and target notes are identical to another part's are counted once.
- **Guidance.** The trainer warns when the data is thin for what's asked of it: a pattern pack's
  contexts mostly unseen, or a neural pack with too few parts for its size. The study's corpus,
  about 5,000 parts per target, is the reference point for what's enough.

### 6.3 Privacy of training data

The prepared set and the packs contain derived figures, never the songs' notes, audio or text. The
training summary in a pack names counts and labels, not songs.

### 6.4 Pattern training

Counting a prepared set into a pack takes minutes on a CPU. The context features, their order, and
the smoothing are options, with the default configuration of §3.2.

### 6.5 Neural training

- Training uses **TorchSharp**, the .NET bindings to the PyTorch engine. It's C#, as G5 requires, and
  is used only by the trainer.
- It runs on a CPU, and uses an NVIDIA GPU when one is present.
- Training is reproducible from a seed. It stops early when the held-out score stops improving.
- Batches are padded and **packed**, so the backward direction of each sequence starts at its own
  last beat, as in the apps, never on padding. The loss is the CRF's negative log-likelihood per beat,
  so long and short songs weigh the same per beat.
- **Any style** is trained in by hiding the style on a share of the examples (§3.3). The share is an
  option, with a default.
- The trained network is exported into the pack format, and the apps' managed inference reads it
  (§3.3). Every export is checked by running both the trainer's and the apps' inference on the same
  inputs and comparing (the parity requirement of §3.3).
- *Considered and not chosen for now:* hand-written C# training with no TorchSharp. The network is
  small enough for it to be possible, but it means writing and verifying the gradients and the
  optimiser, and would train much more slowly. It stays an option if TorchSharp's size or licensing
  becomes a problem.

### 6.6 Evaluation in training

Every trained pack is evaluated on its held-out songs against Rules, and against Patterns when a
pattern pack is given. The results are written as a report and summarised in the pack's manifest
(§5.2). The report also measures the density spread that sets the pack's slider range (§3.4), and
the minimum gaps that become its hard-limit defaults (§3.5): the first percentile of the gaps between
the official target's kept onsets, in 48ths of a beat, within a beat as well as across beats.

For each source–target pair, on the beats the source plays, the pack's decoded drafts and Rules'
drafts are each compared with the official chart: the share of beats whose kept pattern matches, the
precision, recall and F1 of the kept onsets, and the density against the official chart's. Both are
measured on the same held-out beats, so the difference is the pack's gain over Rules.

Two figures are always reported:

- **decoded**, where each beat's context is the generator's own earlier choices, which is what the
  apps do;
- **teacher-forced**, where the context is the reference's choices, which shows what the context is
  worth.

---

## 7. Requirements on the apps

- **Speed.** On a mid-range CPU, for a typical part of about 600 beats:
  - Patterns drafts all three lower difficulties in under 0.1 s;
  - Neural drafts them in under 0.5 s.
- **Off the interface thread.** Drafts are computed in the background and can be cancelled, as
  Reduce's are today. Compare (§4.2) computes the three generators' drafts concurrently.
- **Memory.** A loaded pack and its runs stay under 200 MB.
- **No new native dependencies** in either app, and nothing that stops the shared core library from
  building for AOT.
- **No network access** for packs. The apps only open the pack repository's page in the browser
  (§5.5).
- **Shared.** The generators, the beat pipeline, the decoder and the pack reader live in the shared
  core library, so Chart Builder and Chart Packer behave the same. The dialog and Settings changes
  are in the shared and Chart Builder interfaces.

---

## 8. What changes in the existing design

The following changes are needed. This section describes them; it doesn't prescribe the code.

- **A generator seam.** Reduce, Reduce all and the Validator already run the reduction of each
  target through one step that's passed in. That step becomes a choice among the three generators,
  each producing the same kind of draft.
- **Rules is split** into its two halves: choosing which chords to keep, and voicing and attributes.
  Rules uses both. The learned generators replace the first and reuse the second (§3.4).
- **Settings** gain:
  - the keys rule sets (§3.1);
  - the generator, pack, style and Reduce all defaults (§4.4);
  - the user packs folder and its list (§5.5).

  Older settings read as before, with the new defaults filled in.
- **The Reduce dialog** gains the generator choice, the per-generator settings, compare, the
  summary, the reference and the Get packs link (§4).
- **The check** (lint findings, suggested ratings, the "not easier than the one above" warning) runs
  on whichever draft is shown, unchanged.
- **C9 and C16** in the charter spec are updated to describe the generators, and C9's defaults table
  to the retuned rules.
- **The installer** leaves the trainer project out.

---

## 9. Testing

- **No real charts in tests.** As everywhere in this repository, every test fixture is synthetic.
  The reference corpus is used only for evaluation, outside the repository.
- **The beat pipeline:** beats across time-signature changes, triplets, shifted onsets, empty beats,
  ranges at song edges.
- **The decoder:** every hard limit of §3.5 on fixtures built to tempt it (dense runs, star power
  phrases with one note, riffs with different surroundings), and that it finds the best sequence on
  small cases checked by brute force.
- **The density slider:** centred changes nothing, and its ends move the draft's chord count in the
  expected direction without breaking a hard limit.
- **Patterns:** the blending of rare contexts, unseen patterns, the Any style's backoff, determinism.
- **Neural:** parity between the apps' inference and the trainer's (§3.3), on a small network with
  random weights made for the test, never trained on real charts.
- **Packs:** every validation failure of §5.3 on damaged and hostile files: wrong versions, wrong
  dimensions, non-finite values, bad checksums, oversized files. Each is a reported problem and a
  fallback, never a crash, and a failed import copies nothing.
- **No pack installed:** every generator choice works, the learned ones falling back to Rules, in
  both apps.
- **The generators end to end:** each makes a valid, playable draft of every target on fixture
  charts, and the draft passes the check.
- **The interface** is tested by hand, using the checks listed with each milestone.

---

## 10. Milestones

Each milestone ends with the apps shippable. Its acceptance criteria must pass before the next
begins.

### M1 — The seam and the Rules fixes

The generator seam (§8) with Rules as its only generator, the keys rule sets, and the retuned
defaults (§3.1).

- **AC M1.1** With the generator set to Rules and the old defaults, every draft is identical to
  today's.
- **AC M1.2** With the retuned defaults, on the reference corpus, Hard and Medium keep ratios are
  within 0.05 of the official ones, and sustains kept within 10 points.
- **AC M1.3** Keys reduce by their own rule sets, and settings saved before M1 read with the keys
  defaults filled in.

### M2 — The beat pipeline

Beats, candidates, the decoder with every hard limit, ranges, riffs, the density slider and voicing
by Rules' logic, run with a stand-in scorer that only reproduces simple rules (for testing). The
nesting measurement of §3.6 is made, and the Reduce all default recorded.

- **AC M2.1** Every hard limit of §3.5 holds on the decoder's test fixtures.
- **AC M2.2** A range reduction leaves the target's notes outside the range unchanged, and keeps the
  minimum gap against them.
- **AC M2.3** The nesting result and the Reduce all default it sets are recorded in §3.6.

### M3 — Patterns, packs and compare

The pattern generator, the pack format and loader, import and the Get packs link, the trainer's
Prepare, Train patterns, Evaluate and Inspect commands, and the dialog's generator choice, compare,
summary and reference.

- **AC M3.1** A pattern pack trained by the trainer imports and loads in both apps. A damaged one is
  refused at import, or falls back to Rules with its problem shown if it's damaged after import.
- **AC M3.2** In the dialog, switching between Rules' and Patterns' drafts is immediate once both are
  computed. Apply applies the one shown, as one undo step.
- **AC M3.3** With a reference present, the summary shows each draft's onset and pattern agreement.
- **AC M3.4** On the held-out reference songs, decoded Patterns drafts agree with the official
  patterns more often than Rules does, on every target.
- **AC M3.5** With no pack installed, both apps behave as after M1, and the dialog's learned
  generators show the Get packs link.

### M4 — The go/no-go for Neural

The evaluation report of Patterns against Rules on held-out songs, decoded. This decides how much
Neural must improve to be worth its cost.

- **AC M4.1** The report exists, with the decoded and teacher-forced figures of §6.6 for every
  target and style, Any included.

### M5 — Neural

The neural trainer (TorchSharp), managed inference in the apps, neural packs, and Neural in the
dialog and Validator.

- **AC M5.1** The apps' inference matches the trainer's within tolerance on the parity fixtures.
- **AC M5.2** Neural meets the speed and memory requirements of §7.
- **AC M5.3** On held-out reference songs, decoded Neural drafts agree with the official patterns
  more often than Patterns' do, on every target.
- **AC M5.4** Rules, Patterns and Neural can all be compared in one dialog session.

### M6 — Swappable packs

Listing and removing packs, model cards, the defaults offer of §4.4, style mapping in the trainer,
and the guide for users who train their own.

- **AC M6.1** A pack trained by one user, copied to another machine and imported, gives identical
  drafts there.
- **AC M6.2** A pack trained with a custom style mapping offers those styles in the dialog, with Any
  first.
- **AC M6.3** Removing a pack that's a default falls back to the next installed pack of its kind,
  or to Rules, and Settings says so.

### M7 — Learned voicing and sustains

The learned generators also choose frets, chord width and which sustains are kept, from the same
packs (a new architecture version), with Rules' voicing as the fallback for packs that don't
include it.

- **AC M7.1** On held-out reference songs, learned voicing agrees with the official frets more often
  than Rules' voicing does, without breaking a hard limit.
- **AC M7.2** Packs without learned voicing keep working, voiced by Rules' logic.

---

## 11. Decisions

The design questions are settled as follows.

| Question | Decision |
| --- | --- |
| Do packs ship with the apps? | No. Packs are published in a separate repository. A fresh install has Rules only, which is why Rules is the fallback everywhere (§1, §5.5). |
| How do users get packs? | A **Get packs** link opens the pack repository's releases page. Users download a pack and import it in Settings. The apps don't download packs (§5.5). |
| Where does the trainer live? | In this repository, as a project the installer leaves out, so the beat schema stays in step with the apps. The pack repository holds packs and reports only (§6.1). |
| Chained or from Expert for Reduce all? | Decided by measuring how far official difficulties nest, before M3. Packs support both (§3.6). |
| Minimum gaps | Each pack's own defaults, measured from its training charts, editable per run (§3.5). |
| The density control | A centred **fewer notes ↔ more notes** slider, limited to the pack's evaluated spread (§3.4). |
| Tier | The song.ini tier isn't used: packs read the source's rank by the difficulty model instead, which is consistent where tiers follow house rules (§3.3). It's on by default, and the pack says whether it uses it. |
| Any style | Offered by every pack with styles, learned in training by hiding the style on a share of examples. It's the default style (§3.3). |
| Learned voicing and sustains | A later milestone, M7 (§10). |
| Training stack | TorchSharp with GPU acceleration where present (spec §6.5), CPU otherwise. Batched, bucketed full-sequence CRF NLL with Adam; reproducible from a seed. |
| App inference | Hand-written managed C# BiGRU+CRF in PyTorch GRU convention (parity 1e-4, checked by the trainer's `selftest`; measured 1.7e-6). ONNX Runtime is not used in the apps (§7: no new native dependencies, AOT). |
| Pack storage | One pack is one `NeuralDifficulty` model in the shared generator-model store (`IGeneratorModelStore`), file `NeuralDifficulty/{name}.neuralpack`. |

Chosen when the work started: the pack file's extension is `.neuralpack`, and the parity
tolerance of §3.3 is 1e-4. Still to be chosen: the pack repository's name and address
(for the Get packs link).
