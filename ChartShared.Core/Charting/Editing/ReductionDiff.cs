using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>What changed at one tick of a reduction, as the games read the chart (<see cref="ReductionDiff"/>).</summary>
[Flags]
public enum ReductionChanges
{
    None = 0,

    /// <summary>The source's chord has none at its tick in the draft.</summary>
    Removed = 1,

    /// <summary>A 2x kick the draft hasn't.</summary>
    DoubleKickDropped = 2,

    /// <summary>The lanes differ: frets or pads left out, moved or pressed together, a kick left out of a hit, a cymbal made a tom.</summary>
    Reshaped = 4,

    /// <summary>A chord the draft has where the source had none.</summary>
    Added = 8,

    /// <summary>A note held shorter, or no longer held, as read: the sustain cutoff applied.</summary>
    SustainTrimmed = 16,

    /// <summary>Strum, HOPO or tap as read differs while the chord's forcing is the same: a natural HOPO turned.</summary>
    HopoFlipped = 32,

    /// <summary>Strum, HOPO or tap as read differs, and so does the chord's forcing or tap marker.</summary>
    ForcingChanged = 64,
}

/// <summary>A tick of a reduction where something changed.</summary>
/// <param name="EndTick">Where the chord's longest note ends, the source's or the draft's.</param>
/// <param name="IsBoundary">Outside the run's range: a knock-on change to the target as it was, not a change from the source.</param>
public readonly record struct ReductionChange(long Tick, long EndTick, ReductionChanges Kinds, bool IsBoundary);

/// <summary>
/// How one draft of a run of Reduce differs from what it's reduced from, as the games read both (charter-spec C9): each
/// changed tick, and for each note of either what changed about it, for the Reduce dialog's preview to show.
/// </summary>
/// <remarks>
/// Chords are matched by tick and notes by lane, a 2x kick apart from the kick. Inside the run's range the draft is
/// compared with the source; outside it, where the draft keeps what the target had, with the target as it was, so only the
/// knock-on changes at the range's ends show: a sustain cut, a natural HOPO turned.
/// </remarks>
public sealed class ReductionDiff
{
    private const ChordFlags Forcing = ChordFlags.ForceHopo | ChordFlags.ForceStrum | ChordFlags.Tap;

    private ReductionDiff(ChartDifficulty source, ChartDifficulty target, NoteTrack sourceTrack, NoteTrack draftTrack,
        IReadOnlyList<ReductionChange> changes, ReductionChanges[] sourceMarks, ReductionChanges[] draftMarks)
    {
        Source = source;
        Target = target;
        SourceTrack = sourceTrack;
        DraftTrack = draftTrack;
        Changes = changes;
        SourceMarks = sourceMarks;
        DraftMarks = draftMarks;
    }

    /// <summary>The difficulty the draft is reduced from.</summary>
    public ChartDifficulty Source { get; }

    /// <summary>The difficulty the draft is of.</summary>
    public ChartDifficulty Target { get; }

    /// <summary>The source as the games read it: in a run of several difficulties, the draft of the one above when it's in the run.</summary>
    public NoteTrack SourceTrack { get; }

    /// <summary>The draft as the games read it.</summary>
    public NoteTrack DraftTrack { get; }

    /// <summary>The ticks where something changed, in tick order.</summary>
    public IReadOnlyList<ReductionChange> Changes { get; }

    /// <summary>For each of <see cref="SourceTrack"/>'s notes, what the draft lost of it: removed, a 2x kick dropped, or reshaped.</summary>
    public IReadOnlyList<ReductionChanges> SourceMarks { get; }

    /// <summary>For each of <see cref="DraftTrack"/>'s notes, what changed about its chord, and whether it's held shorter.</summary>
    public IReadOnlyList<ReductionChanges> DraftMarks { get; }

    /// <summary>How many changed ticks have any of <paramref name="kinds"/>.</summary>
    public int CountOf(ReductionChanges kinds) => Changes.Count(c => (c.Kinds & kinds) != 0);

    /// <summary>
    /// The diff of each of <paramref name="steps"/> of a run of <paramref name="part"/>: <paramref name="drafted"/> is the
    /// chart with the drafts in it, read with <paramref name="options"/>. It can run off the editing thread.
    /// </summary>
    /// <param name="before">The targets as the games read them before the run; used outside <paramref name="range"/>.</param>
    public static IReadOnlyList<ReductionDiff> Of(EditableChart drafted, ChartInstrument part, IReadOnlyList<(ChartDifficulty Source, ChartDifficulty Target)> steps,
        (long From, long To)? range, ChartReadOptions options, IReadOnlyDictionary<ChartDifficulty, NoteTrack>? before = null)
    {
        ArgumentNullException.ThrowIfNull(drafted);
        ArgumentNullException.ThrowIfNull(steps);
        var tracks = GameView.ProjectPart(drafted, part, options);
        var whole = drafted.PartOf(part);
        Func<long, ChordFlags> ForcingOf(ChartDifficulty difficulty) => tick => whole?[difficulty].FlagsAt(tick) ?? ChordFlags.None;
        return
        [
            .. steps.Select(s => Compare(s.Source, s.Target, tracks[s.Source], tracks[s.Target], ForcingOf(s.Source), ForcingOf(s.Target),
                range, range is null ? null : before?.GetValueOrDefault(s.Target))),
        ];
    }

    /// <summary>
    /// The diff of <paramref name="draftTrack"/> from <paramref name="sourceTrack"/>, each chord's forcing and tap given by
    /// <paramref name="sourceForcing"/> and <paramref name="draftForcing"/>.
    /// </summary>
    /// <param name="range">The ticks the run reduces, both ends in, or null for the whole track.</param>
    /// <param name="targetBefore">The target as it was, which the draft is compared with outside <paramref name="range"/>; without it, nothing outside is.</param>
    public static ReductionDiff Compare(ChartDifficulty source, ChartDifficulty target, NoteTrack sourceTrack, NoteTrack draftTrack,
        Func<long, ChordFlags> sourceForcing, Func<long, ChordFlags> draftForcing, (long From, long To)? range = null, NoteTrack? targetBefore = null)
    {
        ArgumentNullException.ThrowIfNull(sourceTrack);
        ArgumentNullException.ThrowIfNull(draftTrack);
        ArgumentNullException.ThrowIfNull(sourceForcing);
        ArgumentNullException.ThrowIfNull(draftForcing);

        var sourceMarks = new ReductionChanges[sourceTrack.Notes.Count];
        var draftMarks = new ReductionChanges[draftTrack.Notes.Count];
        var changes = new List<ReductionChange>();
        bool Inside(long tick) => range is not { } r || (tick >= r.From && tick <= r.To);

        Walk(sourceTrack.Notes, draftTrack.Notes, Inside, sourceForcing, draftForcing, sourceMarks, draftMarks, changes, boundary: false);
        // Outside the range the draft has the target's forcing as it was.
        if (range is not null && targetBefore is not null)
            Walk(targetBefore.Notes, draftTrack.Notes, t => !Inside(t), draftForcing, draftForcing, null, draftMarks, changes, boundary: true);
        changes.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));

        return new ReductionDiff(source, target, sourceTrack, draftTrack, changes, sourceMarks, draftMarks);
    }

    // Each tick of either list that `where` takes, the chords there compared.
    private static void Walk(IReadOnlyList<ChartNote> before, IReadOnlyList<ChartNote> after, Func<long, bool> where,
        Func<long, ChordFlags> beforeForcing, Func<long, ChordFlags> afterForcing,
        ReductionChanges[]? beforeMarks, ReductionChanges[] afterMarks, List<ReductionChange> changes, bool boundary)
    {
        int i = 0, j = 0;
        while (i < before.Count || j < after.Count)
        {
            long tick = Math.Min(i < before.Count ? before[i].Tick : long.MaxValue, j < after.Count ? after[j].Tick : long.MaxValue);
            int iEnd = i, jEnd = j;
            long longest = 0;
            while (iEnd < before.Count && before[iEnd].Tick == tick)
                longest = Math.Max(longest, before[iEnd++].Length);
            while (jEnd < after.Count && after[jEnd].Tick == tick)
                longest = Math.Max(longest, after[jEnd++].Length);

            if (where(tick))
            {
                var kinds = CompareChord(before, i, iEnd, after, j, jEnd, beforeForcing(tick), afterForcing(tick), beforeMarks, afterMarks);
                if (kinds != ReductionChanges.None)
                    changes.Add(new ReductionChange(tick, tick + longest, kinds, boundary));
            }

            i = iEnd;
            j = jEnd;
        }
    }

    private static ReductionChanges CompareChord(IReadOnlyList<ChartNote> before, int beforeStart, int beforeEnd, IReadOnlyList<ChartNote> after, int afterStart, int afterEnd,
        ChordFlags beforeForcing, ChordFlags afterForcing, ReductionChanges[]? beforeMarks, ReductionChanges[] afterMarks)
    {
        if (afterStart == afterEnd)
        {
            var removed = ReductionChanges.None;
            for (int b = beforeStart; b < beforeEnd; b++)
            {
                var lost = IsDoubleKick(before[b]) ? ReductionChanges.DoubleKickDropped : ReductionChanges.Removed;
                removed |= lost;
                beforeMarks?[b] |= lost;
            }

            return removed;
        }

        if (beforeStart == beforeEnd)
        {
            for (int a = afterStart; a < afterEnd; a++)
                afterMarks[a] |= ReductionChanges.Added;
            return ReductionChanges.Added;
        }

        var kinds = ReductionChanges.None;
        var matched = new bool[afterEnd - afterStart];
        long longestBefore = 0, longestAfter = 0;
        for (int b = beforeStart; b < beforeEnd; b++)
        {
            var note = before[b];
            longestBefore = Math.Max(longestBefore, note.Length);
            int match = -1;
            for (int a = afterStart; a < afterEnd && match < 0; a++)
            {
                if (!matched[a - afterStart] && after[a].Lane == note.Lane && IsDoubleKick(after[a]) == IsDoubleKick(note))
                    match = a;
            }

            if (match < 0)
            {
                var lost = IsDoubleKick(note) ? ReductionChanges.DoubleKickDropped : ReductionChanges.Reshaped;
                kinds |= lost;
                beforeMarks?[b] |= lost;
                continue;
            }

            matched[match - afterStart] = true;
            if ((after[match].Flags & NoteFlags.Cymbal) != (note.Flags & NoteFlags.Cymbal))
            {
                kinds |= ReductionChanges.Reshaped;
                beforeMarks?[b] |= ReductionChanges.Reshaped;
            }

            if (after[match].Length < note.Length)
            {
                kinds |= ReductionChanges.SustainTrimmed;
                afterMarks[match] |= ReductionChanges.SustainTrimmed;
            }
        }

        for (int a = afterStart; a < afterEnd; a++)
        {
            longestAfter = Math.Max(longestAfter, after[a].Length);
            if (!matched[a - afterStart])
                kinds |= ReductionChanges.Reshaped;
        }

        // A chord reshaped and held shorter than its longest note was: each of its notes is.
        if ((kinds & ReductionChanges.Reshaped) != 0 && longestAfter < longestBefore)
        {
            kinds |= ReductionChanges.SustainTrimmed;
            for (int a = afterStart; a < afterEnd; a++)
                afterMarks[a] |= ReductionChanges.SustainTrimmed;
        }

        if (StateOf(before, beforeStart, beforeEnd) != StateOf(after, afterStart, afterEnd))
            kinds |= (beforeForcing & Forcing) != (afterForcing & Forcing) ? ReductionChanges.ForcingChanged : ReductionChanges.HopoFlipped;

        var chord = kinds & (ReductionChanges.Reshaped | ReductionChanges.HopoFlipped | ReductionChanges.ForcingChanged);
        if (chord != ReductionChanges.None)
        {
            for (int a = afterStart; a < afterEnd; a++)
                afterMarks[a] |= chord;
        }

        return kinds;
    }

    // Strum, HOPO or tap, as a chord's notes are read: the games give all of a chord's notes the same.
    private static int StateOf(IReadOnlyList<ChartNote> notes, int start, int end)
    {
        int state = 0;
        for (int i = start; i < end; i++)
        {
            if ((notes[i].Flags & NoteFlags.Tap) != 0)
                return 2;
            if ((notes[i].Flags & NoteFlags.Hopo) != 0)
                state = 1;
        }

        return state;
    }

    private static bool IsDoubleKick(ChartNote note) => (note.Flags & NoteFlags.DoubleKick) != 0;
}
