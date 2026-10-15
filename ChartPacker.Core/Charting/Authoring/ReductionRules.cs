using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Authoring;

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

    /// <summary>The thinning grid, as a snap's division (16 for 1/16): no two chords closer than one step. 0 thins nothing.</summary>
    public int ThinningDivision { get; init; } = 16;

    /// <summary>Whether forced HOPOs and taps are kept; forced strums always are.</summary>
    public bool KeepForcedHopoAndTap { get; init; } = true;

    /// <summary>Whether sustains shorter than a beat are kept, rather than made plain notes.</summary>
    public bool KeepShortSustains { get; init; } = true;

    /// <summary>The shipped defaults for reducing a <paramref name="kind"/> part to <paramref name="target"/> (the C9 table).</summary>
    public static FretReductionRules Default(ChartInstrumentKind kind, ChartDifficulty target)
    {
        bool ghl = kind == ChartInstrumentKind.SixFret;
        return target switch
        {
            ChartDifficulty.Hard => new() { MaxChordSize = 2, HighestLane = ghl ? 2 : 4, ThinningDivision = 16, KeepForcedHopoAndTap = true, KeepShortSustains = true },
            ChartDifficulty.Medium => new() { MaxChordSize = 2, HighestLane = ghl ? 2 : 3, ThinningDivision = 8, KeepForcedHopoAndTap = false, KeepShortSustains = true },
            _ => new() { MaxChordSize = 1, HighestLane = ghl ? 1 : 2, ThinningDivision = 4, KeepForcedHopoAndTap = false, KeepShortSustains = false },
        };
    }

    /// <summary>These rules with every value kept to what it can be for <paramref name="kind"/>, as one read from a file may not be.</summary>
    public FretReductionRules Normalized(ChartInstrumentKind kind)
    {
        int columns = kind == ChartInstrumentKind.SixFret ? 3 : 5;
        return this with
        {
            MaxChordSize = Math.Clamp(MaxChordSize, 1, columns),
            HighestLane = Math.Clamp(HighestLane, 0, columns - 1),
            ThinningDivision = ReductionDefaults.NormalizedDivision(ThinningDivision),
        };
    }
}

/// <summary>How Reduce makes a lower difficulty of drums (charter-spec C9 item 2).</summary>
public sealed record DrumReductionRules
{
    /// <summary>The most pads hit at once, the kick aside.</summary>
    public int PadsPerHit { get; init; } = 2;

    /// <summary>The thinning grid for every hit, kick and pads together, as a snap's division. 0 thins nothing.</summary>
    public int ThinningDivision { get; init; } = 16;

    /// <summary>The kick's own thinning grid, after the hits are thinned, as a snap's division. 0 keeps every kick left.</summary>
    public int KickDivision { get; init; }

    /// <summary>Whether 2× kicks are kept; only Expert can have them, so Reduce never keeps them.</summary>
    public bool KeepDoubleKick { get; init; }

    /// <summary>The shipped defaults for reducing drums to <paramref name="target"/> (the C9 table).</summary>
    public static DrumReductionRules Default(ChartDifficulty target) => target switch
    {
        ChartDifficulty.Hard => new() { PadsPerHit = 2, ThinningDivision = 16, KickDivision = 0 },
        ChartDifficulty.Medium => new() { PadsPerHit = 2, ThinningDivision = 8, KickDivision = 8 },
        _ => new() { PadsPerHit = 1, ThinningDivision = 4, KickDivision = 4 },
    };

    /// <summary>These rules with every value kept to what it can be.</summary>
    public DrumReductionRules Normalized() => this with
    {
        PadsPerHit = Math.Clamp(PadsPerHit, 1, 5),
        ThinningDivision = ReductionDefaults.NormalizedDivision(ThinningDivision),
        KickDivision = ReductionDefaults.NormalizedDivision(KickDivision),
        KeepDoubleKick = false,
    };
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

    /// <summary>A copy with every value kept to what it can be, and the shipped rules for any set the file lacks.</summary>
    public ReductionDefaults Normalized()
    {
        var shipped = Shipped;
        return new ReductionDefaults
        {
            FiveFretHard = (FiveFretHard ?? shipped.FiveFretHard).Normalized(ChartInstrumentKind.FiveFret),
            FiveFretMedium = (FiveFretMedium ?? shipped.FiveFretMedium).Normalized(ChartInstrumentKind.FiveFret),
            FiveFretEasy = (FiveFretEasy ?? shipped.FiveFretEasy).Normalized(ChartInstrumentKind.FiveFret),
            SixFretHard = (SixFretHard ?? shipped.SixFretHard).Normalized(ChartInstrumentKind.SixFret),
            SixFretMedium = (SixFretMedium ?? shipped.SixFretMedium).Normalized(ChartInstrumentKind.SixFret),
            SixFretEasy = (SixFretEasy ?? shipped.SixFretEasy).Normalized(ChartInstrumentKind.SixFret),
            DrumsHard = (DrumsHard ?? shipped.DrumsHard).Normalized(),
            DrumsMedium = (DrumsMedium ?? shipped.DrumsMedium).Normalized(),
            DrumsEasy = (DrumsEasy ?? shipped.DrumsEasy).Normalized(),
        };
    }

    /// <summary><paramref name="division"/> as a thinning grid: 0 for none, otherwise the nearest of the common snaps.</summary>
    internal static int NormalizedDivision(int division) =>
        division <= 0 ? 0 : SnapGrid.CommonDivisions.MinBy(d => Math.Abs(d - division));

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
