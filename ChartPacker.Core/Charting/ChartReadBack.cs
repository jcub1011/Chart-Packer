using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>What a <see cref="ReadBackDifference"/> is in.</summary>
public enum ReadBackDifferenceKind
{
    /// <summary>The resolution, a tempo or a time signature.</summary>
    TempoMap,

    /// <summary>Whether the drums are five-lane, or pro drums.</summary>
    DrumsLayout,

    /// <summary>A track the games should read and don't: a difficulty that should have notes has none.</summary>
    MissingTrack,

    /// <summary>A track the games read that shouldn't be there: a difficulty that should have no notes has some.</summary>
    ExtraTrack,

    /// <summary>Notes that should be read at a tick and lane and aren't.</summary>
    MissingNotes,

    /// <summary>Notes read at a tick and lane that shouldn't be there.</summary>
    ExtraNotes,

    /// <summary>Notes read with a sustain other than the exact length they should have.</summary>
    SustainLengths,

    /// <summary>Notes read as something else: a HOPO, a tap, a cymbal, a 2x kick, in star power, an accent or a ghost.</summary>
    NoteFlags,

    /// <summary>Star-power phrases that should be read and aren't, or are read and shouldn't be.</summary>
    StarPower,
}

/// <summary>A way the games read a saved chart other than as its game view says, and where.</summary>
/// <param name="Instrument">The instrument it's in, when it's in one.</param>
/// <param name="Difficulty">The difficulty it's in, when it's in one track.</param>
/// <param name="Ticks">
/// Where it is, in the chart's ticks, in order: the first <see cref="ChartReadBack.MaxTicks"/> of the places that
/// differ (a track's notes, for a track that's missing or extra; tick 0 for the resolution or the drums' layout).
/// </param>
/// <param name="Times">The song time of each of <paramref name="Ticks"/>, by the game view's tempo map, in order.</param>
/// <param name="Count">How many ticks differ, all told, of which <paramref name="Ticks"/> lists the first.</param>
/// <param name="Flags">For <see cref="ReadBackDifferenceKind.NoteFlags"/>, the flags that differ on any of the notes.</param>
public sealed record ReadBackDifference(
    ReadBackDifferenceKind Kind,
    ChartInstrument? Instrument,
    ChartDifficulty? Difficulty,
    IReadOnlyList<long> Ticks,
    IReadOnlyList<TimeSpan> Times,
    int Count,
    NoteFlags Flags = NoteFlags.None);

/// <summary>
/// The save's read-back check against the game view (charter-spec §4.3): compares <see cref="ChartReader"/>'s reading
/// of the saved bytes with the model's <see cref="GameView"/>, strictly. It goes further than <see cref="NoteCheck"/>,
/// whose join it uses: every flag, accents and ghosts too, the exact sustain lengths, the star-power phrases, the
/// drums' layout and the tempo map must be the same.
/// </summary>
/// <remarks>
/// Only <c>instruments</c> are compared, which for a save are the model's (<see cref="GameView.Instruments"/>): tracks
/// the model passes through, such as <c>PART KEYS</c>, are checked byte for byte instead. There's at most one difference
/// of each kind for each track, each giving its first <see cref="MaxTicks"/> places, so the list stays short however
/// much differs.
/// </remarks>
public static class ChartReadBack
{
    /// <summary>The most places a difference lists; its <see cref="ReadBackDifference.Count"/> says how many there were.</summary>
    public const int MaxTicks = 20;

    /// <summary>
    /// How <paramref name="actual"/>, the chart read back, differs from <paramref name="expected"/>, the game view: the
    /// tempo map, the drums' layout, then each of <paramref name="instruments"/> and each difficulty, easiest first.
    /// Empty when they're the same.
    /// </summary>
    /// <param name="instruments">The instruments compared; the rest of both charts is ignored.</param>
    public static IReadOnlyList<ReadBackDifference> Compare(ChartDocument expected, ChartDocument actual, IEnumerable<ChartInstrument> instruments)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(instruments);

        var differences = new List<ReadBackDifference>();
        var tempo = expected.Tempo;
        void Add(ReadBackDifferenceKind kind, ChartInstrument? instrument, ChartDifficulty? difficulty, IReadOnlyCollection<long> ticks, NoteFlags flags = NoteFlags.None)
        {
            if (ticks.Count == 0)
                return;
            long[] all = [.. ticks.Distinct().Order()];
            long[] listed = all[..Math.Min(all.Length, MaxTicks)];
            differences.Add(new ReadBackDifference(kind, instrument, difficulty, listed, [.. listed.Select(t => TimeSpan.FromSeconds(tempo.SecondsAt(t)))], all.Length, flags));
        }

        Add(ReadBackDifferenceKind.TempoMap, null, null, TempoDifferences(expected.Tempo, actual.Tempo));

        var compared = instruments.Distinct().Order().ToList();
        if (compared.Contains(ChartInstrument.Drums) && (expected.IsFiveLaneDrums != actual.IsFiveLaneDrums || expected.IsProDrums != actual.IsProDrums))
            Add(ReadBackDifferenceKind.DrumsLayout, ChartInstrument.Drums, null, [0]);

        foreach (var instrument in compared)
        {
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                var key = new ChartTrackKey(instrument, difficulty);
                var e = expected.Tracks.GetValueOrDefault(key);
                var a = actual.Tracks.GetValueOrDefault(key);
                if (e is null || a is null)
                {
                    if ((e ?? a) is { } only)
                        Add(e is null ? ReadBackDifferenceKind.ExtraTrack : ReadBackDifferenceKind.MissingTrack, instrument, difficulty, [.. only.Notes.Select(n => n.Tick)]);
                    continue;
                }

                var missing = new List<long>();
                var extra = new List<long>();
                var lengths = new List<long>();
                var flagged = new List<long>();
                var flags = NoteFlags.None;
                foreach (var (want, got) in NoteCheck.Join(e.Notes, a.Notes))
                {
                    if (want is not { } w)
                    {
                        extra.Add(got!.Value.Tick);
                        continue;
                    }

                    if (got is not { } g)
                    {
                        missing.Add(w.Tick);
                        continue;
                    }

                    if (w.Length != g.Length)
                        lengths.Add(w.Tick);
                    if (w.Flags != g.Flags)
                    {
                        flagged.Add(w.Tick);
                        flags |= w.Flags ^ g.Flags;
                    }
                }

                Add(ReadBackDifferenceKind.MissingNotes, instrument, difficulty, missing);
                Add(ReadBackDifferenceKind.ExtraNotes, instrument, difficulty, extra);
                Add(ReadBackDifferenceKind.SustainLengths, instrument, difficulty, lengths);
                Add(ReadBackDifferenceKind.NoteFlags, instrument, difficulty, flagged, flags);
                Add(ReadBackDifferenceKind.StarPower, instrument, difficulty, Unmatched(e.StarPower, a.StarPower, static p => p.Tick));
            }
        }

        return differences;
    }

    /// <summary>Where two tempo maps differ: tick 0 for a resolution, and each tick with a tempo or time signature on one map that the other hasn't.</summary>
    private static List<long> TempoDifferences(TempoMap expected, TempoMap actual)
    {
        if (expected.SameAs(actual))
            return [];
        if (expected.Resolution != actual.Resolution)
            return [0];
        return [.. Unmatched(expected.Tempos, actual.Tempos, static t => t.Tick), .. Unmatched(expected.TimeSignatures, actual.TimeSignatures, static s => s.Tick)];
    }

    /// <summary>The ticks of the items on one side only, each kept as many times as it's there more than on the other.</summary>
    private static List<long> Unmatched<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, Func<T, long> tickOf)
        where T : notnull
    {
        var counts = new Dictionary<T, int>();
        foreach (var item in expected)
            counts[item] = counts.GetValueOrDefault(item) + 1;
        foreach (var item in actual)
            counts[item] = counts.GetValueOrDefault(item) - 1;

        var ticks = new List<long>();
        foreach (var (item, count) in counts)
        {
            for (int i = 0; i < Math.Abs(count); i++)
                ticks.Add(tickOf(item));
        }

        return ticks;
    }
}
