# Guitar Hero Chart Difficulty Rating System

*Design document: difficulty criteria, online measurement model, aggregation, and visualization*

*From issue #108's attachment (`chart-difficulty-design-v2.md`), with ranks counted from 0 so they match `song.ini`'s `diff_*` tiers, and the implementation's choices in section 13. The code is in `ChartShared.Core/Charting/Difficulty`.*

---

## 1. Purpose and Scope

This system rates the highest available difficulty of a 5-fret guitar chart (typically Expert) on an open-ended rank scale. Ranks 0–6 cover charts written to be played, the same scale as `song.ini`'s `diff_*` tiers, and rank 6 is a **soft cap** reached by the hardest realistic charts. **Overcharts**, charts so extreme they sit beyond realistic play, continue upward to rank 7 and above with no upper limit. The goal is a rank that most players would agree with, produced consistently by an algorithm rather than assigned by hand.

Difficulty is measured along five attributes: **strum difficulty**, **note difficulty**, **rhythmic complexity**, **endurance**, and **pattern irregularity**. These are combined through weights into an aggregate score, and a tunable curve converts that score into the final rank.

### 1.1 Online requirement

The model is designed as an **online algorithm**. It processes notes in time order, and every quantity it defines depends only on notes at or before the current time. This means:

- The **instantaneous difficulty** of the chart, and of each individual attribute, is defined at every point in time $t$, not just as a single number for the whole song.
- Each attribute's value at any time can be computed by advancing a small state through the notes up to $t$. Strum, note, rhythm, and endurance state is fixed in size. Pattern irregularity also keeps a history of phrases it has seen, which grows with the number of distinct phrases (at most one entry per note).
- A chart-level score, a rank, or the score of any selected range is derived from the same time-varying quantities, so whole-song ratings and moment-to-moment values are always consistent with each other.

This makes the model usable for features in a charting program beyond the final rating, such as highlighting hard passages while charting, showing the difficulty profile at the playhead, or rating a selected section.

The formulas in this document define what is computed. How they are evaluated (event-driven updates, fixed time steps, exact or approximate integration, caching) is left to the implementer.

### 1.2 Difficulty target

The rating measures **the difficulty of playing the chart cleanly** (high accuracy, approaching a full combo), not the difficulty of passing it. Players disagree about rankings largely because they judge different goals: a long song with one brutal solo can be easy to pass and very hard to full-combo. Committing to one target keeps the rankings consistent, and every tuning decision should be checked against it.

### 1.3 Out of scope

Drums, vocals, 6-fret guitar, star power strategy, and player-specific settings (scroll speed, note skin, highway length) are out of scope for the first version. Section 12 lists possible extensions.

---

## 2. Notation and Preprocessing

### 2.1 Notation

| Symbol | Meaning |
|---|---|
| $i$ | Index of a note event, in time order, starting at 1. A chord is a single event. |
| $t_i$ | Onset time of note $i$, in milliseconds |
| $\Delta_i = t_i - t_{i-1}$ | Inter-onset interval (IOI) before note $i$; $\Delta_1 = \infty$ |
| $\hat\Delta_i = \max(\Delta_i, \Delta_{\min})$ | IOI guarded against near-zero values |
| $f_i = 1000 / \hat\Delta_i$ | Instantaneous note rate, in notes per second ($f_1 = 0$) |
| $F_i \subseteq \{0,1,2,3,4\}$ | Set of lanes in note $i$ (green = 0 … orange = 4); $F_i = \varnothing$ is an open note |
| $\tau_i \in \{\text{strum}, \text{hopo}, \text{tap}\}$ | Resolved note type |
| $\bot$ | "No previous note." $F_0 = \tau_0 = \bot$. Distinct from $\varnothing$, which is an open note. |
| $b_i$ | Position of note $i$ in beats (fractional), from the tempo map |
| $\Delta b_i = b_i - b_{i-1}$ | Interval before note $i$, in beats |
| $\chi_i$ | Number of lanes still sustaining at $t_i$ that are not part of $F_i$ |
| $\langle x \rangle_m$ | Distance from $x$ to the nearest multiple of $1/m$ |
| $[\,\cdot\,]$ | Indicator: 1 if the condition holds, 0 otherwise |

### 2.2 Timing

All note positions are converted from ticks to milliseconds using the tempo map. Speeds, intervals, decay, and windows all operate in milliseconds, so chart resolution has no effect, and tempo changes have no direct cost: a tempo change matters only through the note intervals it produces.

Three quantities deliberately use beat positions $b_i$ instead: the subdivision family and syncopation terms of rhythmic complexity (section 4.3), and the rhythm component of pattern tokens (section 4.5). These describe how a rhythm reads against the beat, which milliseconds cannot express. They assume the tempo map tracks the musical beat. Two consequences follow:

- Charting the same audio at a different BPM (for example, double time with halved note values) changes these three quantities, even though every note lands at the same millisecond. Everything else is unchanged.
- Live-tempo charts, whose tempo maps follow a drifting drummer, are handled well: beat positions stay clean even though millisecond intervals drift.

### 2.3 Note type resolution

Each note is classified as exactly one of strum, HOPO, or tap. Open notes are a lane state ($F_i = \varnothing$), not a type, so an open note can be strummed or a HOPO.

1. A note is a natural HOPO when it falls within the HOPO threshold of the previous note and its lane set differs from the previous note's. The threshold is derived from chart resolution and the `song.ini` values `hopo_frequency` and `eighthnote_hopo`.
2. Forced flags flip a note between strum and HOPO.
3. Tap markers override both.

Engines differ in details, such as whether chords can be natural HOPOs, so resolution should follow a specific engine's rules (an engine profile, section 12). Type resolution depends only on the current and previous note, so it fits the online model. Types are resolved over the whole chart before anything else is computed: they are a property of the chart, not of any range being scored.

Every heuristic depends on correct note types. Misclassified HOPOs are the most likely source of wrong results.

### 2.4 Cleanup

Merge duplicate notes on the same tick into a single chord, ignore notes after the chart's end event, and treat sustains shorter than the engine's sustain cutoff as unsustained. Malformed custom charts are common, and cleanup prevents them from producing artificial spikes.

### 2.5 Initialization

Every quantity is defined for the first notes of a chart or range:

- All strains and endurance start at 0.
- The first note has $\Delta_1 = \infty$, so $f_1 = 0$ and it contributes nothing through rate-scaled terms.
- The first note's predecessor is $\bot$ (no lane set, no type). It is never treated as an open note.
- Terms that need more history than exists (interval ratios, entropy, n-grams) use the fallback stated in their own definitions.

---

## 3. The Online Strain Model

Strum, note, rhythm, and pattern difficulty share one mechanism, adapted from the strain-based model used by osu!'s star rating. Endurance uses a related mechanism described in section 4.4.

### 3.1 Strain accumulation and decay

Each heuristic $k$ assigns every note a **contribution** $c_k(i) \ge 0$: how hard that specific note is in that heuristic's terms. Contributions feed a strain value $S_k$ that decays exponentially over time.

At a note onset:

$$S_k(t_i) = S_k(t_{i-1}) \cdot d_k^{\,\Delta_i / 1000} + c_k(i)$$

Between notes, for $t_j \le t < t_{j+1}$:

$$S_k(t) = S_k(t_j) \cdot d_k^{\,(t - t_j)/1000}$$

Here $d_k \in (0,1)$ is the fraction of strain retained after one second. It is often easier to tune through a half-life $h_k$ in seconds:

$$d_k = 2^{-1/h_k}$$

Because of the decay, isolated hard notes produce small, brief spikes, while dense hard passages build up high strain. This captures the observation that difficulty lives in phrases and sections, not in individual notes. Since $S_k(t)$ is defined for every $t$, the instantaneous value of each attribute can be queried at any point in the song.

### 3.2 Steady state

If the same contribution $c$ repeats at a constant interval $\Delta$, strain converges to a steady state (measured just after each note):

$$S_k^{\infty} = \frac{c}{1 - d_k^{\,\Delta/1000}}$$

Two consequences are useful during design and tuning:

- **The accumulator already accounts for density.** When $\Delta$ is short compared with the half-life, $S_k^\infty \approx c \cdot h_k f / \ln 2$, so it grows roughly in proportion to note rate. Speed terms inside contributions (the $f_i^{\gamma}$ factors below) therefore only need to capture difficulty that rises faster than linearly with speed.
- **Reference values can be defined from patterns rather than charts.** For example, the reference value for strum difficulty can be the steady-state strain of strummed single notes at a chosen speed. This gives each attribute a meaning that doesn't depend on any particular song.

### 3.3 Normalization

Raw strains of different heuristics have unrelated scales. Each is normalized to a common scale, where 100 represents a chosen reference level of difficulty:

$$\tilde S_k(t) = 100 \cdot \frac{S_k(t)}{\rho_k}$$

The reference $\rho_k$ is a fixed constant per heuristic: the steady-state strain of a **reference pattern** (section 3.2). Fixed references keep normalization online and stable: adding new charts to a library never changes existing values.

Choose each reference pattern at **the limit of realistic clean play in that attribute**: something a strong player can sustain cleanly, but only just. With the default aggregation (section 5), a short burst at one such pattern lands in rank 5, near the soft cap; holding a physical one for minutes also builds endurance (section 4.4) and passes the cap, as that much play at the limit is beyond realistic. Values above 100 are allowed and expected: overcharts exceed the references, often in several attributes at once.

---

## 4. Difficulty Attributes

Each attribute below lists the factors that contribute to it and a formula for measuring it. The constants ($\alpha$, $w$, $\gamma$, and others) are tunable starting points, collected in section 11.

### 4.1 Strum Difficulty

**What it captures:** the demand on the strumming hand, plus the decision of when to strum.

| Factor | Why it is hard | How it is measured |
|---|---|---|
| Strum rate | Faster strumming is physically harder | Interval between consecutive **strummed** notes only; HOPOs and taps are excluded |
| Alternate strumming | Above a certain speed, players must switch from down-strumming to alternate strumming, and difficulty rises steeply | Extra factor when the strum interval drops below $T_{\text{alt}}$ |
| Strummed chords | Chords demand a cleaner, more deliberate strum | Multiplier for strummed chords |
| Strum/HOPO switching | In mixed sections, the player must constantly decide whether a note needs a strum; this is a common source of dropped combos | Cost when a strummed note closely follows a HOPO or tap |

Let $\Delta^S_i$ be the time since the previous **strummed** note (infinite for the first one), $\hat\Delta^S_i = \max(\Delta^S_i, \Delta_{\min})$, and $f^S_i = 1000/\hat\Delta^S_i$.

$$c_S(i) = \begin{cases} \alpha_S \cdot \big(f^S_i\big)^{\gamma_S} \cdot A\big(\hat\Delta^S_i\big) \cdot \kappa^{[\,|F_i| > 1\,]} + \sigma_i & \text{if } \tau_i = \text{strum} \\[4pt] 0 & \text{otherwise} \end{cases}$$

The alternate-strumming factor, continuous at $T_{\text{alt}}$:

$$A(\Delta) = \max\left(1, \frac{T_{\text{alt}}}{\Delta}\right)^{\gamma_{\text{alt}}}$$

The switching cost, continuous at the edge of its window (it rises from 0 at $\Delta_i = T_{\text{sw}}$):

$$\sigma_i = \begin{cases} \alpha_{\text{sw}} \cdot \max\left(0,\; \dfrac{T_{\text{sw}}}{\hat\Delta_i} - 1\right) & \text{if } \tau_{i-1} \in \{\text{hopo}, \text{tap}\} \\[6pt] 0 & \text{otherwise (including } \tau_{i-1} = \bot\text{)} \end{cases}$$

Most engines let players strum HOPOs, so at slow speeds a "strum everything" strategy removes switching difficulty entirely. The window $T_{\text{sw}}$ restricts the switching cost to speeds where strumming every note is impractical.

The combined effect of the speed terms is steep. With $\gamma_S = 1$, steady-state strum strain grows roughly with $f^2$ (one factor from the contribution, one from the accumulator), and below $T_{\text{alt}}$ the alternate factor raises this to roughly $f^{3.5}$. Tune $\gamma_S$ and $\gamma_{\text{alt}}$ together.

### 4.2 Note Difficulty

**What it captures:** the demand on the fretting hand, mainly the cost of moving between lane states.

| Factor | Why it is hard | How it is measured |
|---|---|---|
| Fingers changed | More fingers moving means more to coordinate | Number of lanes that differ between consecutive notes |
| Hand travel | Shifting hand position (e.g. reaching orange) is slower than moving a finger | Distance between the average lane positions of consecutive notes |
| Chord shape | Larger and non-adjacent chords (green+yellow, red+blue) are harder to land | Count, span, and gaps within the chord |
| Open notes | Lifting every finger, then placing them again, breaks the hand's position | Fixed cost on transitions into or out of an open note |
| Chord-to-chord changes | Combines all of the above at once | Emerges from the transition cost |
| HOPO and tap execution | The fretting hand must produce the note on its own, without a strum | Multiplier for HOPOs and taps |
| Repeated HOPOs and taps | Re-sounding the same fret without a strum requires lifting and re-hammering | Fixed re-fret cost |
| Extended sustains | Held lanes lock fingers while others must move | Cost per lane still sustaining |

$$c_N(i) = \alpha_N \cdot f_i^{\,\gamma_N} \cdot \lambda_{\tau_i} \cdot \Big[\, T(F_{i-1}, F_i) \;+\; w_r\,\big[\,F_{i-1} = F_i \ne \varnothing \;\land\; \tau_i \ne \text{strum}\,\big] \;+\; w_h \, \chi_i \,\Big]$$

where $\lambda_{\text{strum}} = 1$ and $\lambda_{\text{hopo}} = \lambda_{\text{tap}} = \lambda_{\text{ht}}$.

The **transition cost** between lane sets $F$ and $G$:

$$T(F, G) = \begin{cases} \text{Shape}(G) & \text{if } F = \bot \\[4pt] 0 & \text{if } F = G \\[4pt] w_f\,|F \,\triangle\, G| \;+\; w_o\,[\,F = \varnothing \lor G = \varnothing\,] \;+\; \text{Shape}(G) \;+\; w_c\,|\mu(F) - \mu(G)| & \text{otherwise} \end{cases}$$

Here $F \triangle G$ is the set of lanes in one note but not the other, and $\mu$ is the average lane position:

$$\mu(G) = \frac{1}{|G|}\sum_{g \in G} g, \qquad \mu(\varnothing) = \mu_{\text{open}}$$

The **shape cost** of landing a chord, where $s(G) = \max G - \min G + 1$ is the chord's span:

$$\text{Shape}(G) = \begin{cases} 0 & \text{if } |G| \le 1 \\[4pt] w_n\,|G| \;+\; w_g\,\big(s(G) - |G|\big) \;+\; w_s \max\big(0,\, s(G) - 3\big) & \text{otherwise} \end{cases}$$

The term $s(G) - |G|$ counts gaps inside the chord (green+yellow has one), and the last term penalizes wide stretches. Shape is charged only for the chord being landed, so moving into a chord costs more than moving out of it.

A repeated identical **strummed** note or chord has zero fretting cost: that difficulty belongs to the strumming hand. A repeated HOPO or tap pays the re-fret cost $w_r$ instead, since it gets no help from a strum. (Repeated open HOPOs are left to the engine profile, since engines differ on whether they are playable.)

With the default weights, this one transition model produces both behaviors that motivated the system, without special cases:

- **Open-note alternation** costs more than alternating between any two lanes up to two apart. Open↔yellow costs 1.75 and open↔green 2.35, against 1.3 for adjacent lanes and 1.6 for lanes two apart (green↔yellow). Wider jumps such as green↔orange (2.2) can still exceed the cheapest open alternation, which is a deliberate choice: a full-width reach is treated as a larger hand movement than an open note near the center.
- **Chord alternation** costs more than alternating between the corresponding single notes. Green+red↔red+yellow costs 1.9 against 1.3 for green↔red, and green+red↔yellow+blue costs 3.2.

These are listed as invariants in section 10.3, so they can be rechecked after any retuning.

### 4.3 Rhythmic Complexity

**What it captures:** how hard the timing is to read and execute, independent of which lanes are involved.

| Factor | Why it is hard | How it is measured |
|---|---|---|
| Changing intervals | Steady streams are easy to lock into; shifting intervals are not | Complexity of the ratio between consecutive IOIs |
| Subdivision changes | Switching between straight and triplet feel disrupts internal timing | Change in subdivision family between consecutive notes |
| Syncopation | Off-beat notes are harder to place without an anchor on the beat | Notes on weak subdivisions with no note on the preceding beat or half-beat |

Tempo changes have no separate cost. A tempo change that alters note spacing is already measured by the interval ratios; one that doesn't (a tempo-map correction, a double-time marker, a live-tempo adjustment) isn't felt by the player.

**Interval ratios.** For $i \ge 2$, let $r_i = \max(\Delta_i, \Delta_{i-1}) / \min(\Delta_i, \Delta_{i-1})$. Its complexity is scored against a set $\mathcal{Q}$ of ratios that arise between common note values, using Tenney height $\operatorname{TH}(p/q) = \log_2(pq)$ (for a fraction in lowest terms), a standard measure of ratio complexity from music theory:

$$\mathcal{Q} = \left\{1,\; \tfrac{4}{3},\; \tfrac{3}{2},\; 2,\; 3,\; 4,\; 6,\; 8\right\}$$

$$\Psi(r) = \min_{q \in \mathcal{Q}} \Big[\, \operatorname{TH}(q) \;+\; w_{\text{off}} \cdot \max\big(0,\; |\log_2 r - \log_2 q| - \delta_{\text{off}}\big) \,\Big]$$

The minimum chooses the **lowest-cost** match, which keeps $\Psi$ continuous. The dead zone $\delta_{\text{off}}$ ignores small timing deviations, and $w_{\text{off}}$ is set high enough that every ratio in $\mathcal{Q}$, played exactly, is matched to itself. With the defaults:

| Ratio | Example | $\Psi$ |
|---|---|---|
| 1:1 | steady stream | 0 |
| 2:1 | 8ths to 16ths | 1 |
| 3:1 | quarters to 8th triplets | 1.58 |
| 4:1 | quarter to 16th | 2 |
| 3:2 | 8ths to 8th triplets | 2.58 |
| 4:3 | 8th triplets to 16ths | 3.58 |
| 1:1, 2% off | timing jitter | 0 |
| 1:1, 5% off | heavier jitter | 0.40 |
| 5:4 | off-grid (not in $\mathcal{Q}$) | 2.92 |

Ratios outside $\mathcal{Q}$ (quintuplets, septuplets, unquantized timing) are scored as imprecise versions of their cheapest neighbor, so they cost more than a clean ratio of similar size. The largest values occur near 5:1 (about 4.9), so the cap below is active.

A very long interval is a rest, which restarts the rhythm rather than complicating it:

$$\Psi'(r_i) = \begin{cases} 0 & \text{if } i = 1 \text{ or } \max(\Delta_i, \Delta_{i-1}) > \Delta_{\text{rest}} \\ \min\big(\Psi(r_i), \Psi_{\max}\big) & \text{otherwise} \end{cases}$$

Since $\Delta_1 = \infty$, the second note automatically falls under the rest case.

**Subdivision families.** Let $u_i = b_i - \lfloor b_i \rfloor$ be the note's position within its beat and $\epsilon_b$ a tolerance in beats. Classify each note, checking in order:

$$\phi_i = \begin{cases} \text{neutral} & \text{if } \langle u_i \rangle_2 < \epsilon_b \quad \text{(on the beat or half-beat)} \\ \text{binary} & \text{if } \langle u_i \rangle_{16} < \epsilon_b \\ \text{ternary} & \text{if } \langle u_i \rangle_{12} < \epsilon_b \\ \text{other} & \text{otherwise} \end{cases}$$

The closest binary and ternary grid points are $1/48$ beat apart, so $\epsilon_b$ must stay below $1/96 \approx 0.0104$ beats for the classes to be unambiguous. The default of 0.01 beats is about 5 ms at 120 BPM, and covers tick-quantized charts at any common resolution.

A change between two non-neutral families is costly. Neutral positions belong to every feel, so moving to or from them is free:

$$\text{Fam}_i = \big[\, \phi_{i-1} \ne \phi_i \;\land\; \phi_{i-1} \ne \text{neutral} \;\land\; \phi_i \ne \text{neutral} \,\big] \qquad (\text{Fam}_1 = 0)$$

A switch from straight to triplet feel usually changes the interval ratio too (3:2), so it is charged by both $\Psi'$ and $\text{Fam}$. This is intentional: the ratio measures the change in spacing, and the family term adds the cost of resetting the internal grid, which a spacing change within one feel (such as 8ths to 16ths) doesn't require.

**Syncopation.** Let $\beta_i = \lfloor 2b_i \rfloor / 2$ be the most recent beat or half-beat position at or before $b_i$:

$$\text{Sync}_i = \big[\, \phi_i \ne \text{neutral} \;\land\; \text{no earlier note } j \text{ has } |b_j - \beta_i| < \epsilon_b \,\big]$$

**Contribution.** Rhythmic complexity matters more as speed rises (a complex rhythm at slow speed is easy to read), so the terms are scaled by note rate:

$$c_R(i) = \alpha_R \cdot f_i^{\,\gamma_R} \cdot \Big[\, \Psi'(r_i) + C_{\text{fam}}\,\text{Fam}_i + C_{\text{sync}}\,\text{Sync}_i \,\Big]$$

### 4.4 Endurance

**What it captures:** how long the hands have been working hard without enough recovery: the physical stamina a long run of steady strumming or fretting takes, even well below the speed limit.

Endurance is not built from per-note contributions. It is derived from the **physical load**: the strum and note strains, the two hands' work, combined with the weighted power mean $M_p$ (defined in section 5) and raised to a power $q$ on the 0-to-100 scale:

$$L(t) = 100 \left( \frac{M_p\big(\tilde S_S(t), \tilde S_N(t)\big)}{100} \right)^{q}$$

Rhythmic complexity and pattern irregularity are left out: they are hard to read, not tiring to play. Strain grows with about the square of the note rate (section 3.2), so with $q = 1/2$ the load follows how fast the hands are working. Strummed eighths at 185 BPM, about six strums a second, have a strum strain near 11 but a load near 28, so steady strumming at a moderate pace counts for as much as it tires.

Endurance strain $\Phi$ is a leaky integral of the load above a threshold $L_0$. Time spent above the threshold builds fatigue, and fatigue recovers with half-life $h_E$:

$$\Phi(t) = \int_{0}^{t} \max\big(0,\, L(u) - L_0\big) \cdot d_E^{\,(t-u)/1000} \; \frac{du}{1000}, \qquad d_E = 2^{-1/h_E}$$

Equivalently, as a differential equation that can be advanced online:

$$\frac{d\Phi}{dt} = \frac{1}{1000}\Big[\max\big(0,\, L(t) - L_0\big) + \Phi(t) \ln d_E\Big]$$

For a constant load $L > L_0$, endurance converges to:

$$\Phi^\infty(L) = \frac{(L - L_0)\, h_E}{\ln 2}$$

Endurance is normalized like the other attributes, $\tilde E(t) = 100\,\Phi(t)/\rho_E$. Its reference is not the strum reference, which marks the speed limit, but a **stamina limit**: the **endurance reference pattern**, strummed single notes in sixteenths at 160 BPM, played indefinitely. That is a pace a strong player can keep up for a whole song, but only just. $\rho_E$ is the value $\Phi$ settles at under that pattern; because $L$ oscillates between notes, it is computed by running the pattern to steady state. So $\tilde E = 100$ means "sustaining the limit of realistic stamina for a whole song."

With the defaults, strummed single notes moving between two frets, held for four minutes:

| Held for four minutes | Steady-state $\tilde E$ | Chart rank |
|---|---|---|
| Eighths at 120 BPM | 0 | 0.4 |
| Eighths at 185 BPM | 23 | 1.0 |
| Sixteenths at 150 BPM | 83 | 3.9 |
| Sixteenths at 170 BPM | 118 | 5.5 |
| Sixteenths at 210 BPM (the strum reference) | 196 | 8.3 |
| The same, for only 15 seconds | 65 | 5.4 |

The threshold $L_0$ is **absolute**, not relative to the chart's own peak, so a long but easy song never builds endurance strain simply because it never stops. The default of 20 is about the load of strummed eighths at 120 BPM, a pace that can be kept up indefinitely. The half-life $h_E$ is much longer than the other attributes' (tens of seconds rather than fractions of a second), which is what separates stamina from burst difficulty.

### 4.5 Pattern Irregularity

**What it captures:** how predictable the chart is. A repeated riff is learned within a couple of repetitions; a non-repeating run at the same speed must be read note by note.

| Factor | Why it is hard | How it is measured |
|---|---|---|
| Local unpredictability | Many different movements in a short span cannot be grouped into a pattern | Normalized entropy of recent **move** tokens |
| Novelty | A phrase seen earlier in the song is easier the second time | Time since the current phrase last appeared |
| Irregular runs | Runs that change direction unpredictably are harder than scales | Move tokens: a scale uses two moves (up and down), an irregular run many |

**Rhythm component.** Both token types below include the note's interval in beats, quantized to the $1/48$-beat grid that contains every binary and ternary position from section 4.3, and capped so that all long gaps share one bucket:

$$\iota_i = \min\Big(\operatorname{round}\big(48\,\Delta b_i\big),\; 48\,B_{\text{rest}}\Big), \qquad \iota_1 = \bot$$

Using beats rather than milliseconds keeps tokens unchanged under speed modifiers and under the small interval drift of live-tempo charts, so "same lanes, different rhythm" is a different pattern, but "same rhythm, slightly different milliseconds" is not.

**Move tokens** describe how the hand moves into each note, independent of where on the neck it happens:

$$m_i = \Big(\mu(F_i) - \mu(F_{i-1}),\; |F_i|,\; s(F_i),\; \tau_i,\; \iota_i\Big)$$

where $s(F) = |F|$ when $|F| \le 1$, and the first component is $\bot$ for $i = 1$. A trill alternates two moves; a scale up and down uses two moves; a shuffled run uses many. Because the moves depend on note order, two passages with the same notes in different orders can score differently.

**Local entropy.** Over the last $N' = \min(i, N_H)$ move tokens (including $m_i$), with $p_m$ the fraction of those tokens equal to $m$:

$$\hat H_i = \begin{cases} 0 & \text{if } N' < 2 \\[4pt] \dfrac{-\sum_m p_m \log_2 p_m}{\log_2 N'} & \text{otherwise} \end{cases}$$

This ranges from 0 (one movement repeated) to 1 (every movement different). A long riff that repeats can still have high local entropy; recognizing the repetition is the job of novelty.

**Novelty** uses **phrase tokens**, which keep the actual lanes so that a riff is recognized only when it is truly repeated:

$$z_i = \big(F_i,\; \tau_i,\; \iota_i\big)$$

Let $\pi_i = (z_{i-n_g+1}, \dots, z_i)$ be the n-gram ending at note $i$, and $t^{\text{last}}(\pi_i)$ the most recent time that same n-gram previously ended:

$$\nu_i = \begin{cases} 1 & \text{if } i < n_g \text{ or } \pi_i \text{ has not occurred before} \\ 1 - e^{-(t_i - t^{\text{last}}(\pi_i))/T_{\text{rec}}} & \text{otherwise} \end{cases}$$

A phrase repeated immediately has novelty near 0; one last seen long ago approaches 1 again.

**Contribution.** As with rhythm, irregularity matters more at speed:

$$c_P(i) = \alpha_P \cdot f_i^{\,\gamma_P} \cdot \big(\, w_H \hat H_i + w_\nu \nu_i \,\big)$$

---

## 5. Instantaneous Combined Difficulty

### 5.1 Weighted power mean

Attributes are combined with a weighted power mean:

$$M_p(x_1, \dots, x_m) = \left( \frac{\sum_k w_k\, x_k^{\,p}}{\sum_k w_k} \right)^{1/p}$$

The power mean always lies between the weighted average ($p = 1$) and the largest input ($p \to \infty$). A high exponent ($p = 4$ by default) lets the hardest attribute dominate, so a moment that is extreme in one attribute is diluted only mildly by being easy in the others. This matches player perception: a passage of pure fast strumming feels hard even if its lane changes are trivial.

With the default weights, one attribute at its reference (100) and the rest at 0 gives:

| Attribute at 100 | Short burst ($\tilde E = 0$) | Sustained (endurance at steady state) |
|---|---|---|
| Strum | 70.7 | about 125 |
| Note | 74.0 | about 130 |
| Rhythm or pattern | 62.2 | 62.2 |

For comparison, $p = 1.5$ gives 40 for the strum case and 28 for rhythm or pattern. A short burst at one reference sits in rank 5 under the placeholder thresholds (section 7.1), approaching the soft cap (section 3.3). Held for minutes, the strum or note reference also far exceeds the stamina limit, and the chart is an overchart. Rhythm and pattern put no load on the hands, so they build no endurance (section 4.4). Endurance at its own reference, with little else hard, gives about 62, as rhythm or pattern alone do.

A higher $p$ also makes $D$ less sensitive to correlated attributes, since only the largest input matters much, but it does not remove double counting (section 5.2).

### 5.2 Instantaneous difficulty

The instantaneous overall difficulty combines all five attributes:

$$D(t) = M_p\big(\tilde S_S(t),\, \tilde S_N(t),\, \tilde S_R(t),\, \tilde S_P(t),\, \tilde E(t)\big)$$

There is no circularity: endurance depends on the physical load $L$ from section 4.4, which only strum and note make up.

**Starting weights:**

| Attribute | Weight | Rationale |
|---|---|---|
| Strum difficulty | 0.25 | Primary physical limit for most players |
| Note difficulty | 0.30 | Primary limit in HOPO, tap, and chord-heavy passages |
| Rhythmic complexity | 0.15 | Matters most in combination with speed, which is already reflected elsewhere |
| Endurance | 0.15 | Separates sustained difficulty from short bursts |
| Pattern irregularity | 0.15 | Separates readable passages from chaotic ones |

The load $L$ uses the same weights for its four attributes. The power mean divides by the sum of the weights it is given, so no separate renormalization is needed.

**Watch for double counting.** Endurance is derived from strum and note, and pattern irregularity tends to correlate with note difficulty. During calibration, check the correlation between attributes across a set of charts. If two correlate very strongly, reduce one weight or adjust one heuristic so it measures something more distinct.

### 5.3 Instantaneous rank-equivalent

Raw $D(t)$ has a sawtooth shape (section 5.4), while chart scores are built from window peaks (section 6.1). To keep the two comparable, the rank-equivalent is computed from the peak envelope $\bar D$ with the same window length $W$:

$$r(t) = \mathcal{R}\big(\bar D(t)\big)$$

This answers "how hard is the chart right here, in tier terms?" For example, $r(t) = 5.3$ during a solo means that passage is rank-5 material, and $r(t) = 7.4$ means overchart material. This is meaningful because of a property of the chart score (section 6): a chart whose window peaks all equal $D$ receives a score of exactly $D$, and $\bar D(t)$ is the peak a window ending at $t$ would record.

### 5.4 Readable instantaneous values

Raw strain has a sawtooth shape: it jumps at each note and decays until the next one. For most uses, a smoother quantity is more convenient. Two options, both computable online:

- **Peak envelope:** the maximum over a trailing window of length $W$:
  $$\bar D(t) = \max_{u \in [t-W,\, t]} D(u)$$
- **Smoothed value:** an exponential moving average of $D$ with time constant $\tau_{\text{ema}}$.

The same options apply to each attribute's $\tilde S_k(t)$. Graphs and readouts should state which one they show. Rank-equivalents always use the peak envelope (section 5.3).

---

## 6. Chart and Range Scores

### 6.1 Peak-weighted score

Windows of length $W$ (default 400 ms) form a fixed grid starting at time 0 of the song: window $j$ covers $[jW, (j+1)W)$. Using one global grid means a passage gets the same window peaks whether it is scored as part of the chart or as a selected range.

To summarize a span $[a, b)$, take every grid window that overlaps it, clipped to the span (so the first and last windows may be partial), and record the peak value of $D$ within each:

$$P_j = \max_{t \,\in\, \text{window } j \,\cap\, [a,b)} D(t)$$

Sort the $J$ window peaks in descending order, $P_{(1)} \ge P_{(2)} \ge \dots \ge P_{(J)}$, and take a geometrically weighted average:

$$\text{Score}(a, b) = \frac{\sum_{m=1}^{J} \omega^{\,m-1}\, P_{(m)}}{\sum_{m=1}^{J} \omega^{\,m-1}}$$

This implements the principle that **difficulty lives in the hardest part of the song** without letting a single 400 ms flourish define the whole chart. The effective length of the "hardest part" is about $W / (1 - \omega)$: with $\omega = 0.9$, the hardest ~4 seconds carry about 65% of the score, and the hardest ~9 seconds carry about 90% of it. Raising $\omega$ toward 0.95 lengthens that span. Because the average is normalized, a constant $D$ scores exactly $D$.

**Finding window peaks.** Between note onsets every strain decreases, so $D$ can only rise through endurance, which changes slowly (half-life 20 s). Evaluating $D$ at note onsets and at window boundaries therefore finds each window's peak to within a negligible error. Implementations that need exact peaks can also sample inside windows.

### 6.2 Chart score and rank

The **chart score** is the score of the whole song, and the **chart rank** comes from passing it through the rank curve:

$$\text{ChartScore} = \text{Score}(0,\, t_{\text{end}}), \qquad \text{Rank} = \big\lfloor \mathcal{R}(\text{ChartScore}) \big\rfloor$$

The rank has no upper bound (section 7).

### 6.3 Attribute scores

Each attribute's chart-level score applies the same formula to its own normalized strain $\tilde S_k(t)$ (or $\tilde E(t)$). These five scores form the spider graph.

The attribute scores do not combine into the chart score through the power mean, and that is intentional. The chart score is computed from $D(t)$, so it reflects what is hard **at the same time**. A chart with a hard strumming section and a separate hard HOPO section will show high strum and note scores, but its chart score reflects the harder of the two passages rather than both added together. If you prefer charts that test many different skills to rank higher, the alternative is to compute the chart score as the power mean of the five attribute scores instead.

### 6.4 Online and range use

The score is defined for **any range**, not just the whole song, which supports features such as rating a selected passage. There are two ways to treat the state carried into a range:

- **In context:** use the state as it is at time $a$, including strain buildup, endurance, and phrase history from earlier notes. This measures the passage as it is experienced during the song.
- **In isolation:** reset strains, endurance, and phrase history at $a$, and treat the first note in the range as having no predecessor ($\bot$, section 2.5). Note types keep their whole-chart resolution (section 2.3). This measures the passage as it would be experienced when practiced on its own.

The score can also be maintained as a running value while notes are processed. Because the weights decay geometrically, windows beyond the top $K$ contribute almost nothing, where:

$$K = \left\lceil \frac{\ln \varepsilon}{\ln \omega} \right\rceil$$

for a chosen relative precision $\varepsilon$. With $\omega = 0.9$ and $\varepsilon = 10^{-3}$, about 66 windows matter, so a running chart score needs only the top $K$ window peaks plus a running total of the weights.

---

## 7. Rank Curve

The rank curve $\mathcal{R}$ converts a score into a continuous rank. It is deliberately the most hand-tuned part of the system: the attributes measure difficulty, and the curve encodes the judgment of where each tier begins.

The curve has no ceiling. Ranks 0–6 are calibrated against realistic charts, with rank 6 as a **soft cap**: the hardest charts written to be played should land in rank 6. Above that, the curve continues linearly, and **each rank above 6 spans the same score width as rank 6**. Overcharts therefore receive 7, 8, and beyond, and remain sortable however extreme they are.

### 7.1 Piecewise linear curve (recommended)

Choose eight thresholds $\theta_0 < \theta_1 < \dots < \theta_7$, where rank $j$ begins at $\theta_j$, and $\theta_7$ is the soft cap where rank 7 begins. Then:

$$\mathcal{R}(x) = \begin{cases} 0 & \text{if } x \le \theta_0 \\[4pt] j + \dfrac{x - \theta_j}{\theta_{j+1} - \theta_j} & \text{if } \theta_j \le x < \theta_{j+1}, \quad j = 0, \dots, 5 \\[10pt] 6 + \dfrac{x - \theta_6}{\theta_7 - \theta_6} & \text{if } x \ge \theta_6 \end{cases}$$

The last case is rank 6's segment extended without limit, so rank $j \ge 7$ begins at $\theta_6 + (j - 6)(\theta_7 - \theta_6)$. The integer rank is $\lfloor \mathcal{R}(x) \rfloor$, and the continuous value (e.g. 5.42 or 7.81) can be shown for sorting within a tier.

**Starting thresholds (placeholders until calibrated):**

| Rank | Begins at score |
|---|---|
| 0 | $\theta_0 = 0$ |
| 1 | $\theta_1 = 15$ |
| 2 | $\theta_2 = 28$ |
| 3 | $\theta_3 = 41$ |
| 4 | $\theta_4 = 54$ |
| 5 | $\theta_5 = 67$ |
| 6 | $\theta_6 = 80$ |
| 7 (soft cap) | $\theta_7 = 100$ |
| 8, 9, … | 120, 140, … (derived: width 20) |

Expert charts span an enormous range, and custom charts cluster toward the top of it, so the calibrated thresholds will likely be uneven. The placeholders are evenly spaced only as a neutral starting point. $\theta_6 = 80$ is placed where a single attribute sustained at its reference lands (section 5.1), and reaching rank 7 requires either well beyond-reference difficulty in one attribute or reference-level difficulty in several at once.

### 7.2 Parametric alternative

With fewer knobs, a single-exponent curve also works. Let $x_{\text{cap}}$ be the score where rank 7 begins:

$$\mathcal{R}(x) = \begin{cases} 7\left(\dfrac{x}{x_{\text{cap}}}\right)^{\xi} & \text{if } x \le x_{\text{cap}} \\[10pt] 7 + \dfrac{x - x_{\text{cap}}}{x_{\text{cap}} - x_6} & \text{if } x > x_{\text{cap}} \end{cases}, \qquad x_6 = x_{\text{cap}} \left(\tfrac{6}{7}\right)^{1/\xi}$$

Here $x_6$ is where rank 6 begins, so the linear part above the cap again uses rank 6's width. The exponent sets how score ranges are shared between ranks:

- $\xi > 1$ gives low ranks wide score ranges and high ranks narrow ones. With $\xi = 2$, rank 0 covers the bottom 38% of $[0, x_{\text{cap}}]$ and rank 6 covers only the top 7%.
- $\xi < 1$ does the opposite. With $\xi = 0.5$, rank 6 covers the top 27%.

Because overchart ranks reuse rank 6's width, $\xi > 1$ also makes overcharts climb ranks quickly. This curve is easier to tune but cannot correct a single misplaced tier boundary, which the piecewise curve can.

### 7.3 Tuning process

1. Select 3–5 anchor charts per rank that are broadly agreed upon, plus a set of overchart anchors.
2. Compute the chart score for every anchor and plot them, colored by expected rank.
3. Place each threshold in the gap between the highest-scoring anchor of one rank and the lowest-scoring anchor of the next. Place the soft cap $\theta_7$ between the hardest realistic anchors and the easiest overcharts.
4. Check the overchart anchors against the linear extension. If their computed ranks are consistently compressed or spread compared with community judgment, revisit the width rule rather than the thresholds below the cap.
5. If anchors overlap heavily and no gap exists, the problem is in the attributes or weights, not the curve. Fix that first.

---

## 8. Using the Model in a Charting Program

### 8.1 State

Each attribute needs only a small amount of state to advance from one note to the next:

| Attribute | State carried forward |
|---|---|
| Strum | $S_S$; time of the last strummed note; type and time of the previous note |
| Note | $S_N$; previous lane set (or $\bot$); end times of active sustains |
| Rhythm | $S_R$; previous two onset times; previous subdivision family; recent beat positions within half a beat |
| Pattern | $S_P$; last $N_H$ move tokens; last $n_g - 1$ phrase tokens; phrase history |
| Endurance | $\Phi$ (the physical load $L$ is computed from strum and note) |
| Score | Top $K$ window peaks, running weight total, current window peak |

The phrase history is the only state that grows. Store it as, for each n-gram, the list of times it ended, in time order. The last occurrence before any time is a binary search, a snapshot of the history is just a point in time, and rolling back to a snapshot removes the entries after it. Nothing is ever pruned, so results are exact.

Because the rest of the state is small, the program can keep periodic snapshots of it (for example, every few seconds of song time). A query at any time $t$ then starts from the nearest snapshot before $t$ rather than from the beginning of the song.

### 8.2 Causality and edits

Every value at time $t$ depends only on notes at or before $t$. Let $t_e$ be the earliest time an edit touches (for a moved note, the earlier of its old and new positions). Editing therefore never changes any value before $t_e$.

After an edit, the change in each strain decays at that attribute's rate. The effect of an edit on attribute $k$ falls below a relative tolerance $\varepsilon$ after roughly:

$$\text{horizon}_k \approx h_k \log_2(1/\varepsilon) \text{ seconds}$$

For the slowest short-term attribute ($h = 0.6$ s) and $\varepsilon = 10^{-3}$, that is about 6 seconds.

Recomputation starts at the nearest snapshot before $t_e$. It must run at least through the first unedited note after the edit, because that note's type, interval, and transition all depend on the note before it. After that, it can stop once the recomputed state matches the previous state within tolerance. Three cases reach further:

- **Pattern irregularity.** Local entropy recovers after $N_H$ notes. Novelty can change later wherever an n-gram that the edit added or removed occurs again, so recomputation continues until each such n-gram has reoccurred (after which its last-seen time is the same as before), or to the end of the song.
- **Endurance** has a long half-life, so its horizon can be minutes. In practice, small edits change $\Phi$ only slightly, and recomputation can stop once the difference is negligible.
- **Tempo map edits** shift the times of every later note, so everything after the edit must be recomputed.

### 8.3 Anticipation

Because the model is causal, difficulty is credited at the note where it is experienced, not where the player starts preparing for it. A hard transition shows up at its second note. For display purposes, a small backward shift can be applied to graphs if passages should appear to start slightly earlier.

---

## 9. Visualizations

### 9.1 Difficulty graph

Shows how difficulty changes over the course of the song.

| Element | Specification |
|---|---|
| X axis | Song time (mm:ss) |
| Y axis | Difficulty on the normalized scale, from 0 to the larger of $\theta_7$ and the highest value shown, so overcharts are never clipped |
| Main line | Overall difficulty $D(t)$, shown as the peak envelope or smoothed value (section 5.4) |
| Attribute lines | Toggleable lines for each attribute's normalized strain, including endurance |
| Section markers | Vertical guides and labels from the chart's section events (e.g. "Solo 1") |
| Rank guide lines | Horizontal lines at each rank threshold, including rank 7 and above when they fall within the visible range; a passage between the rank 5 and rank 6 lines is rank-5 material |
| Score contributors | Highlighting on the windows that contributed most to the chart score |
| Tooltip | Time, section name, $D(t)$, rank-equivalent $r(t)$, and the attribute contributing most at that moment |

Because the graph is drawn from the same online quantities as the chart score, it can update immediately as notes are edited.

### 9.2 Spider graph

Shows difficulty in each individual attribute.

| Element | Specification |
|---|---|
| Axes | Five axes, one per attribute, with a marked ring at 100 (the reference level) |
| Axis order | Fixed: Strum, Rhythm, Endurance, Pattern, Note. Since the axes form a loop, Note and Strum are adjacent, which places the two physical attributes side by side and makes "physical" and "reading" charts visually distinct |
| Polygon | Attribute scores, unclamped. When any value exceeds 100, the plot expands to fit it, with the 100 ring still marked, so overcharts are visibly beyond the reference |
| Comparison overlay | Optional second polygon: the typical profile for the chart's rank, or another chosen chart |

The spider graph supports three scopes, all from the same formulas:

- **Chart:** the five attribute scores for the whole song (section 6.3).
- **Selection:** the five attribute scores for a selected range (section 6.4).
- **Playhead:** the instantaneous attribute values $\tilde S_k(t)$ and $\tilde E(t)$ at the current position, using the peak envelope so the shape doesn't flicker between notes.

---

## 10. Calibration and Validation

### 10.1 Ground truth sources

| Source | Strength | Weakness |
|---|---|---|
| Anchor charts | Directly encodes the intended rankings | Small sample; reflects one person's judgment |
| Charter ratings (`diff_guitar` in `song.ini`) | Large volume across chart packs | Individually inconsistent; useful only in aggregate |
| Pairwise player votes ("which is harder?") | Most reliable form of human judgment; fit with a Bradley-Terry or Elo model | Requires collecting votes |
| Score and full-combo data | Most objective signal | Rarely available, and almost absent for overcharts |

### 10.2 Metrics

For each version of the weights and constants, track the Spearman rank correlation against each ground truth source, the percentage of pairwise votes the system agrees with, and a table of expected vs. computed rank for the anchors. A reasonable target is that no realistic anchor is off by more than one rank and most are exact. For overcharts, where agreed ranks are rarer, the main target is correct ordering.

### 10.3 Sanity tests

These should hold for any tuning:

| Test | Expected behavior |
|---|---|
| Speed up a chart by 10% | Strum, note, and rhythm scores increase or stay the same. Pattern irregularity, endurance, and the chart score normally increase, but can dip slightly because novelty and fatigue depend on elapsed real time |
| Add a long, easy intro | Endurance does not increase. The chart score drops by less than $\varepsilon$ (relative) when the chart already spans at least $K$ windows (about 26 s); shorter charts can drop more |
| Duplicate the hardest section back to back | Endurance increases; other attribute scores rise only modestly |
| Shift all notes up one lane (where possible) | Scores stay similar, apart from open-note and hand-position effects |
| Change chart resolution without changing timing | All values identical |
| Re-chart at double BPM with the same note times | Strum and note scores identical; rhythm and pattern change only through their beat-based terms |
| Force a HOPO run to be strummed | Strum difficulty increases; note difficulty changes only by the type multiplier and the re-fret cost |
| Compute $D(t)$ incrementally vs. from the start of the song | Identical values at every $t$, apart from differences caused by the chosen integration method |
| Edit notes at or after $t_e$ | No value before $t_e$ changes |
| Open-note and chord alternation | Open↔any lane costs more than green↔yellow; each chord alternation costs more than the corresponding single-note alternation (section 4.2) |
| Clean ratios | Every ratio in $\mathcal{Q}$, played exactly, scores its own Tenney height (section 4.3) |
| Reference patterns | A short burst of each single-attribute reference pattern lands in rank 5; the endurance reference, held, settles at $\tilde E = 100$ (sections 4.4 and 5.1) |

---

## 11. Parameter Reference

All values are starting points to be tuned during calibration. Attribute weights are in section 5.2 and rank thresholds in section 7.1.

| Parameter | Default | Used in |
|---|---|---|
| $\Delta_{\min}$ | 20 ms | All attributes (guards against near-zero intervals) |
| Sustain cutoff | From the engine profile | Cleanup |
| $W$ (window length) | 400 ms | Window peaks, peak envelope |
| $\omega$ (peak weight) | 0.9 | Chart and range scores |
| $\varepsilon$ (relative precision) | $10^{-3}$ | Top-$K$ windows, edit horizon |
| $\tau_{\text{ema}}$ (smoothing time constant) | 150 ms | Smoothed display values |
| Snapshot interval | 5 s | Charting program state |
| $h_S$ (strum half-life) | 0.35 s | Strum difficulty |
| $h_N$ (note half-life) | 0.5 s | Note difficulty |
| $h_R$ (rhythm half-life) | 0.6 s | Rhythmic complexity |
| $h_P$ (pattern half-life) | 0.6 s | Pattern irregularity |
| $h_E$ (endurance half-life) | 20 s | Endurance |
| $\gamma_S$ | 1.0 | Strum difficulty |
| $T_{\text{alt}}$ | 110 ms | Strum difficulty |
| $\gamma_{\text{alt}}$ | 1.5 | Strum difficulty |
| $\kappa$ (strummed chord multiplier) | 1.2 | Strum difficulty |
| $T_{\text{sw}}$ (switching window) | 150 ms | Strum difficulty |
| $\alpha_{\text{sw}}$ | 2.0 | Strum difficulty |
| $\gamma_N$ | 1.1 | Note difficulty |
| $\lambda_{\text{ht}}$ (HOPO/tap multiplier) | 1.1 | Note difficulty |
| $w_h$ (held lane cost) | 0.6 | Note difficulty |
| $w_r$ (re-fret cost) | 0.5 | Note difficulty |
| $w_f,\ w_o,\ w_c$ | 0.5, 1.25, 0.3 | Note transition cost |
| $w_n,\ w_g,\ w_s$ | 0.3, 0.4, 0.2 | Chord shape cost |
| $\mu_{\text{open}}$ | 2 | Note transition cost, move tokens |
| $\mathcal{Q}$ (ratio set) | $\{1, 4/3, 3/2, 2, 3, 4, 6, 8\}$ | Rhythmic complexity |
| $w_{\text{off}}$ | 10 | Rhythmic complexity |
| $\delta_{\text{off}}$ (dead zone) | 0.03 (log₂ units, about ±2%) | Rhythmic complexity |
| $\Psi_{\max}$ | 4 | Rhythmic complexity |
| $\Delta_{\text{rest}}$ | 1000 ms | Rhythmic complexity |
| $\epsilon_b$ | 0.01 beats (must stay below 1/96) | Rhythmic complexity |
| $C_{\text{fam}},\ C_{\text{sync}}$ | 1.5, 0.75 | Rhythmic complexity |
| $\gamma_R$ | 0.75 | Rhythmic complexity |
| $N_H$ (entropy window) | 16 notes | Pattern irregularity |
| $n_g$ (n-gram length) | 4 | Pattern irregularity |
| $B_{\text{rest}}$ (longest distinct interval) | 4 beats | Pattern irregularity |
| $T_{\text{rec}}$ | 30 s | Pattern irregularity |
| $w_H,\ w_\nu$ | 1.0, 1.0 | Pattern irregularity |
| $\gamma_P$ | 0.75 | Pattern irregularity |
| $L_0$ (endurance threshold) | 20 | Endurance |
| $q$ (physical load power) | 0.5 | Endurance |
| $p$ (power mean exponent) | 4 | Load, instantaneous difficulty |
| $\alpha_S,\ \alpha_N,\ \alpha_R,\ \alpha_P$ | 1.0 each | Overall scale; largely absorbed by $\rho_k$ |
| $\rho_S,\ \rho_N,\ \rho_R,\ \rho_P$ | Steady-state strain of each reference pattern | Normalization |
| $\rho_E$ | $\Phi$ at steady state under the endurance reference pattern (strummed sixteenths at 160 BPM) | Normalization |
| $x_{\text{cap}},\ \xi$ | — | Parametric rank curve (section 7.2), if used |

---

## 12. Open Questions and Future Work

**Engine-specific rules.** HOPO and tap behavior differs between games and engines. Type resolution should take an engine profile as input, covering HOPO rules, chord HOPOs, the sustain cutoff, and whether repeated open HOPOs are playable.

**Speed modifiers.** Many players practice at modified speeds. Rating a chart at speed $s$ only requires scaling note times by $1/s$ before evaluation. Beat positions are unchanged, so beat-based rhythm terms and pattern tokens are unchanged, and only speed-dependent quantities move.

**Overchart scaling.** Ranks above 6 currently use rank 6's score width (section 7). Once enough overcharts have community rankings, check whether a different width, or a separate width for the overchart range, fits them better.

**Player profiles.** If players' results were tracked per attribute, a player's own spider graph could be overlaid on a chart's graph to predict which charts or passages they will find hard.

**Sight-reading.** Visual density (how many notes are on screen at once at a given scroll speed) affects first-play difficulty. It is out of scope for a clean-play target but could become a sixth attribute if the target shifts toward first-attempt difficulty.

**Other instruments and difficulties.** The strain model, aggregation, and rank curve generalize to bass and 6-fret guitar with new transition costs, and to drums with entirely different per-note attributes. The instantaneous values could also support tools for lower difficulties, such as flagging passages in a Hard chart that exceed its intended tier.

---

## 13. Implementation Notes

**Where it runs.** Five-fret parts are rated by this model; drums and six-fret, which it doesn't cover, keep the earlier
note-density rating. The suggested `diff_*` value is the chart rank, uncapped, so an overchart is suggested as 7 or more.
Chart Builder shows the rank, the spider graph (chart, selection or playhead) and the difficulty graph in the Editor's
Difficulty tab, which also opens in a window of its own.

**Note types.** The model reads the game view, which resolves natural HOPOs, forced flags, taps and the sustain cutoff as
the games do (the engine profile of section 12). Notes after the `[end]` event are left out. A chord with an open note and
frets is read as its frets.

**Beats.** Beat positions are quarter notes: the tick over the chart's resolution. When most notes sit the same small distance (up to a 32nd note) off the tempo map's beats and half-beats, as in a chart synced by moving its notes rather than its tempo map, that offset is taken out of the beat positions first. Otherwise every note would read as off-grid and syncopated. Larger offsets are left alone, so a riff on the off-beats is never taken for one on the beat.

**Reference patterns.** Each reference is the mean strain just after a note, once the pattern has settled. Each is a
placeholder until calibration:

| Attribute | Reference pattern |
|---|---|
| Strum | Strummed single green notes, sixteenths at 210 BPM; measured from 30 s to 60 s |
| Note | A HOPO scale, green to orange and back, sixteenths at 240 BPM; from 30 s to 60 s |
| Rhythm | Strummed notes, a beat of sixteenths then a beat of eighth triplets, at 180 BPM; from 30 s to 60 s |
| Pattern | Strummed single notes on frets picked by a fixed xorshift, sixteenths at 180 BPM; from 60 s to 180 s |
| Endurance | Strummed single green notes, sixteenths at 160 BPM, held for fifteen endurance half-lives; measured over the 30 s after |

**Evaluation.** Each pass reads the track from its start. It takes a sample just after each note and at the start of each
400 ms window. Endurance is integrated in steps of at most 10 ms. Each step takes the load at its middle and uses the exact
solution for a constant load. Values between samples decay from the last one. A five-minute Expert track takes a few
milliseconds, so after an edit the whole track is read again, off the UI thread. The snapshots and early stopping of
section 8 aren't needed at this size. The state can be cloned, which leaves them open for later.

**Phrase history.** Only the last time each phrase ended is kept, since passes always start from the beginning.
