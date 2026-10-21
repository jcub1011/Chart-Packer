using System.Runtime.CompilerServices;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// What each attribute is normalized against (ρ, docs/difficulty-model.md §3.3): its raw strain under its reference
/// pattern, which scores 100. Each instrument has its own patterns (§14.3). Endurance's is the fatigue the endurance reference, a stamina limit rather than a speed one,
/// builds when played without end.
/// </summary>
/// <remarks>
/// The references are fixed for a set of parameters, so adding charts to a library never changes the values of those
/// already rated. Each is the mean strain just after a note, once the pattern has settled.
/// </remarks>
public sealed record DifficultyReferences(double Strum, double Note, double Rhythm, double Pattern, double Endurance)
{
    // How long each pattern is played, and from when its strain is averaged, in seconds.
    private const double SettleSeconds = 30;
    private const double PlaySeconds = 60;
    private const double PatternSettleSeconds = 60;
    private const double PatternPlaySeconds = 180;

    // Endurance's half-life is long, so it's given fifteen of them to settle.
    private const double EnduranceHalfLives = 15;

    private static readonly ConditionalWeakTable<DifficultyParameters, DifficultyReferences> Cache = new();

    /// <summary>A reference of 100 for each attribute, so a pass gives raw strains.</summary>
    internal static DifficultyReferences Raw { get; } = new(100, 100, 100, 100, 100);

    /// <summary>The references for <paramref name="parameters"/>, worked out once for each instance.</summary>
    public static DifficultyReferences For(DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Cache.GetValue(parameters, Compute);
    }

    private static DifficultyReferences Compute(DifficultyParameters parameters)
    {
        var raw = new DifficultyModel(parameters, Raw);
        double strum = Mean(raw, StrumPattern(parameters, PlaySeconds), SettleSeconds, static l => l.Strum);
        double note = Mean(raw, NotePattern(parameters, PlaySeconds), SettleSeconds, static l => l.Note);
        double rhythm = Mean(raw, RhythmPattern(parameters, PlaySeconds), SettleSeconds, static l => l.Rhythm);
        double pattern = Mean(raw, PatternPattern(parameters, PatternPlaySeconds), PatternSettleSeconds, static l => l.Pattern);

        // Endurance builds from the normalized load, so it's measured with the other four in place.
        var partial = new DifficultyModel(parameters, new DifficultyReferences(strum, note, rhythm, pattern, 100));
        double seconds = parameters.EnduranceHalfLife * EnduranceHalfLives;
        double endurance = Mean(partial, EndurancePattern(parameters, seconds + SettleSeconds), seconds, static l => l.Endurance);
        return new DifficultyReferences(strum, note, rhythm, pattern, endurance);
    }

    // The mean of what's picked just after each note from fromSeconds on.
    private static double Mean(DifficultyModel model, IEnumerable<DifficultyNote> notes, double fromSeconds, Func<DifficultyAttributes, double> pick)
    {
        var state = new DifficultyState(model);
        double sum = 0;
        int count = 0;
        foreach (var note in notes)
        {
            state.Advance(note);
            if (note.TimeMs >= fromSeconds * 1000)
            {
                sum += pick(state.Levels);
                count++;
            }
        }

        return count == 0 || sum <= 0 ? 1 : sum / count;
    }

    /// <summary><paramref name="attribute"/>'s reference pattern for <paramref name="parameters"/>' instrument, <paramref name="seconds"/> long.</summary>
    internal static IEnumerable<DifficultyNote> PatternOf(DifficultyAttribute attribute, DifficultyParameters parameters, double seconds) => attribute switch
    {
        DifficultyAttribute.Strum => StrumPattern(parameters, seconds),
        DifficultyAttribute.Note => NotePattern(parameters, seconds),
        DifficultyAttribute.Rhythm => RhythmPattern(parameters, seconds),
        DifficultyAttribute.Pattern => PatternPattern(parameters, seconds),
        DifficultyAttribute.Endurance => EndurancePattern(parameters, seconds),
        _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, null),
    };

    /// <summary>
    /// The strum reference: strummed single green notes (six-fret: the first white fret), sixteenths. For drums, the
    /// kick's: kicks alone, sixteenths.
    /// </summary>
    internal static IEnumerable<DifficultyNote> StrumPattern(DifficultyParameters parameters, double seconds)
    {
        byte lanes = parameters.Instrument.IsDrums() ? DrumPads.Kick : (byte)1;
        return Steady(parameters.StrumReferenceBpm, 0.25, seconds, _ => (lanes, NoteKind.Strum));
    }

    /// <summary>
    /// The endurance reference: strummed single green notes, sixteenths, at a pace kept up for a whole song. For drums,
    /// sixteenths on yellow with a kick on every eighth.
    /// </summary>
    internal static IEnumerable<DifficultyNote> EndurancePattern(DifficultyParameters parameters, double seconds)
    {
        if (parameters.Instrument.IsDrums())
            return Steady(parameters.EnduranceReferenceBpm, 0.25, seconds, static i => ((byte)(i % 2 == 0 ? 0b10 | DrumPads.Kick : 0b10), NoteKind.Strum));
        return Steady(parameters.EnduranceReferenceBpm, 0.25, seconds, static _ => (1, NoteKind.Strum));
    }

    /// <summary>
    /// The note reference: HOPOs up and down the frets, green to orange and back, sixteenths; six-fret, along the white
    /// frets and back along the black. For drums, the hands': single strokes across the toms and back, sixteenths.
    /// </summary>
    internal static IEnumerable<DifficultyNote> NotePattern(DifficultyParameters parameters, double seconds)
    {
        switch (parameters.Instrument)
        {
            case DifficultyInstrument.SixFret:
                int[] grid = [0, 1, 2, 5, 4, 3];
                return Steady(parameters.NoteReferenceBpm, 0.25, seconds, i => ((byte)(1 << grid[i % grid.Length]), i == 0 ? NoteKind.Strum : NoteKind.Hopo));
            case DifficultyInstrument.Drums or DifficultyInstrument.ProDrums:
                int[] toms = [0, 1, 2, 3, 2, 1];
                return Steady(parameters.NoteReferenceBpm, 0.25, seconds, i => ((byte)(1 << toms[i % toms.Length]), NoteKind.Strum));
            default:
                return Steady(parameters.NoteReferenceBpm, 0.25, seconds, static i =>
                {
                    int step = i % 8;
                    int lane = step <= 4 ? step : 8 - step;
                    return ((byte)(1 << lane), i == 0 ? NoteKind.Strum : NoteKind.Hopo);
                });
        }
    }

    /// <summary>
    /// The rhythm reference: strummed green notes (six-fret: the first white fret; drums: red), a beat of sixteenths then
    /// a beat of eighth triplets.
    /// </summary>
    internal static IEnumerable<DifficultyNote> RhythmPattern(DifficultyParameters parameters, double seconds)
    {
        double beatMs = 60_000 / parameters.RhythmReferenceBpm;
        for (int beat = 0; beat * beatMs < seconds * 1000; beat++)
        {
            int per = beat % 2 == 0 ? 4 : 3;
            for (int k = 0; k < per; k++)
            {
                double at = beat + ((double)k / per);
                yield return new DifficultyNote(at * beatMs, at, 1, NoteKind.Strum, 0);
            }
        }
    }

    /// <summary>
    /// The pattern reference: strummed single notes on frets picked at random, the same each time, sixteenths. For drums,
    /// on the four pads and the kick.
    /// </summary>
    internal static IEnumerable<DifficultyNote> PatternPattern(DifficultyParameters parameters, double seconds)
    {
        // A fixed xorshift, so the reference never changes with the runtime's Random.
        uint seed = 0x9E3779B9;
        bool drums = parameters.Instrument.IsDrums();
        uint lanes = parameters.Instrument == DifficultyInstrument.SixFret ? 6u : 5u;
        return Steady(parameters.PatternReferenceBpm, 0.25, seconds, _ =>
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            int lane = (int)(seed % lanes);
            return (drums && lane == 4 ? DrumPads.Kick : (byte)(1 << lane), NoteKind.Strum);
        });
    }

    // Notes every `beats` beats at `bpm`, for `seconds`, their lanes and kinds from their index.
    private static IEnumerable<DifficultyNote> Steady(double bpm, double beats, double seconds, Func<int, (byte Lanes, NoteKind Kind)> of)
    {
        double beatMs = 60_000 / bpm;
        for (int i = 0; i * beats * beatMs < seconds * 1000; i++)
        {
            var (lanes, kind) = of(i);
            yield return new DifficultyNote(i * beats * beatMs, i * beats, lanes, kind, 0);
        }
    }
}
