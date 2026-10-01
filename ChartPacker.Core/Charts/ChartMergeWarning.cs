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
public sealed record ChartMergeWarning(ChartMergeIssue Issue, string TrackName, ChartDifficulty Difficulty);

/// <summary>Compares a track's notes with what they should be, and fixes the HOPOs that differ.</summary>
internal static class NoteCheck
{
    /// <summary>What a comparison found.</summary>
    /// <param name="Issue">The worst difference, if there's one.</param>
    /// <param name="HopoFixes">The chords that are a HOPO when they should be a strum, or the other way round.</param>
    public readonly record struct Result(ChartMergeIssue? Issue, IReadOnlyList<(long Tick, bool Hopo)> HopoFixes);

    /// <summary>Compares <paramref name="actual"/> with <paramref name="expected"/>, note by note.</summary>
    public static Result Compare(IReadOnlyList<ChartNote> expected, IReadOnlyList<ChartNote>? actual)
    {
        actual ??= [];
        if (actual.Count < expected.Count)
            return new Result(ChartMergeIssue.NotesLost, []);
        if (actual.Count > expected.Count)
            return new Result(ChartMergeIssue.NotesChanged, []);

        ChartMergeIssue? issue = null;
        var fixes = new List<(long, bool)>();
        void Worse(ChartMergeIssue found) => issue = issue is { } was && was >= found ? was : found;

        for (int i = 0; i < expected.Count; i++)
        {
            var (want, got) = (expected[i], actual[i]);
            if (want.Tick != got.Tick || want.Lane != got.Lane)
                return new Result(ChartMergeIssue.NotesChanged, []);

            if ((want.Length > 0) != (got.Length > 0))
                Worse(ChartMergeIssue.SustainsChanged);

            var differ = want.Flags ^ got.Flags;
            if (differ == NoteFlags.Hopo && (want.Flags & NoteFlags.Tap) == 0)
            {
                if (fixes.Count == 0 || fixes[^1].Item1 != want.Tick)
                    fixes.Add((want.Tick, (want.Flags & NoteFlags.Hopo) != 0));
            }
            else if (differ != NoteFlags.None)
            {
                Worse(ChartMergeIssue.MarkingsChanged);
            }
        }

        return new Result(issue, fixes);
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
        int forceHopo = kind == ChartInstrumentKind.SixFret ? b + 7 : b + 5;
        foreach (var (tick, hopo) in fixes)
        {
            int pitch = hopo ? forceHopo : forceHopo + 1;
            yield return MidiMessage.NoteOn(tick, pitch);
            yield return MidiMessage.NoteOff(tick + 1, pitch);
        }
    }
}
