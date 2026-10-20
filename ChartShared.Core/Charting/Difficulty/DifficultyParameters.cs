namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// The constants the difficulty model is tuned by (docs/difficulty-model.md §11). They're starting points, to be fitted
/// to rated charts; <see cref="Default"/> is what the program uses.
/// </summary>
/// <remarks>
/// Times are in milliseconds and half-lives in seconds. The references each attribute is normalized against are worked
/// out from the reference patterns here (<see cref="DifficultyReferences"/>), once for each set of parameters.
/// </remarks>
public sealed record DifficultyParameters
{
    /// <summary>The parameters the program uses.</summary>
    public static DifficultyParameters Default { get; } = new();

    // Timing and scores (§2, §5, §6).

    /// <summary>The shortest interval counted, so notes all but on top of one another don't make spikes (Δmin).</summary>
    public double MinIntervalMs { get; init; } = 20;

    /// <summary>The length of the score windows and of the peak envelope (W).</summary>
    public double WindowMs { get; init; } = 400;

    /// <summary>How much each window counts against the one before it, hardest first (ω).</summary>
    public double PeakWeight { get; init; } = 0.9;

    /// <summary>The power the attributes are combined with (p): the higher, the more the hardest dominates.</summary>
    public double PowerMean { get; init; } = 4;

    /// <summary>The longest step endurance is integrated over at once.</summary>
    public double IntegrationStepMs { get; init; } = 10;

    // Weights (§5.2).

    public double StrumWeight { get; init; } = 0.25;

    public double NoteWeight { get; init; } = 0.30;

    public double RhythmWeight { get; init; } = 0.15;

    public double PatternWeight { get; init; } = 0.15;

    public double EnduranceWeight { get; init; } = 0.15;

    // Strum difficulty (§4.1).

    public double StrumHalfLife { get; init; } = 0.35;

    public double StrumScale { get; init; } = 1.0;

    public double StrumSpeedPower { get; init; } = 1.0;

    /// <summary>The strum interval alternate strumming starts below (T_alt).</summary>
    public double AlternateStrumMs { get; init; } = 110;

    public double AlternateStrumPower { get; init; } = 1.5;

    /// <summary>How much harder a strummed chord is than a strummed single note (κ).</summary>
    public double StrummedChordFactor { get; init; } = 1.2;

    /// <summary>The interval after a HOPO or tap under which a strum costs a switch (T_sw).</summary>
    public double SwitchWindowMs { get; init; } = 150;

    public double SwitchCost { get; init; } = 2.0;

    // Note difficulty (§4.2).

    public double NoteHalfLife { get; init; } = 0.5;

    public double NoteScale { get; init; } = 1.0;

    public double NoteSpeedPower { get; init; } = 1.1;

    /// <summary>How much harder the fretting hand finds a HOPO or a tap than a strum (λ_ht).</summary>
    public double HopoTapFactor { get; init; } = 1.1;

    /// <summary>The cost of each lane still sustaining that isn't in the note (w_h).</summary>
    public double HeldLaneCost { get; init; } = 0.6;

    /// <summary>The cost of sounding the same frets again without a strum (w_r).</summary>
    public double RefretCost { get; init; } = 0.5;

    /// <summary>The cost of each lane that changes (w_f).</summary>
    public double FingerCost { get; init; } = 0.5;

    /// <summary>The cost of moving into or out of an open note (w_o).</summary>
    public double OpenCost { get; init; } = 1.25;

    /// <summary>The cost of each lane the hand's centre moves (w_c).</summary>
    public double TravelCost { get; init; } = 0.3;

    /// <summary>The cost of each note of a chord landed (w_n).</summary>
    public double ChordNoteCost { get; init; } = 0.3;

    /// <summary>The cost of each gap inside a chord landed (w_g).</summary>
    public double ChordGapCost { get; init; } = 0.4;

    /// <summary>The cost of each lane a chord spans past three (w_s).</summary>
    public double ChordStretchCost { get; init; } = 0.2;

    /// <summary>Where an open note puts the hand's centre, as a lane (μ_open).</summary>
    public double OpenPosition { get; init; } = 2;

    // Rhythmic complexity (§4.3).

    public double RhythmHalfLife { get; init; } = 0.6;

    public double RhythmScale { get; init; } = 1.0;

    public double RhythmSpeedPower { get; init; } = 0.75;

    /// <summary>The cost per log₂ unit of a ratio's distance from the clean ratio it's matched to, past the dead zone (w_off).</summary>
    public double RatioOffCost { get; init; } = 10;

    /// <summary>How far, in log₂ units, a ratio can be from a clean one and still count as it (δ_off).</summary>
    public double RatioDeadZone { get; init; } = 0.03;

    /// <summary>The most a change of interval costs (Ψ_max).</summary>
    public double MaxRatioCost { get; init; } = 4;

    /// <summary>The interval past which a gap is a rest, which starts the rhythm afresh (Δ_rest).</summary>
    public double RestMs { get; init; } = 1000;

    /// <summary>How near, in beats, a note must be to a grid position to be on it (ε_b); under 1/96.</summary>
    public double BeatTolerance { get; init; } = 0.01;

    /// <summary>The cost of switching between binary and ternary feel (C_fam).</summary>
    public double FamilyChangeCost { get; init; } = 1.5;

    /// <summary>The cost of an off-beat note with nothing on the beat or half-beat before it (C_sync).</summary>
    public double SyncopationCost { get; init; } = 0.75;

    // Pattern irregularity (§4.5).

    public double PatternHalfLife { get; init; } = 0.6;

    public double PatternScale { get; init; } = 1.0;

    public double PatternSpeedPower { get; init; } = 0.75;

    /// <summary>How many recent moves the local entropy is over (N_H).</summary>
    public int EntropyWindow { get; init; } = 16;

    /// <summary>How many notes make a phrase (n_g).</summary>
    public int PhraseLength { get; init; } = 4;

    /// <summary>The longest interval, in beats, told apart from longer ones (B_rest).</summary>
    public int RestBeats { get; init; } = 4;

    /// <summary>How long it takes a phrase to be new again (T_rec).</summary>
    public double RecognitionMs { get; init; } = 30_000;

    public double EntropyWeight { get; init; } = 1.0;

    public double NoveltyWeight { get; init; } = 1.0;

    // Endurance (§4.4).

    public double EnduranceHalfLife { get; init; } = 20;

    /// <summary>
    /// The physical load above which fatigue builds (L₀): about what strummed eighths at 120 BPM give, which can be kept
    /// up indefinitely.
    /// </summary>
    public double EnduranceThreshold { get; init; } = 20;

    /// <summary>
    /// The power the physical strain is raised to for the load, on the 0-to-100 scale. Strain grows with about the square
    /// of the note rate, so a half makes the load follow the rate itself.
    /// </summary>
    public double EnduranceLoadPower { get; init; } = 0.5;

    // Rank curve (§7, ranks from 0).

    /// <summary>The scores ranks 0 to 6 start at (θ₀ to θ₆), rising.</summary>
    public IReadOnlyList<double> RankThresholds { get; init; } = [0, 15, 28, 41, 54, 67, 80];

    /// <summary>The soft cap (θ₇): where rank 7, the first beyond realistic play, starts.</summary>
    public double SoftCap { get; init; } = 100;

    // Reference patterns (§3.3): each at the limit of realistic clean play in its attribute.

    /// <summary>The strum reference: strummed single notes on one fret, sixteenths at this tempo.</summary>
    public double StrumReferenceBpm { get; init; } = 210;

    /// <summary>The note reference: a HOPO scale up and down the frets, sixteenths at this tempo.</summary>
    public double NoteReferenceBpm { get; init; } = 240;

    /// <summary>The rhythm reference: strummed notes, a beat of sixteenths then a beat of triplets, at this tempo.</summary>
    public double RhythmReferenceBpm { get; init; } = 180;

    /// <summary>The pattern reference: strummed single notes on frets picked at random, sixteenths at this tempo.</summary>
    public double PatternReferenceBpm { get; init; } = 180;

    /// <summary>
    /// The endurance reference: strummed single notes on one fret, sixteenths at this tempo, played without end. Not the
    /// speed limit but the stamina one: a pace a strong player can keep up for a whole song, but only just.
    /// </summary>
    public double EnduranceReferenceBpm { get; init; } = 160;

    /// <summary>What's kept of strain after a second, from a half-life in seconds (d = 2^(−1/h)).</summary>
    internal static double Retained(double halfLife) => Math.Pow(2, -1 / halfLife);
}
