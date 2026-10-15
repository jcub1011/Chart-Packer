using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Commands;

/// <summary>
/// Makes Pro drum notes toms or cymbals (charter-spec C6 item 3): adds, splits, merges or removes the fewest tom markers
/// that change only the places given. The markers apply to every difficulty, so a place changes on each difficulty with
/// a note there, and <see cref="SharedWith"/> says which others have one.
/// </summary>
/// <remarks>
/// A marker grows only over ticks with no note on its pad, so the notes beside a place keep what they were, and a piece
/// of a split marker that covers no note is left out. Only Pro drums have cymbals: 4-lane drums' markers are left out on
/// saving (C6 item 9), and 5-lane drums have none.
/// </remarks>
public sealed class SetToms : ChartCommand
{
    private readonly (int Pad, long Tick)[] _places;
    private readonly EditLog _log = new();
    private ChartDifficulty[] _sharedWith = [];

    /// <param name="places">The places, at least one, each a pad (2 yellow, 3 blue, 4 green) and a tick.</param>
    /// <param name="tom">Whether they become toms, rather than cymbals.</param>
    /// <param name="editing">The difficulty being edited, which <see cref="SharedWith"/> leaves out; null for none.</param>
    public SetToms(IEnumerable<(int Pad, long Tick)> places, bool tom, ChartDifficulty? editing = null)
    {
        _places = AtLeastOne(places, nameof(places));
        if (_places.Any(static p => p.Pad is < TomMarker.FirstPad or > TomMarker.LastPad))
            throw new ArgumentException("Only yellow, blue and green notes are cymbals or toms.", nameof(places));
        if (_places.Distinct().Count() != _places.Length)
            throw new ArgumentException("Each place is given once.", nameof(places));
        Tom = tom;
        Editing = editing;
    }

    public IReadOnlyList<(int Pad, long Tick)> Places => _places;

    /// <summary>Whether the places become toms, rather than cymbals.</summary>
    public bool Tom { get; }

    /// <summary>The difficulty being edited, or null.</summary>
    public ChartDifficulty? Editing { get; }

    /// <summary>
    /// The difficulties other than <see cref="Editing"/> with a note at a place the last apply changed, easiest first, for
    /// the editor to say (charter-spec C6 item 3).
    /// </summary>
    public IReadOnlyList<ChartDifficulty> SharedWith => _sharedWith;

    private protected override ChartChange Do(EditableChart chart)
    {
        var drums = PartOf(chart, ChartInstrument.Drums);
        if (chart.DrumsVariant != DrumsVariant.Pro)
            throw new InvalidOperationException("Only Pro drums have cymbals and toms.");
        foreach (var (pad, tick) in _places)
        {
            if (!DrumCymbals.HasPadNote(drums, pad, tick, tick + 1))
                throw new InvalidOperationException(Invariant($"No difficulty has a note on pad {pad} at tick {tick} to make a {(Tom ? "tom" : "cymbal")}."));
        }

        // Worked out on each pad's markers apart, then made as the fewest removals and additions.
        var changed = new List<(int Pad, long Tick)>();
        var removed = new List<TomMarker>();
        var added = new List<TomMarker>();
        foreach (var group in _places.GroupBy(static p => p.Pad))
        {
            int pad = group.Key;
            var before = drums.TomMarkers.Where(m => m.Pad == pad).ToList();
            var after = new List<TomMarker>(before);
            foreach (var (_, tick) in group.OrderBy(static p => p.Tick))
            {
                if (after.Any(m => DrumCymbals.Covers(m, tick)) == Tom)
                    continue;
                if (Tom)
                    MakeTom(drums, pad, tick, after);
                else
                    MakeCymbal(drums, pad, tick, after);
                changed.Add((pad, tick));
            }

            removed.AddRange(Except(before, after));
            added.AddRange(Except(after, before));
        }

        foreach (var marker in removed)
            _log.RemoveAt(drums.TomMarkers, drums.TomMarkers.IndexOf(marker));
        foreach (var marker in added)
            _log.Place(drums.TomMarkers, marker);

        _sharedWith = [.. changed
            .SelectMany(p => DrumCymbals.DifficultiesWithNote(drums, p.Pad, p.Tick))
            .Where(d => d != Editing)
            .Distinct()
            .Order()];
        return TomEdits.Spans(removed.Concat(added));
    }

    private protected override void Undo(EditableChart chart) => _log.Undo();

    // A tom at tick, which no marker covers: a marker there, joined to the one before or after it when no note on the pad
    // comes between.
    private static void MakeTom(EditablePart drums, int pad, long tick, List<TomMarker> markers)
    {
        int before = markers.FindLastIndex(m => m.Tick < tick);
        int after = markers.FindIndex(m => m.Tick > tick);
        bool joinBefore = before >= 0 && !DrumCymbals.HasPadNote(drums, pad, DrumCymbals.CoverEnd(markers[before]), tick);
        bool joinAfter = after >= 0 && !DrumCymbals.HasPadNote(drums, pad, tick + 1, markers[after].Tick);
        long start = joinBefore ? markers[before].Tick : tick;
        long end = joinAfter ? DrumCymbals.CoverEnd(markers[after]) : tick + 1;
        if (joinAfter)
            markers.RemoveAt(after);
        if (joinBefore)
            markers.RemoveAt(before);
        Insert(markers, new TomMarker(pad, start, end - start));
    }

    // A cymbal at tick: each marker covering it split round it, keeping only the pieces that cover a note.
    private static void MakeCymbal(EditablePart drums, int pad, long tick, List<TomMarker> markers)
    {
        for (int i = markers.Count - 1; i >= 0; i--)
        {
            var marker = markers[i];
            if (!DrumCymbals.Covers(marker, tick))
                continue;
            markers.RemoveAt(i);
            long end = DrumCymbals.CoverEnd(marker);
            if (DrumCymbals.HasPadNote(drums, pad, marker.Tick, tick))
                Insert(markers, marker with { Length = tick - marker.Tick });
            if (DrumCymbals.HasPadNote(drums, pad, tick + 1, end))
                Insert(markers, new TomMarker(pad, tick + 1, end - tick - 1));
        }
    }

    private static void Insert(List<TomMarker> markers, TomMarker marker)
    {
        int index = markers.FindIndex(m => TomMarker.Order(m, marker) > 0);
        markers.Insert(index < 0 ? markers.Count : index, marker);
    }

    // The markers of a that b hasn't, each as many times as a has it more than b.
    private static List<TomMarker> Except(List<TomMarker> a, List<TomMarker> b)
    {
        var left = new List<TomMarker>(b);
        var only = new List<TomMarker>();
        foreach (var marker in a)
        {
            if (!left.Remove(marker))
                only.Add(marker);
        }

        return only;
    }
}

/// <summary>Removes tom markers from the drums, such as one that covers no note (CHR040).</summary>
/// <param name="markers">The markers, at least one, each exactly as the drums have it.</param>
public sealed class RemoveTomMarkers(IEnumerable<TomMarker> markers) : ChartCommand
{
    private readonly TomMarker[] _markers = AtLeastOne(markers, nameof(markers));
    private readonly EditLog _log = new();

    public IReadOnlyList<TomMarker> Markers => _markers;

    private protected override ChartChange Do(EditableChart chart)
    {
        var list = PartOf(chart, ChartInstrument.Drums).TomMarkers;
        var scratch = list.Clone();
        foreach (var marker in _markers)
        {
            if (!scratch.Remove(marker))
                throw new InvalidOperationException(Invariant($"The drums have no tom marker on pad {marker.Pad} at tick {marker.Tick} of length {marker.Length}."));
        }

        foreach (var marker in _markers)
            _log.RemoveAt(list, list.IndexOf(marker));
        return TomEdits.Spans(_markers);
    }

    private protected override void Undo(EditableChart chart) => _log.Undo();
}

/// <summary>Makes Expert kicks 2× kicks (Expert+), or 2× kicks plain kicks (charter-spec C6 item 2).</summary>
public sealed class SetDoubleKick : ChartCommand
{
    private readonly EditableNote[] _kicks;
    private EditableNote[] _changed = [];

    /// <param name="kicks">The kicks, at least one, each exactly as Expert drums have it, and none already what it's to become.</param>
    /// <param name="doubleKick">Whether they become 2× kicks, rather than plain kicks.</param>
    public SetDoubleKick(IEnumerable<EditableNote> kicks, bool doubleKick)
    {
        _kicks = AtLeastOne(kicks, nameof(kicks));
        NoteEdits.CheckDistinct(_kicks, nameof(kicks));
        if (_kicks.Any(k => k.Lane != 0 || k.DoubleKick == doubleKick))
            throw new ArgumentException(doubleKick ? "Only plain kicks become 2x kicks." : "Only 2x kicks become plain kicks.", nameof(kicks));
        DoubleKick = doubleKick;
    }

    public IReadOnlyList<EditableNote> Kicks => _kicks;

    public bool DoubleKick { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, ChartInstrument.Drums, ChartDifficulty.Expert);
        NoteEdits.CheckPresent(track, ChartInstrument.Drums, _kicks);
        var changed = new EditableNote[_kicks.Length];
        for (int i = 0; i < _kicks.Length; i++)
        {
            changed[i] = _kicks[i] with { DoubleKick = DoubleKick };
            track.Notes.Check(changed[i]);
            if (track.TryGetNote(changed[i].Tick, 0, DoubleKick, out var there))
                throw new InvalidOperationException(Invariant($"Drums Expert already has {Describe(there)}."));
        }

        NoteEdits.Replace(track.Notes, _kicks, changed);
        _changed = changed;
        return NoteEdits.Spans(ChartInstrument.Drums, ChartDifficulty.Expert, _kicks);
    }

    private protected override void Undo(EditableChart chart) => NoteEdits.Replace(TrackOf(chart, ChartInstrument.Drums, ChartDifficulty.Expert).Notes, _changed, _kicks);
}

/// <summary>
/// Adds a disco flip section to one drums difficulty (charter-spec C6 item 7), as mix events (see
/// <see cref="DiscoFlips"/>). The notes stay as they are.
/// </summary>
public sealed class AddDiscoFlip : ChartCommand
{
    private readonly EditLog _log = new();

    /// <param name="tick">Where it starts.</param>
    /// <param name="endTick">Where it ends, after <paramref name="tick"/>.</param>
    public AddDiscoFlip(ChartDifficulty difficulty, long tick, long endTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endTick, tick);
        Difficulty = difficulty;
        Tick = tick;
        EndTick = endTick;
    }

    public ChartDifficulty Difficulty { get; }

    public long Tick { get; }

    public long EndTick { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var mixes = TrackOf(chart, ChartInstrument.Drums, Difficulty).Mixes;
        DiscoFlips.Add(mixes.Clone(), Tick, EndTick, new EditLog());
        DiscoFlips.Add(mixes, Tick, EndTick, _log);
        return ChartChange.Of(ChartInstrument.Drums, Difficulty, Tick, EndTick);
    }

    private protected override void Undo(EditableChart chart) => _log.Undo();
}

/// <summary>Removes a disco flip section from one drums difficulty (charter-spec C6 item 7). The notes stay as they are.</summary>
/// <param name="range">The section, exactly as <see cref="DiscoFlips.RangesOf"/> gives it.</param>
public sealed class RemoveDiscoFlip(ChartDifficulty difficulty, DiscoFlipRange range) : ChartCommand
{
    private readonly EditLog _log = new();

    public ChartDifficulty Difficulty { get; } = difficulty;

    public DiscoFlipRange Range { get; } = range;

    private protected override ChartChange Do(EditableChart chart)
    {
        var mixes = TrackOf(chart, ChartInstrument.Drums, Difficulty).Mixes;
        DiscoFlips.Remove(mixes.Clone(), Range, new EditLog());
        DiscoFlips.Remove(mixes, Range, _log);
        return ChartChange.Of(ChartInstrument.Drums, Difficulty, Range.Tick, Range.EndTick ?? long.MaxValue);
    }

    private protected override void Undo(EditableChart chart) => _log.Undo();
}

/// <summary>Moves or resizes a disco flip section of one drums difficulty (charter-spec C6 item 7). The notes stay as they are.</summary>
public sealed class ChangeDiscoFlip : ChartCommand
{
    private readonly EditLog _log = new();

    /// <param name="from">The section, exactly as <see cref="DiscoFlips.RangesOf"/> gives it.</param>
    /// <param name="tick">Where it's to start.</param>
    /// <param name="endTick">Where it's to end, after <paramref name="tick"/>.</param>
    public ChangeDiscoFlip(ChartDifficulty difficulty, DiscoFlipRange from, long tick, long endTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endTick, tick);
        Difficulty = difficulty;
        From = from;
        Tick = tick;
        EndTick = endTick;
    }

    public ChartDifficulty Difficulty { get; }

    public DiscoFlipRange From { get; }

    public long Tick { get; }

    public long EndTick { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var mixes = TrackOf(chart, ChartInstrument.Drums, Difficulty).Mixes;
        var scratch = mixes.Clone();
        var check = new EditLog();
        DiscoFlips.Remove(scratch, From, check);
        DiscoFlips.Add(scratch, Tick, EndTick, check);

        DiscoFlips.Remove(mixes, From, _log);
        DiscoFlips.Add(mixes, Tick, EndTick, _log);
        return ChartChange.Of(ChartInstrument.Drums, Difficulty, From.Tick, From.EndTick ?? long.MaxValue)
            .Merge(ChartChange.Of(ChartInstrument.Drums, Difficulty, Tick, EndTick));
    }

    private protected override void Undo(EditableChart chart) => _log.Undo();
}

/// <summary>
/// Converts the drums to or from 5-lane (charter-spec C6 item 8) with the fixed mapping of §7.6
/// (<see cref="DrumConversion"/>), and makes the song's drums that variant, as one step. Notes it can't place are left
/// out, and listed in <see cref="Report"/>; undoing brings them back. Switching between 4-lane and Pro isn't a conversion
/// (<see cref="SetDrumsVariant"/>).
/// </summary>
public sealed class ConvertDrums : ChartCommand
{
    private EditableNote[][] _oldNotes = [];
    private TomMarker[] _oldMarkers = [];
    private DrumsVariant? _oldVariant;

    public ConvertDrums(DrumsVariant to)
    {
        if (!Enum.IsDefined(to))
            throw new ArgumentException("Drums are 4-lane, Pro or 5-lane.", nameof(to));
        To = to;
    }

    /// <summary>The variant the drums are converted to.</summary>
    public DrumsVariant To { get; }

    /// <summary>What the last apply couldn't convert as the mapping says; null until applied, or when it changed nothing.</summary>
    public DrumConversionReport? Report { get; private set; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var drums = PartOf(chart, ChartInstrument.Drums);
        var from = chart.DrumsVariant ?? throw new InvalidOperationException("The song doesn't say how its drums are charted, so they can't be converted.");
        Report = null;
        _oldVariant = from;
        if (from == To)
            return ChartChange.None;
        if ((from == DrumsVariant.FiveLane) == (To == DrumsVariant.FiveLane))
            throw new InvalidOperationException("Switching between 4-lane and Pro drums isn't a conversion: the notes stay as they are.");

        var (notes, markers, report) = DrumConversion.Convert(drums, from, To);
        _oldNotes = [.. drums.Tracks.Select(static t => t.Notes.AsSpan().ToArray())];
        _oldMarkers = [.. drums.TomMarkers];
        foreach (var track in drums.Tracks)
            ChartTickSnapshot.Refill(track.Notes, notes[(int)track.Difficulty]);
        ChartTickSnapshot.Refill(drums.TomMarkers, markers);
        chart.DrumsVariant = To;
        Report = report;
        return ChartChange.Of([PartEdits.Whole(ChartInstrument.Drums)], SongChanges.DrumsVariant);
    }

    private protected override void Undo(EditableChart chart)
    {
        if (Report is null)
            return;
        var drums = PartOf(chart, ChartInstrument.Drums);
        foreach (var track in drums.Tracks)
            ChartTickSnapshot.Refill(track.Notes, _oldNotes[(int)track.Difficulty]);
        ChartTickSnapshot.Refill(drums.TomMarkers, _oldMarkers);
        chart.DrumsVariant = _oldVariant;
        (_oldNotes, _oldMarkers) = ([], []);
    }
}

/// <summary>Changes the song's drum variant (charter-spec C1, Parts; C6 item 9) the way it needs: a switch between 4-lane and Pro, or a conversion to or from 5-lane.</summary>
public static class DrumVariantChange
{
    /// <summary>
    /// The command that makes <paramref name="chart"/>'s drums <paramref name="to"/>: <see cref="ConvertDrums"/> when
    /// they have notes and go to or from 5-lane, otherwise <see cref="SetDrumsVariant"/>.
    /// </summary>
    public static IChartCommand For(EditableChart chart, DrumsVariant to)
    {
        ArgumentNullException.ThrowIfNull(chart);
        bool converts = chart.PartOf(ChartInstrument.Drums) is { HasNotes: true }
            && chart.DrumsVariant is { } from
            && (from == DrumsVariant.FiveLane) != (to == DrumsVariant.FiveLane);
        return converts ? new ConvertDrums(to) : new SetDrumsVariant(to);
    }
}

/// <summary>What the tom marker commands share.</summary>
internal static class TomEdits
{
    /// <summary>The ticks <paramref name="markers"/> cover, on every difficulty.</summary>
    public static ChartChange Spans(IEnumerable<TomMarker> markers) =>
        ChartChange.Of(markers.Select(static m => new ChartLocation(ChartInstrument.Drums, null, m.Tick, DrumCymbals.CoverEnd(m) - 1)));
}
