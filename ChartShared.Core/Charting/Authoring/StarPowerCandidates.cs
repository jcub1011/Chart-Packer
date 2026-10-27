namespace ChartShared.Core.Charting.Authoring;

/// <summary>How much each thing about a spot counts towards a star power phrase being placed there (<see cref="StarPowerCandidates"/>).</summary>
internal static class StarPowerWeights
{
    /// <summary>It starts on a measure line.</summary>
    public const double OnMeasureLine = 1.0;

    /// <summary>Its first chord is where it starts.</summary>
    public const double OnFirstChord = 0.5;

    /// <summary>It ends at the end of a run: the last chord before a rest or a section.</summary>
    public const double RunEnd = 1.0;

    /// <summary>It ends on the last chord before a measure line, mid-run.</summary>
    public const double MeasureEnd = 0.5;

    /// <summary>Its last chord holds a sustain of a beat or longer, which a player can whammy for more.</summary>
    public const double Sustain = 1.0;

    /// <summary>It lasts <see cref="ShortestGood"/> to <see cref="StarPowerParameters.PreferredMaxMeasures"/> measures.</summary>
    public const double GoodLength = 0.5;

    /// <summary>The fewest measures a phrase of a good length lasts.</summary>
    public const double ShortestGood = 0.75;

    /// <summary>What each measure past <see cref="StarPowerParameters.PreferredMaxMeasures"/> costs.</summary>
    public const double LongMeasure = 0.5;

    /// <summary>What each chord away from <see cref="IdealChords"/> costs.</summary>
    public const double ChordsOff = 0.05;

    /// <summary>The number of chords a phrase best has, the official charts' median on Expert.</summary>
    public const int IdealChords = 7;
}

/// <summary>
/// A spot a star power phrase can go (<see cref="StarPowerCandidates"/>), and where it falls.
/// </summary>
/// <param name="Chords">How many of the source's chords it covers.</param>
/// <param name="Quality">How good a spot it is (<see cref="StarPowerWeights"/>).</param>
/// <param name="StartMeasure">Its start, as a measure position.</param>
/// <param name="EndMeasure">Its end, the tick after the last it covers, as a measure position.</param>
/// <param name="CompleteMeasure">Its last chord, where a player completes it, as a measure position.</param>
/// <param name="StartActive">Its start, in measures of the part's playing.</param>
/// <param name="CompleteActive">Its last chord, in measures of the part's playing.</param>
/// <param name="StartSeconds">Its start, in seconds of the part's playing.</param>
/// <param name="EndSeconds">Its end, in seconds of the part's playing.</param>
internal readonly record struct StarPowerCandidate(
    Phrase Phrase,
    int Chords,
    double Quality,
    double StartMeasure,
    double EndMeasure,
    double CompleteMeasure,
    double StartActive,
    double CompleteActive,
    double StartSeconds,
    double EndSeconds)
{
    /// <summary>Its middle, in seconds of the part's playing.</summary>
    public double CenterSeconds => (StartSeconds + EndSeconds) / 2;
}

/// <summary>The spots a star power phrase can go in a part (<see cref="StarPowerGenerator"/>), with how good each is.</summary>
internal static class StarPowerCandidates
{
    /// <summary>Every spot, in order of start then end.</summary>
    public static List<StarPowerCandidate> Find(StarPowerContext c)
    {
        var p = c.Parameters;
        var chords = c.Chords;
        var timeline = c.Timeline;
        var lines = timeline.MeasureLines;
        double lastActive = timeline.ActiveMeasureAt(c.LastChordTick);
        var found = new List<StarPowerCandidate>();
        foreach (var (first, last) in c.Runs)
        {
            // A run's measure lines, and its first chord where it's on a beat.
            var starts = new List<long>();
            long runStart = chords[first].Tick, runEnd = chords[last].Tick;
            if (Reducer.BeatStrength(runStart, c.Map) <= 1)
                starts.Add(runStart);
            int line = Array.BinarySearch(lines, runStart);
            for (int m = line >= 0 ? line : ~line; m < lines.Length && lines[m] <= runEnd; m++)
            {
                if (lines[m] != runStart)
                    starts.Add(lines[m]);
            }

            int a = first;
            foreach (long start in starts)
            {
                while (chords[a].Tick < start)
                    a++;
                double startMeasure = timeline.MeasureAt(start);
                double startSeconds = timeline.SecondsAt(start);
                for (int b = a; b <= last; b++)
                {
                    int count = b - a + 1;
                    if (count > p.MaxChords || timeline.MeasureAt(chords[b].Tick) - startMeasure >= p.MaxMeasures)
                        break;
                    bool atRunEnd = b == last;
                    if (count < p.MinChords || (!atRunEnd && timeline.MeasureIndexAt(chords[b + 1].Tick) == timeline.MeasureIndexAt(chords[b].Tick)))
                        continue;

                    long end = EndOf(c, StarPowerTimeline.EndOf(chords[b]), b + 1 < chords.Count ? chords[b + 1].Tick : long.MaxValue);
                    if (timeline.SecondsAt(end) - startSeconds > p.MaxPhraseSeconds)
                        break;

                    var phrase = new Phrase(PhraseKind.StarPower, start, end - start);
                    double completeActive = timeline.ActiveMeasureAt(chords[b].Tick);
                    if (lastActive - completeActive < p.TailMeasures || !Fits(c, phrase))
                        continue;

                    double measures = timeline.MeasureAt(end) - startMeasure;
                    double quality = (start == lines[timeline.MeasureIndexAt(start)] ? StarPowerWeights.OnMeasureLine : 0)
                        + (chords[a].Tick == start ? StarPowerWeights.OnFirstChord : 0)
                        + (atRunEnd ? StarPowerWeights.RunEnd : StarPowerWeights.MeasureEnd)
                        + (chords[b].Notes.Any(n => n.Length >= c.Resolution) ? StarPowerWeights.Sustain : 0)
                        + (measures >= StarPowerWeights.ShortestGood - 1e-9 && measures <= p.PreferredMaxMeasures + 1e-9 ? StarPowerWeights.GoodLength : 0)
                        - (StarPowerWeights.LongMeasure * Math.Max(0, measures - p.PreferredMaxMeasures))
                        - (StarPowerWeights.ChordsOff * Math.Abs(count - StarPowerWeights.IdealChords));
                    found.Add(new StarPowerCandidate(
                        phrase,
                        count,
                        quality,
                        startMeasure,
                        timeline.MeasureAt(end),
                        timeline.MeasureAt(chords[b].Tick),
                        timeline.ActiveMeasureAt(start),
                        completeActive,
                        timeline.ActiveSecondsAt(start),
                        timeline.ActiveSecondsAt(end)));
                }
            }
        }

        found.Sort(static (x, y) => x.Phrase.Tick != y.Phrase.Tick ? x.Phrase.Tick.CompareTo(y.Phrase.Tick) : x.Phrase.Length.CompareTo(y.Phrase.Length));
        return found;
    }

    // Where a phrase whose last chord is let go at release ends, the next chord at next: as the official charts end one, on
    // the measure line after it, within a beat, or else on the next beat line, where that comes before the next chord;
    // else where it's let go, up to the next chord.
    private static long EndOf(StarPowerContext c, long release, long next)
    {
        int line = Array.BinarySearch(c.Timeline.MeasureLines, release);
        line = line >= 0 ? line : ~line;
        if (line < c.Timeline.MeasureLines.Length && c.Timeline.MeasureLines[line] <= next && c.Timeline.MeasureLines[line] - release <= c.Resolution)
            return c.Timeline.MeasureLines[line];
        foreach (var beat in c.Map.BeatLines(release, release + (4L * c.Resolution)))
            return beat.Tick <= next ? beat.Tick : Math.Min(release, next);
        return Math.Min(release, next);
    }

    // Whether a phrase can go there: within the span, crossing no section, solo edge or big rock ending, clear of the star
    // power there but for what the plan replaces, and covering a note on every difficulty with notes.
    private static bool Fits(StarPowerContext c, Phrase phrase)
    {
        if (c.Span is { } span && (phrase.Tick < span.From || phrase.CoverEnd - 1 > span.To))
            return false;
        int section = Array.BinarySearch(c.Sections, phrase.Tick + 1);
        section = section >= 0 ? section : ~section;
        if (section < c.Sections.Length && c.Sections[section] < phrase.CoverEnd)
            return false;

        foreach (var other in c.Part.Phrases.AsSpan())
        {
            if (!phrase.Overlaps(other))
                continue;
            if (other.Kind == PhraseKind.StarPower)
            {
                if (!c.ReplacingStarPower)
                    return false;
                // Star power wholly inside the span goes when the plan is applied; one reaching across its edge stays, so
                // the proposals keep clear of it.
                if (c.Span is { } limits && (other.Tick < limits.From || other.CoverEnd - 1 > limits.To))
                    return false;
                continue;
            }

            if (other.Kind == PhraseKind.BigRockEnding)
                return false;
            if (other.Kind == PhraseKind.Solo && (phrase.Tick < other.Tick || phrase.CoverEnd > other.CoverEnd))
                return false;
        }

        foreach (var track in c.Others)
        {
            if (track.Notes.Between(phrase.Tick, phrase.CoverEnd - 1).IsEmpty)
                return false;
        }

        return true;
    }
}
