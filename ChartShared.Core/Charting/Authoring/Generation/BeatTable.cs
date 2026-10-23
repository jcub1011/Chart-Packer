using System.Numerics;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>One source beat and its onsets (spec 3.4.1). Empty beats are kept: an empty previous
/// beat is information for the scorer.</summary>
public sealed record SourceBeat(
    int Index,
    long StartTick,
    long LengthTicks,
    int MeasurePlace,
    BeatPattern Source,
    IReadOnlyList<long> Ticks,
    IReadOnlyList<int> Offsets48,
    BeatShape Shape = BeatShape.None,
    IReadOnlyList<double>? OnsetSalience = null,
    int DownbeatFretMask = 0,
    int LastOffbeatFretMask = 0,
    int LastOffbeatOffset48 = -1);

/// <summary>Splits a part's source notes into beats by the chart's time signatures.</summary>
public static class BeatTable
{
    internal readonly record struct ChordSummary(long Tick, int FretMask, int ChordSize, long MaxLength, int MinLane);

    /// <summary>Build every beat from tick 0 to the last source onset, in order.</summary>
    public static IReadOnlyList<SourceBeat> Build(IReadOnlyList<long> sourceTicks, TempoMap map)
    {
        ArgumentNullException.ThrowIfNull(sourceTicks);
        ArgumentNullException.ThrowIfNull(map);
        var bounds = BeatBounds(map, sourceTicks.Count == 0 ? 0 : sourceTicks.Max());
        var result = new List<SourceBeat>(bounds.Count);
        int cursor = 0;
        var ordered = sourceTicks.Order().ToList();
        for (int i = 0; i < bounds.Count; i++)
        {
            var (start, length, place) = bounds[i];
            var ticks = new List<long>();
            while (cursor < ordered.Count && ordered[cursor] < start)
                cursor++;
            int scan = cursor;
            while (scan < ordered.Count && ordered[scan] < start + length)
            {
                ticks.Add(ordered[scan]);
                scan++;
            }

            ulong mask = 0;
            var offsets = new List<int>(ticks.Count);
            foreach (long tick in ticks)
            {
                int p = Offset48(tick - start, length);
                offsets.Add(p);
                mask |= 1UL << p;
            }

            result.Add(new SourceBeat(i, start, length, place, new BeatPattern(mask), ticks, offsets));
        }

        return result;
    }

    /// <summary>Build every beat from a <see cref="ChartNote"/> track, computing each beat's <see cref="BeatShape"/> and onset salience.</summary>
    public static IReadOnlyList<SourceBeat> Build(IReadOnlyList<ChartNote> sourceNotes, TempoMap map)
    {
        ArgumentNullException.ThrowIfNull(sourceNotes);
        ArgumentNullException.ThrowIfNull(map);
        var chords = Summarize(sourceNotes);
        return BuildFromChords(chords, map);
    }

    /// <summary>Build every beat from an <see cref="EditableNote"/> track, computing each beat's <see cref="BeatShape"/> and onset salience.</summary>
    public static IReadOnlyList<SourceBeat> Build(IReadOnlyList<EditableNote> sourceNotes, TempoMap map)
    {
        ArgumentNullException.ThrowIfNull(sourceNotes);
        ArgumentNullException.ThrowIfNull(map);
        var chords = Summarize(sourceNotes);
        return BuildFromChords(chords, map);
    }

    private static List<ChordSummary> Summarize(IReadOnlyList<ChartNote> notes)
    {
        var chords = new List<ChordSummary>();
        int i = 0;
        while (i < notes.Count)
        {
            long tick = notes[i].Tick;
            int fretMask = 0;
            long maxLen = 0;
            int minLane = int.MaxValue;
            while (i < notes.Count && notes[i].Tick == tick)
            {
                var n = notes[i];
                int bit = n.Lane == ChartNote.OpenLane ? 7 : Math.Clamp((int)n.Lane, 0, 6);
                fretMask |= 1 << bit;
                if (n.Length > maxLen)
                    maxLen = n.Length;
                if (n.Lane != ChartNote.OpenLane && n.Lane < minLane)
                    minLane = n.Lane;
                i++;
            }

            int fretted = BitOperations.PopCount((uint)(fretMask & 0x7F));
            int size = Math.Max(1, fretted);
            chords.Add(new ChordSummary(tick, fretMask, size, maxLen, minLane == int.MaxValue ? -1 : minLane));
        }

        return chords;
    }

    private static List<ChordSummary> Summarize(IReadOnlyList<EditableNote> notes)
    {
        var chords = new List<ChordSummary>();
        int i = 0;
        while (i < notes.Count)
        {
            long tick = notes[i].Tick;
            int fretMask = 0;
            long maxLen = 0;
            int minLane = int.MaxValue;
            while (i < notes.Count && notes[i].Tick == tick)
            {
                var n = notes[i];
                int bit = n.Lane == ChartNote.OpenLane ? 7 : Math.Clamp((int)n.Lane, 0, 6);
                fretMask |= 1 << bit;
                if (n.Length > maxLen)
                    maxLen = n.Length;
                if (n.Lane != ChartNote.OpenLane && n.Lane < minLane)
                    minLane = n.Lane;
                i++;
            }

            int fretted = BitOperations.PopCount((uint)(fretMask & 0x7F));
            int size = Math.Max(1, fretted);
            chords.Add(new ChordSummary(tick, fretMask, size, maxLen, minLane == int.MaxValue ? -1 : minLane));
        }

        return chords;
    }

    internal static IReadOnlyList<SourceBeat> BuildFromChords(List<ChordSummary> chords, TempoMap map)
    {
        var bounds = BeatBounds(map, chords.Count == 0 ? 0 : chords[^1].Tick);
        var result = new List<SourceBeat>(bounds.Count);
        int cursor = 0;
        int prevBeatDownbeatFrets = 0;
        bool prevBeatHadSinglePedal = false;

        for (int i = 0; i < bounds.Count; i++)
        {
            var (start, length, place) = bounds[i];
            while (cursor < chords.Count && chords[cursor].Tick < start)
                cursor++;
            int startIdx = cursor;
            int scan = cursor;
            while (scan < chords.Count && chords[scan].Tick < start + length)
                scan++;
            int count = scan - startIdx;
            if (count == 0)
            {
                result.Add(new SourceBeat(i, start, length, place, BeatPattern.Empty, [], []));
                prevBeatDownbeatFrets = 0;
                prevBeatHadSinglePedal = false;
                continue;
            }

            var ticks = new List<long>(count);
            var offsets = new List<int>(count);
            ulong mask = 0;
            int downIdx = -1;
            int downChordSize = 0;
            int downFrets = 0;
            int offMaxChordSize = 0;
            int minChordSize = int.MaxValue;
            bool offChangesFret = false;
            bool offSustains = false;
            int lastOffFrets = 0;
            int lastOffOffset = -1;

            for (int k = 0; k < count; k++)
            {
                int cIdx = startIdx + k;
                var c = chords[cIdx];
                int p = Offset48(c.Tick - start, length);
                ticks.Add(c.Tick);
                offsets.Add(p);
                mask |= 1UL << p;
                if (c.ChordSize < minChordSize)
                    minChordSize = c.ChordSize;

                if (p == 0 && downIdx < 0)
                {
                    downIdx = cIdx;
                    downChordSize = c.ChordSize;
                    downFrets = c.FretMask;
                }
                else if (p > 0)
                {
                    if (c.ChordSize > offMaxChordSize)
                        offMaxChordSize = c.ChordSize;
                    if (cIdx > 0 && chords[cIdx].Tick - chords[cIdx - 1].Tick <= length && chords[cIdx].FretMask != chords[cIdx - 1].FretMask)
                        offChangesFret = true;
                    if (c.MaxLength >= length / 2)
                        offSustains = true;
                    lastOffFrets = c.FretMask;
                    lastOffOffset = p;
                }
            }

            var shape = BeatShape.None;
            if (downChordSize > offMaxChordSize && downChordSize > 1)
                shape |= BeatShape.DownbeatChord;
            else if (offMaxChordSize > downChordSize && offMaxChordSize > 1)
                shape |= BeatShape.OffbeatChord;
            else if (downChordSize > 1 && offMaxChordSize > 1)
                shape |= BeatShape.MultiChords;

            if (offChangesFret)
                shape |= BeatShape.OffbeatChangesFret;

            bool downRepeatsPrev = false;
            if (downIdx >= 0)
            {
                if (downIdx > 0 && chords[downIdx].Tick - chords[downIdx - 1].Tick <= length && chords[downIdx].FretMask == chords[downIdx - 1].FretMask)
                    downRepeatsPrev = true;
                else if (prevBeatDownbeatFrets != 0 && downFrets == prevBeatDownbeatFrets && prevBeatHadSinglePedal && offChangesFret && offMaxChordSize > 1)
                    downRepeatsPrev = true;
            }

            if (downRepeatsPrev)
                shape |= BeatShape.DownbeatRepeatsPrev;

            int lastIdx = scan - 1;
            bool lastOffAnticipates = false;
            if (offsets[^1] > 0 && offChangesFret)
            {
                if (lastIdx + 1 < chords.Count)
                {
                    var nextChord = chords[lastIdx + 1];
                    long nextBeatEnd = start + length + (length / 8);
                    if (nextChord.Tick >= start + length && nextChord.Tick <= nextBeatEnd && nextChord.FretMask == chords[lastIdx].FretMask)
                        lastOffAnticipates = true;
                }
            }

            if (offSustains || lastOffAnticipates)
                shape |= BeatShape.OffbeatSustainsOrAnticipates;

            var salience = new double[count];
            for (int k = 0; k < count; k++)
            {
                int cIdx = startIdx + k;
                var c = chords[cIdx];
                int p = offsets[k];
                double s = 0;

                if (c.ChordSize > 1)
                    s += 0.55 * (c.ChordSize - 1);
                if (count > 1 && c.ChordSize > minChordSize)
                    s += 0.75;

                if (c.MaxLength >= length)
                    s += 1.10;
                else if (c.MaxLength >= length / 2)
                    s += 0.75;

                if (cIdx == 0 || c.Tick - chords[cIdx - 1].Tick >= length)
                    s += 0.60;
                else if (c.FretMask != chords[cIdx - 1].FretMask)
                    s += 0.40;
                else
                    s -= 0.45;

                if (p == 0 && downRepeatsPrev && offChangesFret)
                    s -= 0.50;
                if (p > 0 && k == count - 1 && lastOffAnticipates)
                    s += 0.65;

                if (cIdx + 1 == chords.Count || chords[cIdx + 1].Tick - (c.Tick + Math.Max(1, c.MaxLength)) >= length)
                    s += 0.35;

                if (cIdx > 0 && cIdx + 1 < chords.Count && chords[cIdx - 1].MinLane >= 0 && c.MinLane >= 0 && chords[cIdx + 1].MinLane >= 0)
                {
                    int d1 = Math.Sign(c.MinLane - chords[cIdx - 1].MinLane);
                    int d2 = Math.Sign(chords[cIdx + 1].MinLane - c.MinLane);
                    if (d1 != 0 && d2 != 0 && d1 != d2)
                        s += 0.30;
                }

                salience[k] = s;
            }

            result.Add(new SourceBeat(
                i,
                start,
                length,
                place,
                new BeatPattern(mask),
                ticks,
                offsets,
                shape,
                salience,
                downFrets,
                lastOffFrets,
                lastOffOffset));

            prevBeatDownbeatFrets = downFrets;
            prevBeatHadSinglePedal = downChordSize > 1 && offMaxChordSize == 1;
        }

        return result;
    }

    internal static int Offset48(long withinBeat, long beatLength)
    {
        if (beatLength <= 0)
            return 0;
        double exact = (double)withinBeat * BeatPattern.Divisions / beatLength;
        int p = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        return Math.Clamp(p, 0, 47);
    }

    internal static long BeatLengthOf(TimeSignature signature, int resolution) =>
        Math.Max(1, (long)resolution * 4L / signature.Denominator);

    private static List<(long Start, long Length, int Place)> BeatBounds(TempoMap map, long lastTick)
    {
        var bounds = new List<(long, long, int)>();
        var signatures = map.TimeSignatures;
        long horizon = Math.Max(lastTick + BeatLengthOf(signatures[^1], map.Resolution), BeatLengthOf(signatures[0], map.Resolution));
        for (int s = 0; s < signatures.Count; s++)
        {
            var sig = signatures[s];
            long end = s + 1 < signatures.Count ? signatures[s + 1].Tick : horizon + BeatLengthOf(sig, map.Resolution);
            long beat = BeatLengthOf(sig, map.Resolution);
            long numerator = Math.Max(1, sig.Numerator);
            long firstMeasure = sig.Tick;
            for (long start = sig.Tick; start < end; start += beat)
            {
                if (start + beat <= 0)
                    continue;
                long withinMeasure = start - firstMeasure;
                int place = (int)((withinMeasure / beat) % numerator);
                bounds.Add((start, beat, place));
                if (start > horizon)
                    return bounds;
            }
        }

        return bounds;
    }
}
