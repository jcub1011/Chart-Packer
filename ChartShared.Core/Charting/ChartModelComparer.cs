using System.Globalization;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>What a <see cref="ModelDifference"/> is in.</summary>
public enum ModelDifferenceKind
{
    /// <summary>Ticks per beat.</summary>
    Resolution,

    /// <summary>The tempo track's name.</summary>
    Name,

    /// <summary>A tempo marker.</summary>
    Tempo,

    /// <summary>A time-signature marker.</summary>
    TimeSignature,

    /// <summary>A tempo anchor.</summary>
    Anchor,

    /// <summary>An event on the tempo track other than a tempo, a time signature or an anchor.</summary>
    TempoTrackEvent,

    /// <summary>A text or raw event on <c>EVENTS</c>.</summary>
    GlobalEvent,

    /// <summary>The song's drum variant.</summary>
    DrumsVariant,

    /// <summary>A part one chart has and the other doesn't.</summary>
    Part,

    /// <summary>A part's <c>[ENHANCED_OPENS]</c> or <c>[ENABLE_CHART_DYNAMICS]</c>.</summary>
    PartSetting,

    /// <summary>A note.</summary>
    Note,

    /// <summary>A chord's force HOPO, force strum or tap.</summary>
    ChordFlags,

    /// <summary>A drum mix event.</summary>
    Mix,

    /// <summary>A phrase.</summary>
    Phrase,

    /// <summary>A tom marker.</summary>
    TomMarker,

    /// <summary>A text or raw event on a part's track.</summary>
    PartEvent,

    /// <summary>A passthrough track.</summary>
    Passthrough,

    /// <summary>Not a difference itself: more were found than are listed.</summary>
    More,
}

/// <summary>A way two editable charts differ, and where.</summary>
/// <param name="Part">The part it's in, when it's in one.</param>
/// <param name="Difficulty">The difficulty it's in, when it's in one track.</param>
/// <param name="Tick">The tick it's at, when it's at one.</param>
/// <param name="Description">What differs, in words, with where it is.</param>
public sealed record ModelDifference(ModelDifferenceKind Kind, ChartInstrument? Part, ChartDifficulty? Difficulty, long? Tick, string Description);

/// <summary>
/// Compares two editable charts in everything they hold, as the save's read-back check needs: the chart read back must
/// be the chart written (charter-spec §4.3).
/// </summary>
public static class ChartModelComparer
{
    /// <summary>The most differences listed; past it, one <see cref="ModelDifferenceKind.More"/> entry says there were more.</summary>
    public const int MaxDifferences = 100;

    /// <summary>
    /// How <paramref name="actual"/> differs from <paramref name="expected"/>, in the order: the tempo track, <c>EVENTS</c>,
    /// the parts, then the passthrough tracks. Empty when they're the same.
    /// </summary>
    public static IReadOnlyList<ModelDifference> Differences(EditableChart expected, EditableChart actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var found = new Found();
        Compare(found, expected, actual);
        return found.Result();
    }

    private static void Compare(Found found, EditableChart expected, EditableChart actual)
    {
        if (expected.Resolution != actual.Resolution)
            found.Add(ModelDifferenceKind.Resolution, null, null, null, Invariant($"The resolution is {actual.Resolution} rather than {expected.Resolution}."));
        if (expected.Name != actual.Name)
            found.Add(ModelDifferenceKind.Name, null, null, null, $"The tempo track is named {Quoted(actual.Name)} rather than {Quoted(expected.Name)}.");

        var global = new Where(null, null);
        Grouped(found, ModelDifferenceKind.Tempo, global, "tempo marker", expected.TempoMap.Tempos, actual.TempoMap.Tempos, Describe);
        Grouped(found, ModelDifferenceKind.TimeSignature, global, "time signature", expected.TempoMap.TimeSignatures, actual.TempoMap.TimeSignatures, Describe);
        Unique(found, ModelDifferenceKind.Anchor, global, "anchor", expected.TempoMap.Anchors, actual.TempoMap.Anchors, Describe);
        Grouped(found, ModelDifferenceKind.TempoTrackEvent, global, "tempo track event", expected.TempoTrackRaw, actual.TempoTrackRaw, Describe);
        Grouped(found, ModelDifferenceKind.GlobalEvent, global, "EVENTS text event", expected.Events, actual.Events, Describe);
        Grouped(found, ModelDifferenceKind.GlobalEvent, global, "EVENTS raw event", expected.EventsRaw, actual.EventsRaw, Describe);
        if (expected.DrumsVariant != actual.DrumsVariant)
            found.Add(ModelDifferenceKind.DrumsVariant, null, null, null, $"The drums are {Describe(actual.DrumsVariant)} rather than {Describe(expected.DrumsVariant)}.");

        foreach (var instrument in Enum.GetValues<ChartInstrument>())
        {
            if (found.Done)
                return;
            var e = expected.PartOf(instrument);
            var a = actual.PartOf(instrument);
            if (e is not null && a is not null)
                ComparePart(found, e, a);
            else if (e is not null)
                found.Add(ModelDifferenceKind.Part, instrument, null, null, $"The {instrument} part is missing.");
            else if (a is not null)
                found.Add(ModelDifferenceKind.Part, instrument, null, null, $"There's a {instrument} part that shouldn't be there.");
        }

        if (expected.Passthrough.Count != actual.Passthrough.Count)
        {
            found.Add(ModelDifferenceKind.Passthrough, null, null, null,
                Invariant($"There are {actual.Passthrough.Count} passthrough tracks rather than {expected.Passthrough.Count}."));
        }

        for (int i = 0; i < Math.Min(expected.Passthrough.Count, actual.Passthrough.Count) && !found.Done; i++)
        {
            if (!expected.Passthrough[i].Equals(actual.Passthrough[i]))
            {
                found.Add(ModelDifferenceKind.Passthrough, null, null, null,
                    Invariant($"Passthrough track {i + 1} is {actual.Passthrough[i]} rather than {expected.Passthrough[i]}."));
            }
        }
    }

    private static void ComparePart(Found found, EditablePart expected, EditablePart actual)
    {
        var instrument = expected.Instrument;
        var part = new Where(instrument, null);
        if (expected.EnhancedOpens != actual.EnhancedOpens)
            found.Add(ModelDifferenceKind.PartSetting, instrument, null, null, $"{instrument}: [ENHANCED_OPENS] is {OnOff(actual.EnhancedOpens)} rather than {OnOff(expected.EnhancedOpens)}.");
        if (expected.ChartDynamics != actual.ChartDynamics)
            found.Add(ModelDifferenceKind.PartSetting, instrument, null, null, $"{instrument}: [ENABLE_CHART_DYNAMICS] is {OnOff(actual.ChartDynamics)} rather than {OnOff(expected.ChartDynamics)}.");

        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            var e = expected[difficulty];
            var a = actual[difficulty];
            var track = new Where(instrument, difficulty);
            Unique(found, ModelDifferenceKind.Note, track, "note", e.Notes, a.Notes, Describe);
            Unique(found, ModelDifferenceKind.ChordFlags, track, "chord marking", e.FlaggedChords, a.FlaggedChords, Describe);
            Grouped(found, ModelDifferenceKind.Mix, track, "mix event", e.Mixes, a.Mixes, Describe);
        }

        Grouped(found, ModelDifferenceKind.Phrase, part, "phrase", expected.Phrases, actual.Phrases, Describe);
        Grouped(found, ModelDifferenceKind.TomMarker, part, "tom marker", expected.TomMarkers, actual.TomMarkers, Describe);
        Grouped(found, ModelDifferenceKind.PartEvent, part, "text event", expected.TextEvents, actual.TextEvents, Describe);
        Grouped(found, ModelDifferenceKind.PartEvent, part, "raw event", expected.RawEvents, actual.RawEvents, Describe);
    }

    /// <summary>Compares lists holding one item per place, item by item: missing, extra, or at its place but different.</summary>
    private static void Unique<T>(Found found, ModelDifferenceKind kind, Where where, string what, TickList<T> expected, TickList<T> actual, Func<T, string> describe)
        where T : IEquatable<T>
    {
        int i = 0, j = 0;
        while ((i < expected.Count || j < actual.Count) && !found.Done)
        {
            int c = i >= expected.Count ? 1 : j >= actual.Count ? -1 : expected.Compare(expected[i], actual[j]);
            if (c < 0)
            {
                var item = expected[i++];
                where.Add(found, kind, expected.TickOf(item), $"a {what} is missing: {describe(item)}.");
            }
            else if (c > 0)
            {
                var item = actual[j++];
                where.Add(found, kind, actual.TickOf(item), $"there's a {what} that shouldn't be there: {describe(item)}.");
            }
            else
            {
                var e = expected[i++];
                var a = actual[j++];
                if (!e.Equals(a))
                    where.Add(found, kind, expected.TickOf(e), $"a {what} is {describe(a)} rather than {describe(e)}.");
            }
        }
    }

    /// <summary>Compares lists that can hold several items at a tick, tick by tick: the items at each tick must be the same, in the same order.</summary>
    private static void Grouped<T>(Found found, ModelDifferenceKind kind, Where where, string what, TickList<T> expected, TickList<T> actual, Func<T, string> describe)
        where T : IEquatable<T>
    {
        int i = 0, j = 0;
        while ((i < expected.Count || j < actual.Count) && !found.Done)
        {
            long tick = Math.Min(
                i < expected.Count ? expected.TickOf(expected[i]) : long.MaxValue,
                j < actual.Count ? actual.TickOf(actual[j]) : long.MaxValue);
            var e = expected.At(tick);
            var a = actual.At(tick);
            i += e.Length;
            j += a.Length;
            if (e.SequenceEqual(a))
                continue;

            string message = e.IsEmpty ? $"there's a {what} that shouldn't be there: {List(a, describe)}."
                : a.IsEmpty ? $"a {what} is missing: {List(e, describe)}."
                : $"the {what}s are {List(a, describe)} rather than {List(e, describe)}.";
            where.Add(found, kind, tick, message);
        }
    }

    private static string List<T>(ReadOnlySpan<T> items, Func<T, string> describe)
    {
        var parts = new string[items.Length];
        for (int i = 0; i < items.Length; i++)
            parts[i] = describe(items[i]);
        return string.Join("; ", parts);
    }

    private static string Describe(TempoChange t) =>
        Invariant($"{t.MicrosecondsPerQuarter} microseconds per beat ({EditableTempoMap.BpmOf(Math.Max(1, t.MicrosecondsPerQuarter)):0.###} BPM)");

    private static string Describe(TimeSignatureMarker s) =>
        Invariant($"{s.Numerator}/{s.Denominator} (metronome bytes {s.ClocksPerClick} and {s.ThirtySecondsPerQuarter})");

    private static string Describe(TempoAnchor a) => Invariant($"anchored at {a.AudioMicroseconds} microseconds of audio");

    private static string Describe(TextEvent e) => Invariant($"{Quoted(e.Text)} (meta type {e.Type})");

    private static string Describe(RawEvent e) =>
        e.Bytes.Length <= 16 ? Convert.ToHexString(e.Bytes.Span) : Invariant($"{Convert.ToHexString(e.Bytes.Span[..16])}... ({e.Bytes.Length} bytes)");

    private static string Describe(EditableNote n)
    {
        string lane = n.Lane == ChartNote.OpenLane ? "open" : Invariant($"lane {n.Lane}");
        string dynamics = n.Dynamics switch
        {
            NoteDynamics.Accent => ", accent",
            NoteDynamics.Ghost => ", ghost",
            _ => "",
        };
        return Invariant($"{lane}{(n.DoubleKick ? " 2x kick" : "")}, length {n.Length}{dynamics}");
    }

    private static string Describe(FlaggedChord c)
    {
        var names = new List<string>(3);
        if (c.Flags.HasFlag(ChordFlags.ForceHopo))
            names.Add("force HOPO");
        if (c.Flags.HasFlag(ChordFlags.ForceStrum))
            names.Add("force strum");
        if (c.Flags.HasFlag(ChordFlags.Tap))
            names.Add("tap");
        return names.Count > 0 ? string.Join(" and ", names) : "nothing";
    }

    private static string Describe(DrumMix m) => Invariant($"configuration {m.Configuration}, setting {Quoted(m.Setting)}");

    private static string Describe(Phrase p)
    {
        string kind = p.Kind switch
        {
            PhraseKind.StarPower => "star power",
            PhraseKind.Solo => "solo",
            PhraseKind.DrumFill => "drum fill",
            PhraseKind.Roll => "roll",
            PhraseKind.Trill => "trill",
            PhraseKind.BigRockEnding => "big rock ending",
            _ => p.Kind.ToString(),
        };
        string lowest = p.LowestDifficulty is { } d ? $", down to {d}" : "";
        return Invariant($"{kind}, length {p.Length}{lowest}");
    }

    private static string Describe(TomMarker m)
    {
        string pad = m.Pad switch
        {
            2 => "yellow",
            3 => "blue",
            4 => "green",
            _ => Invariant($"pad {m.Pad}"),
        };
        return Invariant($"{pad}, length {m.Length}");
    }

    private static string Describe(DrumsVariant? variant) => variant switch
    {
        DrumsVariant.FourLane => "4-lane",
        DrumsVariant.Pro => "Pro",
        DrumsVariant.FiveLane => "5-lane",
        null => "unset",
        _ => variant.Value.ToString(),
    };

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string Quoted(string? text) => text is null ? "nothing" : $"\"{text}\"";

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>A part and difficulty, either of which can be none, that differences are reported in.</summary>
    private readonly record struct Where(ChartInstrument? Part, ChartDifficulty? Difficulty)
    {
        public void Add(Found found, ModelDifferenceKind kind, long tick, string what)
        {
            if (found.Done)
                return;
            string place = Part is null ? Invariant($"Tick {tick}")
                : Difficulty is null ? Invariant($"{Part} at tick {tick}")
                : Invariant($"{Part} {Difficulty} at tick {tick}");
            found.Add(kind, Part, Difficulty, tick, $"{place}: {what}");
        }
    }

    /// <summary>The differences found so far, up to <see cref="MaxDifferences"/>.</summary>
    private sealed class Found
    {
        private readonly List<ModelDifference> _differences = [];

        /// <summary>Whether a difference past the limit has been found, so there's no need to look further.</summary>
        public bool Done { get; private set; }

        public void Add(ModelDifferenceKind kind, ChartInstrument? part, ChartDifficulty? difficulty, long? tick, string description)
        {
            if (_differences.Count < MaxDifferences)
                _differences.Add(new ModelDifference(kind, part, difficulty, tick, description));
            else
                Done = true;
        }

        public IReadOnlyList<ModelDifference> Result()
        {
            if (Done)
            {
                _differences.Add(new ModelDifference(ModelDifferenceKind.More, null, null, null,
                    Invariant($"More differences were found than the {MaxDifferences} listed.")));
            }

            return _differences;
        }
    }
}
