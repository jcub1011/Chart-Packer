using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring;

/// <summary>
/// How Reduce makes a lower difficulty of a 5-fret or GHL part (charter-spec C9 item 2). The Reduce dialog starts from the
/// defaults in Settings, and its changes apply to one run.
/// </summary>
public sealed record FretReductionRules
{
    /// <summary>The most columns a chord keeps: frets on 5-fret, columns on GHL (a white and a black on one count once).</summary>
    public int MaxChordSize { get; init; } = 5;

    /// <summary>The highest column used: a fret from 0 (green) to 4 (orange), or a GHL column from 0 to 2.</summary>
    public int HighestLane { get; init; } = 4;

    /// <summary>
    /// The thinning grid, as a snap's division (16 for 1/16): chords are on average no closer than one step, and closer only
    /// in a burst (<see cref="MaxBurst"/>). 0 thins nothing.
    /// </summary>
    public int ThinningDivision { get; init; } = 16;

    /// <summary>
    /// Whether HOPOs and taps are kept: each chord played as the source plays it, a strum, a HOPO or a tap, forced where
    /// the notes left out would change it. Otherwise forced HOPOs and taps are removed; forced strums always are kept.
    /// </summary>
    public bool KeepForcedHopoAndTap { get; init; } = true;

    /// <summary>Whether sustains shorter than a beat are kept, rather than made plain notes.</summary>
    public bool KeepShortSustains { get; init; } = true;

    /// <summary>
    /// Whether a sustain held while other notes are played, past the next chord's start, is kept, rather than made a plain
    /// note (a tap staying a tap). Null keeps it; null too in settings saved before it was added, which
    /// <see cref="ReductionDefaults.Normalized"/> fills from the shipped.
    /// </summary>
    public bool? KeepExtendedSustains { get; init; }

    /// <summary>
    /// Whether open notes are kept in a phrase that has pressed notes too, rather than each made a fret: the open green and
    /// the phrase's frets one up, so going between an open and a fret is still a move. A phrase of opens alone keeps them.
    /// Null keeps them; null too in settings saved before it was added, which <see cref="ReductionDefaults.Normalized"/>
    /// fills from the shipped.
    /// </summary>
    public bool? KeepOpenNotes { get; init; }

    /// <summary>
    /// The grid a burst's chords can be as close as, as a snap's division; no coarser than the thinning grid. Null for the
    /// thinning grid, so no burst is closer than it; null too in settings saved before it was added, which
    /// <see cref="ReductionDefaults.Normalized"/> fills from the shipped.
    /// </summary>
    public int? BurstDivision { get; init; }

    /// <summary>
    /// The most chords in a burst: any one more in a row span at least this many thinning gaps, so a burst has room around
    /// it. 1 keeps every two chords a gap apart; null for 1.
    /// </summary>
    public int? MaxBurst { get; init; }

    /// <summary>The shipped defaults for reducing a <paramref name="kind"/> part to <paramref name="target"/> (the C9 table).</summary>
    public static FretReductionRules Default(ChartInstrumentKind kind, ChartDifficulty target)
    {
        bool ghl = kind == ChartInstrumentKind.SixFret;
        return target switch
        {
            ChartDifficulty.Hard => new() { MaxChordSize = 2, HighestLane = ghl ? 2 : 4, ThinningDivision = 16, BurstDivision = 16, MaxBurst = ReductionDefaults.ShippedBurst, KeepForcedHopoAndTap = true, KeepShortSustains = true, KeepExtendedSustains = false, KeepOpenNotes = false },
            ChartDifficulty.Medium => new() { MaxChordSize = 2, HighestLane = ghl ? 2 : 3, ThinningDivision = 8, BurstDivision = 16, MaxBurst = ReductionDefaults.ShippedBurst, KeepForcedHopoAndTap = true, KeepShortSustains = true, KeepExtendedSustains = false, KeepOpenNotes = false },
            _ => new() { MaxChordSize = 1, HighestLane = ghl ? 1 : 2, ThinningDivision = 4, BurstDivision = 8, MaxBurst = ReductionDefaults.ShippedBurst, KeepForcedHopoAndTap = true, KeepShortSustains = false, KeepExtendedSustains = false, KeepOpenNotes = false },
        };
    }

    /// <summary>These rules with every value kept to what it can be for <paramref name="kind"/>, as one read from a file may not be.</summary>
    public FretReductionRules Normalized(ChartInstrumentKind kind)
    {
        int columns = kind == ChartInstrumentKind.SixFret ? 3 : 5;
        int division = ReductionDefaults.NormalizedDivision(ThinningDivision);
        return this with
        {
            MaxChordSize = Math.Clamp(MaxChordSize, 1, columns),
            HighestLane = Math.Clamp(HighestLane, 0, columns - 1),
            ThinningDivision = division,
            BurstDivision = ReductionDefaults.NormalizedBurstDivision(BurstDivision, division),
            MaxBurst = ReductionDefaults.NormalizedBurst(MaxBurst),
        };
    }
}

/// <summary>How Reduce makes a lower difficulty of drums (charter-spec C9 item 2).</summary>
public sealed record DrumReductionRules
{
    /// <summary>The most pads hit at once, the kick aside.</summary>
    public int PadsPerHit { get; init; } = 2;

    /// <summary>
    /// The thinning grid for every hit, kick and pads together, as a snap's division: hits are on average no closer than
    /// one step, and closer only in a burst (<see cref="MaxBurst"/>). 0 thins nothing.
    /// </summary>
    public int ThinningDivision { get; init; } = 16;

    /// <summary>The kick's own thinning grid, after the hits are thinned, as a snap's division. 0 keeps every kick left.</summary>
    public int KickDivision { get; init; }

    /// <summary>Whether 2× kicks are kept; only Expert can have them, so Reduce never keeps them.</summary>
    public bool KeepDoubleKick { get; init; }

    /// <summary>The grid a burst's hits can be as close as, as a snap's division; no coarser than the thinning grid. Null for the thinning grid.</summary>
    public int? BurstDivision { get; init; }

    /// <summary>The most hits in a burst: any one more in a row span at least this many thinning gaps. Null for 1.</summary>
    public int? MaxBurst { get; init; }

    /// <summary>The shipped defaults for reducing drums to <paramref name="target"/> (the C9 table).</summary>
    public static DrumReductionRules Default(ChartDifficulty target) => target switch
    {
        ChartDifficulty.Hard => new() { PadsPerHit = 2, ThinningDivision = 16, BurstDivision = 16, MaxBurst = ReductionDefaults.ShippedBurst, KickDivision = 0 },
        ChartDifficulty.Medium => new() { PadsPerHit = 2, ThinningDivision = 8, BurstDivision = 16, MaxBurst = ReductionDefaults.ShippedBurst, KickDivision = 8 },
        _ => new() { PadsPerHit = 1, ThinningDivision = 4, BurstDivision = 8, MaxBurst = ReductionDefaults.ShippedBurst, KickDivision = 4 },
    };

    /// <summary>These rules with every value kept to what it can be.</summary>
    public DrumReductionRules Normalized()
    {
        int division = ReductionDefaults.NormalizedDivision(ThinningDivision);
        return this with
        {
            PadsPerHit = Math.Clamp(PadsPerHit, 1, 5),
            ThinningDivision = division,
            BurstDivision = ReductionDefaults.NormalizedBurstDivision(BurstDivision, division),
            MaxBurst = ReductionDefaults.NormalizedBurst(MaxBurst),
            KickDivision = ReductionDefaults.NormalizedDivision(KickDivision),
            KeepDoubleKick = false,
        };
    }
}

/// <summary>
/// The default reduction rules (charter-spec §10), one set per family and target difficulty, as Settings keeps them. The
/// Reduce dialog starts from these, and its changes apply to one run only.
/// </summary>
public sealed class ReductionDefaults
{
    public FretReductionRules FiveFretHard { get; set; } = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Hard);

    public FretReductionRules FiveFretMedium { get; set; } = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Medium);

    public FretReductionRules FiveFretEasy { get; set; } = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Easy);

    public FretReductionRules SixFretHard { get; set; } = FretReductionRules.Default(ChartInstrumentKind.SixFret, ChartDifficulty.Hard);

    public FretReductionRules SixFretMedium { get; set; } = FretReductionRules.Default(ChartInstrumentKind.SixFret, ChartDifficulty.Medium);

    public FretReductionRules SixFretEasy { get; set; } = FretReductionRules.Default(ChartInstrumentKind.SixFret, ChartDifficulty.Easy);

    public DrumReductionRules DrumsHard { get; set; } = DrumReductionRules.Default(ChartDifficulty.Hard);

    public DrumReductionRules DrumsMedium { get; set; } = DrumReductionRules.Default(ChartDifficulty.Medium);

    public DrumReductionRules DrumsEasy { get; set; } = DrumReductionRules.Default(ChartDifficulty.Easy);

    /// <summary>The shipped defaults: a new set each time, so changing one never changes another's.</summary>
    public static ReductionDefaults Shipped => new();

    /// <summary>The rules for reducing a <paramref name="kind"/> part to <paramref name="target"/> (Hard, Medium or Easy).</summary>
    public FretReductionRules FretsFor(ChartInstrumentKind kind, ChartDifficulty target) => (kind, target) switch
    {
        (ChartInstrumentKind.SixFret, ChartDifficulty.Hard) => SixFretHard,
        (ChartInstrumentKind.SixFret, ChartDifficulty.Medium) => SixFretMedium,
        (ChartInstrumentKind.SixFret, _) => SixFretEasy,
        (_, ChartDifficulty.Hard) => FiveFretHard,
        (_, ChartDifficulty.Medium) => FiveFretMedium,
        _ => FiveFretEasy,
    };

    /// <summary>The rules for reducing drums to <paramref name="target"/> (Hard, Medium or Easy).</summary>
    public DrumReductionRules DrumsFor(ChartDifficulty target) => target switch
    {
        ChartDifficulty.Hard => DrumsHard,
        ChartDifficulty.Medium => DrumsMedium,
        _ => DrumsEasy,
    };

    /// <summary>A copy with <paramref name="rules"/> for reducing a <paramref name="kind"/> part to <paramref name="target"/>.</summary>
    public ReductionDefaults WithFrets(ChartInstrumentKind kind, ChartDifficulty target, FretReductionRules rules)
    {
        var copy = Copy();
        switch (kind, target)
        {
            case (ChartInstrumentKind.SixFret, ChartDifficulty.Hard): copy.SixFretHard = rules; break;
            case (ChartInstrumentKind.SixFret, ChartDifficulty.Medium): copy.SixFretMedium = rules; break;
            case (ChartInstrumentKind.SixFret, _): copy.SixFretEasy = rules; break;
            case (_, ChartDifficulty.Hard): copy.FiveFretHard = rules; break;
            case (_, ChartDifficulty.Medium): copy.FiveFretMedium = rules; break;
            default: copy.FiveFretEasy = rules; break;
        }

        return copy;
    }

    /// <summary>A copy with <paramref name="rules"/> for reducing drums to <paramref name="target"/>.</summary>
    public ReductionDefaults WithDrums(ChartDifficulty target, DrumReductionRules rules)
    {
        var copy = Copy();
        switch (target)
        {
            case ChartDifficulty.Hard: copy.DrumsHard = rules; break;
            case ChartDifficulty.Medium: copy.DrumsMedium = rules; break;
            default: copy.DrumsEasy = rules; break;
        }

        return copy;
    }

    /// <summary>
    /// A copy with every value kept to what it can be, and the shipped rules for any set the file lacks, or any value: a
    /// set saved before its bursts were added gets the shipped ones, and the shipped HOPOs and taps, where it thins as the
    /// shipped does, and no bursts where it has its own thinning. A minimum gap in milliseconds, which older settings have,
    /// is no longer read: thinning is by fractions of a beat alone.
    /// </summary>
    public ReductionDefaults Normalized()
    {
        var shipped = Shipped;
        return new ReductionDefaults
        {
            FiveFretHard = Filled(FiveFretHard, shipped.FiveFretHard).Normalized(ChartInstrumentKind.FiveFret),
            FiveFretMedium = Filled(FiveFretMedium, shipped.FiveFretMedium).Normalized(ChartInstrumentKind.FiveFret),
            FiveFretEasy = Filled(FiveFretEasy, shipped.FiveFretEasy).Normalized(ChartInstrumentKind.FiveFret),
            SixFretHard = Filled(SixFretHard, shipped.SixFretHard).Normalized(ChartInstrumentKind.SixFret),
            SixFretMedium = Filled(SixFretMedium, shipped.SixFretMedium).Normalized(ChartInstrumentKind.SixFret),
            SixFretEasy = Filled(SixFretEasy, shipped.SixFretEasy).Normalized(ChartInstrumentKind.SixFret),
            DrumsHard = Filled(DrumsHard, shipped.DrumsHard).Normalized(),
            DrumsMedium = Filled(DrumsMedium, shipped.DrumsMedium).Normalized(),
            DrumsEasy = Filled(DrumsEasy, shipped.DrumsEasy).Normalized(),
        };
    }

    /// <summary><paramref name="division"/> as a thinning grid: 0 for none, otherwise the nearest of the common snaps.</summary>
    internal static int NormalizedDivision(int division) =>
        division <= 0 ? 0 : SnapGrid.CommonDivisions.MinBy(d => Math.Abs(d - division));

    /// <summary>The most chords a burst can be.</summary>
    public const int MaxBurstLimit = 8;

    /// <summary>The most chords in a burst the shipped rules allow.</summary>
    public const int ShippedBurst = 3;

    /// <summary>
    /// <paramref name="division"/> as a burst's grid: the nearest of the common snaps, no coarser than
    /// <paramref name="thinning"/>; null when it isn't set or nothing is thinned.
    /// </summary>
    internal static int? NormalizedBurstDivision(int? division, int thinning) =>
        division is { } value && thinning > 0 ? Math.Max(thinning, NormalizedDivision(Math.Max(1, value))) : null;

    /// <summary><paramref name="chords"/> as the most in a burst: from 1 to <see cref="MaxBurstLimit"/>, or null when it isn't set.</summary>
    internal static int? NormalizedBurst(int? chords) => chords is { } value ? Math.Clamp(value, 1, MaxBurstLimit) : null;

    // A set from a file, with what it lacks from the shipped: none, or a value saved before it was added. A set saved
    // before bursts, with neither of theirs, takes the shipped bursts where it thins as the shipped does, and the shipped
    // HOPOs and taps with them, which make a burst's chords played as the source's; with its own thinning it has none.
    private static FretReductionRules Filled(FretReductionRules? rules, FretReductionRules shipped)
    {
        if (rules is null)
            return shipped;
        rules = rules with
        {
            KeepExtendedSustains = rules.KeepExtendedSustains ?? shipped.KeepExtendedSustains,
            KeepOpenNotes = rules.KeepOpenNotes ?? shipped.KeepOpenNotes,
        };
        if (!BeforeBursts(rules.BurstDivision, rules.MaxBurst) || NormalizedDivision(rules.ThinningDivision) != shipped.ThinningDivision)
            return rules;
        return rules with { BurstDivision = shipped.BurstDivision, MaxBurst = shipped.MaxBurst, KeepForcedHopoAndTap = shipped.KeepForcedHopoAndTap };
    }

    private static DrumReductionRules Filled(DrumReductionRules? rules, DrumReductionRules shipped)
    {
        if (rules is null)
            return shipped;
        if (!BeforeBursts(rules.BurstDivision, rules.MaxBurst) || NormalizedDivision(rules.ThinningDivision) != shipped.ThinningDivision)
            return rules;
        return rules with { BurstDivision = shipped.BurstDivision, MaxBurst = shipped.MaxBurst };
    }

    // Whether a set was saved before bursts were added: the fields always save both.
    private static bool BeforeBursts(int? division, int? maxBurst) => division is null && maxBurst is null;

    private ReductionDefaults Copy() => new()
    {
        FiveFretHard = FiveFretHard,
        FiveFretMedium = FiveFretMedium,
        FiveFretEasy = FiveFretEasy,
        SixFretHard = SixFretHard,
        SixFretMedium = SixFretMedium,
        SixFretEasy = SixFretEasy,
        DrumsHard = DrumsHard,
        DrumsMedium = DrumsMedium,
        DrumsEasy = DrumsEasy,
    };
}
