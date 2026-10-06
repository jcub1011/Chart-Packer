using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charts;

/// <summary>How a track came out different from its source when it was converted or merged, worst last.</summary>
public enum ChartMergeIssue
{
    /// <summary>Some sustains became plain notes, or the other way round.</summary>
    SustainsChanged,

    /// <summary>Some notes changed what they are: a HOPO, a tap, a cymbal, in star power.</summary>
    MarkingsChanged,

    /// <summary>Some notes moved or changed lane.</summary>
    NotesChanged,

    /// <summary>Some notes landed on the same tick and became one, or were lost.</summary>
    NotesLost,
}

/// <summary>A track of a converted or merged chart that isn't quite what its source was.</summary>
/// <param name="TrackName">The MIDI track, e.g. <c>PART DRUMS</c>.</param>
/// <param name="Difficulty">The difficulty it's about.</param>
/// <param name="Times">Where the notes that differ are, as song time in the source chart, in order; empty when it isn't known.</param>
public sealed record ChartMergeWarning(ChartMergeIssue Issue, string TrackName, ChartDifficulty Difficulty, IReadOnlyList<TimeSpan>? Times = null)
{
    public IReadOnlyList<TimeSpan> Times { get; init; } = Times ?? [];
}

/// <summary>Compares a track's notes with what they should be, and fixes the HOPOs that differ.</summary>
internal static class NoteCheck
{
    /// <summary>What a comparison found.</summary>
    /// <param name="Issue">The worst difference, if there's one.</param>
    /// <param name="HopoFixes">The chords that are a HOPO when they should be a strum, or the other way round.</param>
    /// <param name="Ticks">Where the notes with any other difference are, in order.</param>
    public readonly record struct Result(ChartMergeIssue? Issue, IReadOnlyList<(long Tick, bool Hopo)> HopoFixes, IReadOnlyList<long> Ticks);

    /// <summary>Compares <paramref name="actual"/> with <paramref name="expected"/>, note by note, by tick and lane.</summary>
    public static Result Compare(IReadOnlyList<ChartNote> expected, IReadOnlyList<ChartNote>? actual)
    {
        actual ??= [];
        ChartMergeIssue? issue = null;
        var fixes = new List<(long, bool)>();
        var ticks = new SortedSet<long>();
        void Found(ChartMergeIssue found, long tick)
        {
            issue = issue is { } was && was >= found ? was : found;
            ticks.Add(tick);
        }

        // A note on one side only moved or changed lane, or, when there are fewer, was lost.
        var unmatched = actual.Count < expected.Count ? ChartMergeIssue.NotesLost : ChartMergeIssue.NotesChanged;
        int i = 0, j = 0;
        while (i < expected.Count || j < actual.Count)
        {
            int order = i == expected.Count ? 1
                : j == actual.Count ? -1
                : (expected[i].Tick, expected[i].Lane).CompareTo((actual[j].Tick, actual[j].Lane));
            if (order != 0)
            {
                Found(unmatched, order < 0 ? expected[i++].Tick : actual[j++].Tick);
                continue;
            }

            var (want, got) = (expected[i++], actual[j++]);
            if ((want.Length > 0) != (got.Length > 0))
                Found(ChartMergeIssue.SustainsChanged, want.Tick);

            // A force marker puts a HOPO right whatever else differs, unless the note is a tap.
            var differ = want.Flags ^ got.Flags;
            if ((differ & NoteFlags.Hopo) != 0 && ((want.Flags | got.Flags) & NoteFlags.Tap) == 0 && (fixes.Count == 0 || fixes[^1].Item1 != want.Tick))
                fixes.Add((want.Tick, (want.Flags & NoteFlags.Hopo) != 0));
            if ((differ & ~NoteFlags.Hopo) != 0)
                Found(ChartMergeIssue.MarkingsChanged, want.Tick);
        }

        return new Result(issue, fixes, [.. ticks]);
    }

    /// <summary>
    /// The MIDI chart <paramref name="midi"/> read with <paramref name="options"/> under each natural-HOPO
    /// threshold a game might play it with (see <see cref="ChartReadOptions.MidiHopoThresholds"/>), the one it's
    /// read with first.
    /// </summary>
    public static IReadOnlyList<ChartDocument> ReadEveryWay(byte[] midi, ChartReadOptions options)
    {
        var (_, resolution, _) = MidiFile.ReadChunks(midi);
        return
        [
            ChartReader.Read(midi, ChartFiles.MidiName, options),
            .. options.MidiHopoThresholds(resolution).Skip(1).Select(t => ChartReader.Read(midi, ChartFiles.MidiName, options with { MidiHopoTicks = t })),
        ];
    }

    /// <summary>
    /// Compares <paramref name="expected"/> with <paramref name="key"/>'s notes in each of <paramref name="reads"/>,
    /// from <see cref="ReadEveryWay"/>: what the first read finds, and the HOPO fixes any of them need.
    /// </summary>
    public static Result Compare(IReadOnlyList<ChartNote> expected, IReadOnlyList<ChartDocument> reads, ChartTrackKey key)
    {
        var results = reads.Select(r => Compare(expected, r.Tracks.GetValueOrDefault(key)?.Notes)).ToList();
        return results[0] with { HopoFixes = [.. results.SelectMany(r => r.HopoFixes).DistinctBy(f => f.Tick).OrderBy(f => f.Tick)] };
    }

    /// <summary>
    /// The warning, if any, for a track that compared as <paramref name="result"/> on check <paramref name="pass"/>:
    /// the first pass's HOPO fixes are forced, so any after it are force markers that didn't take.
    /// </summary>
    /// <param name="timeOf">The song time in the source chart of a tick that was compared.</param>
    public static ChartMergeWarning? WarningOf(Result result, int pass, string trackName, ChartDifficulty difficulty, Func<long, TimeSpan> timeOf)
    {
        var issue = result.Issue;
        IEnumerable<long> ticks = result.Ticks;
        if (pass > 0 && result.HopoFixes.Count > 0)
        {
            issue = issue is { } was && was >= ChartMergeIssue.MarkingsChanged ? was : ChartMergeIssue.MarkingsChanged;
            ticks = ticks.Concat(result.HopoFixes.Select(f => f.Tick)).Distinct().Order();
        }

        return issue is { } found ? new ChartMergeWarning(found, trackName, difficulty, [.. ticks.Select(timeOf)]) : null;
    }

    /// <summary>
    /// Force markers that make each of <paramref name="fixes"/> a HOPO or a strum: five-fret's are
    /// the two pitches above orange, six-fret's the two above black 3.
    /// </summary>
    public static IEnumerable<MidiMessage> ForceMarkers(ChartInstrumentKind kind, ChartDifficulty difficulty, IEnumerable<(long Tick, bool Hopo)> fixes)
    {
        if (kind == ChartInstrumentKind.Drums)
            yield break;

        int b = MidiLayout.BaseOf(kind, difficulty);
        foreach (var (tick, hopo) in fixes)
        {
            int pitch = b + (hopo ? MidiLayout.ForceHopoOffset(kind) : MidiLayout.ForceStrumOffset(kind));
            yield return MidiMessage.NoteOn(tick, pitch);
            yield return MidiMessage.NoteOff(tick + 1, pitch);
        }
    }
}
