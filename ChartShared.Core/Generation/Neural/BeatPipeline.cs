using System.Text;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// One beat of the source part as the network reads it (spec §3.3): the source pattern plus
/// per-beat features. Conditioning shared by a run (pair, instrument, style, source rating)
/// travels separately in <see cref="RunConditioning"/>.
/// </summary>
/// <param name="StarPowerPhrase">The index of the part's star power phrase covering one of the beat's onsets; -1 for none.</param>
/// <param name="SoloPhrase">The index of the part's solo covering one of the beat's onsets; -1 for none.</param>
/// <param name="RiffKey">The beat's place in its measure and what the measure is charted as (Rules' riff key): beats with
/// the same key are the same beat of a riff.</param>
/// <param name="RiffRepeat">How many measures charted the same came before the beat's own.</param>
/// <param name="SecondsPerBeat">How long the beat lasts, in seconds.</param>
/// <param name="HasChord">Whether a chord in the beat presses two or more frets.</param>
/// <param name="MelodyTurn">Whether the melody turns in the beat: a fretted chord whose fretted neighbours are both
/// higher, or both lower.</param>
public sealed record BeatFrame(
    long BeatStartTick,
    int MeasureIndex,
    int BeatInMeasure,
    int SourcePatternId,
    int[] SourceOffsets48,
    long[] SourceTicks,
    int GapBucket,
    bool PhraseStart,
    bool SectionStart,
    bool HasSustain,
    int StarPowerPhrase,
    int SoloPhrase,
    string RiffKey,
    int RiffRepeat,
    double SecondsPerBeat,
    bool HasChord,
    bool MelodyTurn)
{
    /// <summary>Whether a star power phrase covers one of the beat's onsets.</summary>
    public bool StarPower => StarPowerPhrase >= 0;

    /// <summary>Whether a solo covers one of the beat's onsets.</summary>
    public bool Solo => SoloPhrase >= 0;

    /// <summary>The beat's per-beat scalar inputs (<see cref="BeatFeatures"/>).</summary>
    public BeatScalars Scalars => new(
        BeatInMeasure, GapBucket, PhraseStart, SectionStart, HasSustain, StarPower, Solo, RiffRepeat,
        SourceTicks.Length, HasChord, MelodyTurn, (float)SecondsPerBeat);
}

/// <summary>
/// Run-level conditioning, shared by every beat (§3.3): the source–target pair, the part, the style, and the source's
/// rank by the difficulty model (<see cref="NeuralConditioning.SourceRank"/>; null when it can't be rated).
/// </summary>
public sealed record RunConditioning(
    ChartDifficulty Source,
    ChartDifficulty Target,
    ChartInstrument Instrument,
    int StyleId,
    double? SourceRank);

/// <summary>
/// Splits the source part into beats by the chart's time signatures and encodes each beat's
/// onsets as a pattern (§3.4 steps 1–2). Beats the source doesn't play in are passed over for
/// scoring but kept as context (empty previous beats are information).
/// </summary>
public static class BeatPipeline
{
    /// <summary>Beat schema version the packs are trained on; must match the app's (§5.4).</summary>
    public const int BeatSchemaVersion = 2;

    /// <summary>
    /// Every beat of the source from its first onset to its last, in order. A range run reads the whole part too: the
    /// network's context is the song, and only decoding and voicing keep to the range (§3.6).
    /// </summary>
    public static IReadOnlyList<BeatFrame> Extract(EditableChart chart, ChartInstrument part, ChartDifficulty source)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var whole = chart.PartOf(part);
        var track = whole?[source];
        if (track is null || track.Notes.Count == 0)
            return [];

        var map = chart.TempoMap.ToTempoMap();
        var signatures = map.TimeSignatures;
        if (signatures.Count == 0)
            return [];

        int resolution = map.Resolution;
        bool ghl = ChartInstruments.KindOf(part) == ChartInstrumentKind.SixFret;
        int Column(EditableNote n) => ghl ? n.Lane % 3 : n.Lane;
        var chords = Reducer.ChordsOf([.. track.Notes]);
        var turns = Turns(chords, Column);
        var sections = Reducer.SectionTicks(chart).Order().ToArray();
        var phrases = whole!.Phrases.AsSpan().ToArray();
        var starPower = phrases.Where(static p => p.Kind == PhraseKind.StarPower).OrderBy(static p => p.Tick).ToArray();
        var solos = phrases.Where(static p => p.Kind == PhraseKind.Solo).OrderBy(static p => p.Tick).ToArray();

        var frames = new List<BeatFrame>();
        var key = new StringBuilder();
        var riffCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        long measureFrom = long.MinValue;
        string measureKey = "";
        int measureRepeat = 0;
        int next = 0, measureStart = 0, section = 0, sp = 0, solo = 0;
        long prevOnset = -1;
        long last = chords[^1].Tick;

        for (long beat = BeatStartOf(signatures, resolution, chords[0].Tick); beat <= last;)
        {
            var (sig, nextSig) = SignatureAt(signatures, beat);
            long beatLength = BeatLengthOf(sig, resolution);
            long beatEnd = Math.Min(beat + beatLength, nextSig ?? long.MaxValue);

            // The beat's chords: the walk through them never goes back.
            int first = next;
            while (next < chords.Count && chords[next].Tick < beatEnd)
                next++;
            var srcTicks = new long[next - first];
            var offsets = new int[next - first];
            bool sustain = false, chord = false, turn = false;
            for (int i = first; i < next; i++)
            {
                srcTicks[i - first] = chords[i].Tick;
                offsets[i - first] = BeatPattern.OffsetOf(chords[i].Tick - beat, beatLength);
                sustain |= chords[i].Notes.Any(n => n.Length >= resolution);
                chord |= chords[i].Notes.Where(static n => n.Lane != ChartNote.OpenLane).Select(Column).Distinct().Count() > 1;
                turn |= turns[i];
            }

            // The measure's riff key, worked out once per measure.
            long measureLength = beatLength * Math.Max(1, sig.Numerator);
            long from = sig.Tick + (Math.Max(0, beat - sig.Tick) / measureLength * measureLength);
            if (from != measureFrom)
            {
                long to = Math.Min(from + measureLength, nextSig ?? long.MaxValue);
                while (measureStart < chords.Count && chords[measureStart].Tick < from)
                    measureStart++;
                int measureEnd = measureStart;
                while (measureEnd < chords.Count && chords[measureEnd].Tick < to)
                    measureEnd++;
                measureFrom = from;
                measureKey = Reducer.MeasureKey(key, chords, measureStart, measureEnd, from, to - from, resolution);
                measureRepeat = riffCounts.GetValueOrDefault(measureKey);
                riffCounts[measureKey] = measureRepeat + 1;
            }

            int measureIndex = (int)(Math.Max(0, beat - sig.Tick) / measureLength);
            int beatInMeasure = (int)((beat - from) / beatLength);

            int gapBucket = prevOnset < 0 ? 0 : GapBucketOf((srcTicks.Length > 0 ? srcTicks[0] : beatEnd) - prevOnset, resolution);
            bool phraseStart = srcTicks.Length > 0 && (prevOnset < 0 || srcTicks[0] - prevOnset >= resolution);
            while (section < sections.Length && sections[section] < beat)
                section++;
            bool sectionStart = section < sections.Length && sections[section] < beatEnd;

            frames.Add(new BeatFrame(
                beat, measureIndex, beatInMeasure,
                offsets.Length == 0 ? BeatPattern.EmptyId : BeatPattern.EncodeSource(offsets),
                offsets, srcTicks, gapBucket, phraseStart, sectionStart, sustain,
                PhraseOf(starPower, ref sp, beat, srcTicks), PhraseOf(solos, ref solo, beat, srcTicks),
                $"{measureKey}@{beatInMeasure}", measureRepeat,
                map.SecondsAt(beat + beatLength) - map.SecondsAt(beat), chord, turn));

            if (srcTicks.Length > 0)
                prevOnset = srcTicks[^1];
            beat = beatEnd;
        }

        return frames;
    }

    /// <summary>Buckets a tick gap into 8 log-ish bands relative to the resolution.</summary>
    public static int GapBucketOf(long gapTicks, int resolution)
    {
        if (gapTicks <= 0)
            return 0;
        double beats = (double)gapTicks / Math.Max(1, resolution);
        if (beats < 0.26)
            return 1;
        if (beats < 0.51)
            return 2;
        if (beats < 1.01)
            return 3;
        if (beats < 2.01)
            return 4;
        if (beats < 4.01)
            return 5;
        if (beats < 8.01)
            return 6;
        return 7;
    }

    // The index of the first of phrases (in tick order) covering one of ticks, or -1; next is where the walk is, and the
    // phrases that end before the beat are passed for good.
    private static int PhraseOf(Phrase[] phrases, ref int next, long beat, long[] ticks)
    {
        while (next < phrases.Length && phrases[next].CoverEnd <= beat)
            next++;
        if (ticks.Length == 0)
            return -1;
        for (int p = next; p < phrases.Length && phrases[p].Tick <= ticks[^1]; p++)
        {
            foreach (long t in ticks)
            {
                if (phrases[p].Covers(t))
                    return p;
            }
        }

        return -1;
    }

    // Whether each chord is a turn of the melody: the fretted chords either side of it are both higher, or both lower
    // (Reducer.Scores' test, over the whole part).
    private static bool[] Turns(IReadOnlyList<(long Tick, EditableNote[] Notes)> chords, Func<EditableNote, int> column)
    {
        var turns = new bool[chords.Count];
        var frets = new List<(int Index, int Anchor)>();
        for (int i = 0; i < chords.Count; i++)
        {
            var pressed = chords[i].Notes.Where(static n => n.Lane != ChartNote.OpenLane).ToList();
            if (pressed.Count > 0)
                frets.Add((i, pressed.Min(column)));
        }

        for (int k = 1; k < frets.Count - 1; k++)
        {
            int before = Math.Sign(frets[k].Anchor - frets[k - 1].Anchor);
            int after = Math.Sign(frets[k + 1].Anchor - frets[k].Anchor);
            turns[frets[k].Index] = before != 0 && after != 0 && before != after;
        }

        return turns;
    }

    private static long BeatStartOf(IReadOnlyList<TimeSignature> signatures, int resolution, long tick)
    {
        var (sig, _) = SignatureAt(signatures, tick);
        long beat = BeatLengthOf(sig, resolution);
        long offset = Math.Max(0, tick - sig.Tick);
        return sig.Tick + (offset / beat * beat);
    }

    private static long BeatLengthOf(TimeSignature sig, int resolution) =>
        Math.Max(1, resolution * 4L / Math.Max(1, sig.Denominator));

    // The time signature in effect at tick, and where the next one starts, if any.
    private static (TimeSignature Signature, long? Next) SignatureAt(IReadOnlyList<TimeSignature> signatures, long tick)
    {
        var current = signatures[0];
        foreach (var s in signatures)
        {
            if (s.Tick > tick)
                return (current, s.Tick);
            current = s;
        }

        return (current, null);
    }
}
