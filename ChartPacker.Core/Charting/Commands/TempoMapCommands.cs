using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// Adds a tempo marker (charter-spec C4 item 1). Notes keep their ticks, so they move in time with the grid: only the
/// tempo map changes. A tick has at most one marker placed by the editor; one that has a marker is changed with
/// <see cref="SetTempo"/>.
/// </summary>
/// <param name="marker">
/// The marker, in whole microseconds per quarter note as MIDI stores it
/// (<see cref="EditableTempoMap.MicrosecondsPerQuarterOf"/> gives them from a BPM).
/// </param>
public sealed class AddTempo(TempoChange marker) : ChartCommand
{
    private int _index = -1;

    public TempoChange Marker { get; } = marker;

    private protected override ChartChange Do(EditableChart chart)
    {
        var tempos = chart.TempoMap.Tempos;
        tempos.Check(Marker);
        if (!tempos.At(Marker.Tick).IsEmpty)
            throw new InvalidOperationException(FormattableString.Invariant($"There's already a tempo marker at tick {Marker.Tick}; change it instead."));
        _index = tempos.Place(Marker);
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart) => chart.TempoMap.Tempos.RemoveAt(_index);
}

/// <summary>
/// Removes a tempo marker, keeping ticks. Its anchor, if it has one and no other marker is at its tick, goes with it, and
/// comes back when the removal is undone. The marker the song starts with can be changed but not removed.
/// </summary>
/// <param name="marker">The marker, exactly as the tempo map has it.</param>
public sealed class RemoveTempo(TempoChange marker) : ChartCommand
{
    private int _index = -1;
    private TempoAnchor? _anchor;

    public TempoChange Marker { get; } = marker;

    private protected override ChartChange Do(EditableChart chart)
    {
        var map = chart.TempoMap;
        int index = map.Tempos.IndexOf(Marker);
        if (index < 0)
            throw new InvalidOperationException(FormattableString.Invariant($"There's no tempo marker of {Marker.MicrosecondsPerQuarter} microseconds per beat at tick {Marker.Tick}."));
        if (Marker.Tick == 0 && map.Tempos.At(0).Length == 1)
            throw new InvalidOperationException("The tempo the song starts with can be changed but not removed.");

        map.Tempos.RemoveAt(index);
        _index = index;
        _anchor = null;
        if (map.Tempos.At(Marker.Tick).IsEmpty)
        {
            int anchor = map.Anchors.IndexOfPlace(new TempoAnchor(Marker.Tick, 0));
            if (anchor >= 0)
            {
                _anchor = map.Anchors[anchor];
                map.Anchors.RemoveAt(anchor);
            }
        }

        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart)
    {
        var map = chart.TempoMap;
        map.Tempos.Insert(_index, Marker);
        if (_anchor is { } anchor)
            map.Anchors.Add(anchor);
    }
}

/// <summary>Changes a tempo marker's tempo, keeping ticks.</summary>
public sealed class SetTempo : ChartCommand
{
    private int _index = -1;

    /// <param name="marker">The marker, exactly as the tempo map has it.</param>
    /// <param name="microsecondsPerQuarter">Its new tempo, in whole microseconds per quarter note.</param>
    public SetTempo(TempoChange marker, long microsecondsPerQuarter)
    {
        Marker = marker;
        Changed = marker with { MicrosecondsPerQuarter = microsecondsPerQuarter };
    }

    /// <summary>The marker as it was.</summary>
    public TempoChange Marker { get; }

    /// <summary>The marker as it becomes.</summary>
    public TempoChange Changed { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var tempos = chart.TempoMap.Tempos;
        int index = tempos.IndexOf(Marker);
        if (index < 0)
            throw new InvalidOperationException(FormattableString.Invariant($"There's no tempo marker of {Marker.MicrosecondsPerQuarter} microseconds per beat at tick {Marker.Tick}."));
        tempos.Check(Changed);

        tempos.RemoveAt(index);
        tempos.Insert(index, Changed);
        _index = index;
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart)
    {
        var tempos = chart.TempoMap.Tempos;
        tempos.RemoveAt(_index);
        tempos.Insert(_index, Marker);
    }
}

/// <summary>
/// Adds a time-signature marker (charter-spec C4 item 1): a numerator from 1 to <see cref="MaxNumerator"/> and a
/// power-of-two denominator. A tick has at most one marker placed by the editor.
/// </summary>
public sealed class AddTimeSignature(TimeSignatureMarker marker) : ChartCommand
{
    /// <summary>The most beats a measure placed in the editor can have. A loaded chart can have more, up to 255.</summary>
    public const int MaxNumerator = 64;

    private int _index = -1;

    public TimeSignatureMarker Marker { get; } = marker;

    private protected override ChartChange Do(EditableChart chart)
    {
        var signatures = chart.TempoMap.TimeSignatures;
        SignatureEdits.Check(signatures, Marker);
        if (!signatures.At(Marker.Tick).IsEmpty)
            throw new InvalidOperationException(FormattableString.Invariant($"There's already a time signature at tick {Marker.Tick}; change it instead."));
        _index = signatures.Place(Marker);
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart) => chart.TempoMap.TimeSignatures.RemoveAt(_index);
}

/// <summary>Removes a time-signature marker. The one the song starts with can be changed but not removed.</summary>
/// <param name="marker">The marker, exactly as the tempo map has it.</param>
public sealed class RemoveTimeSignature(TimeSignatureMarker marker) : ChartCommand
{
    private int _index = -1;

    public TimeSignatureMarker Marker { get; } = marker;

    private protected override ChartChange Do(EditableChart chart)
    {
        var signatures = chart.TempoMap.TimeSignatures;
        int index = SignatureEdits.IndexOf(signatures, Marker);
        if (Marker.Tick == 0 && signatures.At(0).Length == 1)
            throw new InvalidOperationException("The time signature the song starts with can be changed but not removed.");

        signatures.RemoveAt(index);
        _index = index;
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart) => chart.TempoMap.TimeSignatures.Insert(_index, Marker);
}

/// <summary>Changes a time-signature marker's signature, keeping its metronome bytes.</summary>
public sealed class SetTimeSignature : ChartCommand
{
    private int _index = -1;

    /// <param name="marker">The marker, exactly as the tempo map has it.</param>
    public SetTimeSignature(TimeSignatureMarker marker, int numerator, int denominator)
    {
        Marker = marker;
        Changed = marker with { Numerator = numerator, Denominator = denominator };
    }

    /// <summary>The marker as it was.</summary>
    public TimeSignatureMarker Marker { get; }

    /// <summary>The marker as it becomes.</summary>
    public TimeSignatureMarker Changed { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var signatures = chart.TempoMap.TimeSignatures;
        int index = SignatureEdits.IndexOf(signatures, Marker);
        SignatureEdits.Check(signatures, Changed);

        signatures.RemoveAt(index);
        signatures.Insert(index, Changed);
        _index = index;
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart)
    {
        var signatures = chart.TempoMap.TimeSignatures;
        signatures.RemoveAt(_index);
        signatures.Insert(_index, Marker);
    }
}

/// <summary>
/// Anchors the tempo marker at a tick to a time in the audio (charter-spec C4 item 2), or moves its anchor there. Only a
/// tick with a tempo marker can be anchored.
/// </summary>
public sealed class SetAnchor : ChartCommand
{
    private TempoAnchor? _previous;

    /// <param name="tick">The tempo marker's tick.</param>
    /// <param name="audioMicroseconds">Its time in the audio, in whole microseconds.</param>
    public SetAnchor(long tick, long audioMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        Anchor = new TempoAnchor(tick, audioMicroseconds);
    }

    public TempoAnchor Anchor { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var map = chart.TempoMap;
        if (map.Tempos.At(Anchor.Tick).IsEmpty)
            throw new InvalidOperationException(FormattableString.Invariant($"Only a tempo marker can be anchored, and there's none at tick {Anchor.Tick}."));

        int index = map.Anchors.IndexOfPlace(Anchor);
        _previous = index >= 0 ? map.Anchors[index] : null;
        if (index >= 0)
            map.Anchors.RemoveAt(index);
        map.Anchors.Add(Anchor);
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart)
    {
        var anchors = chart.TempoMap.Anchors;
        anchors.Remove(Anchor);
        if (_previous is { } previous)
            anchors.Add(previous);
    }
}

/// <summary>Unanchors the tempo marker at a tick.</summary>
public sealed class ClearAnchor : ChartCommand
{
    private TempoAnchor _previous;

    public ClearAnchor(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        Tick = tick;
    }

    public long Tick { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var anchors = chart.TempoMap.Anchors;
        int index = anchors.IndexOfPlace(new TempoAnchor(Tick, 0));
        if (index < 0)
            throw new InvalidOperationException(FormattableString.Invariant($"The tempo marker at tick {Tick} isn't anchored."));
        _previous = anchors[index];
        anchors.RemoveAt(index);
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart) => chart.TempoMap.Anchors.Add(_previous);
}

/// <summary>
/// Replaces the whole tempo map in one step (charter-spec C4 items 3 and 7), such as a proposed tempo map, keeping either
/// every item's tick or its time.
/// </summary>
/// <remarks>
/// <para>Keeping times re-ticks everything else in the chart, passthrough tracks included, with
/// <see cref="TickMaps.KeepTimes"/> from the old map to the new. Re-ticking rounds, so undoing doesn't re-tick back: it
/// puts back the lists and passthrough tracks as they were, which the command keeps. That's the one command that keeps a
/// copy of much of the chart, as only a copy can undo a rounding exactly.</para>
/// <para>Items re-ticking moves onto another's place are left out, and listed in <see cref="Collisions"/>.</para>
/// <para>Anchors belong to their markers and aren't re-ticked: the caller gives them at their markers' new ticks.</para>
/// </remarks>
public sealed class ChangeTempoMap : ChartCommand
{
    private readonly TempoChange[] _tempos;
    private readonly TimeSignatureMarker[] _signatures;
    private readonly TempoAnchor[] _anchors;
    private TempoChange[] _oldTempos = [];
    private TimeSignatureMarker[] _oldSignatures = [];
    private TempoAnchor[] _oldAnchors = [];
    private ChartTickSnapshot? _retick;

    /// <param name="tempos">The tempo markers, in the order they're kept at a tick.</param>
    /// <param name="timeSignatures">The time-signature markers, in the order they're kept at a tick.</param>
    /// <param name="anchors">The anchors, at most one per tick, each at a tick of <paramref name="tempos"/>.</param>
    /// <param name="keepTimes">Whether everything else keeps its time, rather than its tick.</param>
    public ChangeTempoMap(IEnumerable<TempoChange> tempos, IEnumerable<TimeSignatureMarker> timeSignatures, IEnumerable<TempoAnchor> anchors, bool keepTimes)
    {
        ArgumentNullException.ThrowIfNull(tempos);
        ArgumentNullException.ThrowIfNull(timeSignatures);
        ArgumentNullException.ThrowIfNull(anchors);
        _tempos = [.. tempos];
        _signatures = [.. timeSignatures];
        _anchors = [.. anchors];
        var tempoTicks = _tempos.Select(static t => t.Tick).ToHashSet();
        if (_anchors.Select(static a => a.Tick).Distinct().Count() != _anchors.Length)
            throw new ArgumentException("A tick has at most one anchor.", nameof(anchors));
        foreach (var anchor in _anchors)
        {
            if (!tempoTicks.Contains(anchor.Tick))
                throw new ArgumentException(FormattableString.Invariant($"Only a tempo marker can be anchored, and there's none at tick {anchor.Tick}."), nameof(anchors));
        }

        KeepTimes = keepTimes;
    }

    public IReadOnlyList<TempoChange> Tempos => _tempos;

    public IReadOnlyList<TimeSignatureMarker> TimeSignatures => _signatures;

    public IReadOnlyList<TempoAnchor> Anchors => _anchors;

    /// <summary>Whether everything else keeps its time, rather than its tick.</summary>
    public bool KeepTimes { get; }

    /// <summary>What re-ticking did, when the last apply kept times; null otherwise.</summary>
    public RetickReport? Report { get; private set; }

    /// <summary>The items the last apply's re-ticking left out, each having moved onto another's place.</summary>
    public IReadOnlyList<RetickCollision> Collisions => Report?.Collisions ?? [];

    private protected override ChartChange Do(EditableChart chart)
    {
        var map = chart.TempoMap;
        foreach (var tempo in _tempos)
            map.Tempos.Check(tempo);
        var kept = map.TimeSignatures.ToHashSet();
        foreach (var signature in _signatures)
        {
            // A loaded signature past what the editor places is kept as it is.
            if (kept.Contains(signature))
                map.TimeSignatures.Check(signature);
            else
                SignatureEdits.Check(map.TimeSignatures, signature);
        }

        foreach (var anchor in _anchors)
            map.Anchors.Check(anchor);

        var oldTempos = map.Tempos.AsSpan().ToArray();
        var oldSignatures = map.TimeSignatures.AsSpan().ToArray();
        var oldAnchors = map.Anchors.AsSpan().ToArray();
        var from = map.ToTempoMap();
        Refill(map, _tempos, _signatures, _anchors);

        RetickReport? report = null;
        ChartTickSnapshot? retick = null;
        if (KeepTimes)
        {
            try
            {
                (report, retick) = ChartRetick.Apply(chart, TickMaps.KeepTimes(from, map.ToTempoMap()));
            }
            catch
            {
                Refill(map, oldTempos, oldSignatures, oldAnchors);
                throw;
            }
        }

        (_oldTempos, _oldSignatures, _oldAnchors) = (oldTempos, oldSignatures, oldAnchors);
        (Report, _retick) = (report, retick);
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart)
    {
        _retick?.Restore();
        Refill(chart.TempoMap, _oldTempos, _oldSignatures, _oldAnchors);
    }

    /// <summary>Empties the tempo map's lists and fills them with these, in their order.</summary>
    internal static void Refill(EditableTempoMap map, TempoChange[] tempos, TimeSignatureMarker[] signatures, TempoAnchor[] anchors)
    {
        ChartTickSnapshot.Refill(map.Tempos, tempos);
        ChartTickSnapshot.Refill(map.TimeSignatures, signatures);
        ChartTickSnapshot.Refill(map.Anchors, anchors);
    }
}

//// <summary>
/// Changes the chart's resolution (charter-spec C1, Changing the resolution), keeping every item's time. The tempo and
/// time-signature markers' ticks are scaled by the new resolution over the old, to the nearest tick, with their tempos
/// unchanged, and each anchor moves with its marker; everything else is re-ticked as a tempo change that keeps times is
/// (<see cref="ChangeTempoMap"/>), the passthrough tracks included.
/// </summary>
/// <remarks>
/// <para>Undoing puts every tick back as it was, from what the command keeps, as re-ticking rounds.</para>
/// <para>Items left out because another lands on their place are listed in <see cref="Collisions"/> and
/// <see cref="LeftOut"/>. <see cref="ResolutionChangeReport.Analyze"/> works out beforehand what the change would move.</para>
/// <para>The same resolution changes nothing.</para>
/// </remarks>
public sealed class ChangeResolution : ChartCommand
{
    private int _oldResolution;
    private TempoChange[] _oldTempos = [];
    private TimeSignatureMarker[] _oldSignatures = [];
    private TempoAnchor[] _oldAnchors = [];
    private ChartTickSnapshot? _retick;

    /// <param name="resolution">The new ticks per beat.</param>
    public ChangeResolution(int resolution)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        if (resolution > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "The resolution is too fine for a MIDI file.");
        Resolution = resolution;
    }

    /// <summary>The new ticks per beat.</summary>
    public int Resolution { get; }

    /// <summary>What re-ticking did, when the last apply changed the resolution; null otherwise.</summary>
    public RetickReport? Report { get; private set; }

    /// <summary>The items the last apply's re-ticking left out, each having moved onto another's place.</summary>
    public IReadOnlyList<RetickCollision> Collisions => Report?.Collisions ?? [];

    /// <summary>
    /// What the last apply left out or made the games read differently, in words: the markers and anchors that landed on
    /// another's tick, then <see cref="Collisions"/>.
    /// </summary>
    public IReadOnlyList<string> LeftOut { get; private set; } = [];

    private protected override ChartChange Do(EditableChart chart)
    {
        var map = chart.TempoMap;
        _oldResolution = map.Resolution;
        _retick = null;
        if (map.Resolution == Resolution)
        {
            (Report, LeftOut) = (null, []);
            return ChartChange.None;
        }

        var oldTempos = map.Tempos.AsSpan().ToArray();
        var oldSignatures = map.TimeSignatures.AsSpan().ToArray();
        var oldAnchors = map.Anchors.AsSpan().ToArray();
        var scaled = ResolutionScaling.Markers(map, Resolution);
        var from = map.ToTempoMap();

        map.Resolution = Resolution;
        ChangeTempoMap.Refill(map, scaled.Tempos, scaled.Signatures, scaled.Anchors);
        RetickReport report;
        ChartTickSnapshot retick;
        try
        {
            (report, retick) = ChartRetick.Apply(chart, TickMaps.KeepTimes(from, map.ToTempoMap()));
        }
        catch
        {
            map.Resolution = _oldResolution;
            ChangeTempoMap.Refill(map, oldTempos, oldSignatures, oldAnchors);
            throw;
        }

        (_oldTempos, _oldSignatures, _oldAnchors) = (oldTempos, oldSignatures, oldAnchors);
        (Report, _retick) = (report, retick);
        LeftOut = [.. scaled.LeftOut, .. report.Collisions.Select(static c => c.Description)];
        return ChartChange.Of(SongChanges.TempoMap);
    }

    private protected override void Undo(EditableChart chart)
    {
        if (_retick is null)
            return;
        _retick.Restore();
        var map = chart.TempoMap;
        map.Resolution = _oldResolution;
        ChangeTempoMap.Refill(map, _oldTempos, _oldSignatures, _oldAnchors);
        _retick = null;
    }
}

/// <summary>
/// Sets the tempo and time signature the song starts with, the markers at tick 0 (charter-spec C1, Timing), keeping
/// either every item's tick or its time, as any tempo edit does (C4). A tick 0 with no marker gets one; one with several
/// has the last changed, as that's the one the games read. Anchors stay where they are.
/// </summary>
public sealed class SetStartingTempo : ChartCommand
{
    private CompositeCommand? _applied;

    /// <param name="microsecondsPerQuarter">The starting tempo, in whole microseconds per quarter note, or null to leave it.</param>
    /// <param name="numerator">The starting time signature's beats, or null to leave it (with <paramref name="denominator"/>).</param>
    /// <param name="denominator">The starting time signature's note value, or null to leave it.</param>
    /// <param name="keepTimes">Whether everything keeps its time, rather than its tick, when the tempo changes.</param>
    public SetStartingTempo(long? microsecondsPerQuarter, int? numerator, int? denominator, bool keepTimes)
    {
        if (numerator.HasValue != denominator.HasValue)
            throw new ArgumentException("A time signature needs both its numerator and its denominator.", nameof(numerator));
        MicrosecondsPerQuarter = microsecondsPerQuarter;
        Numerator = numerator;
        Denominator = denominator;
        KeepTimes = keepTimes;
    }

    /// <summary>The starting tempo, or null to leave it.</summary>
    public long? MicrosecondsPerQuarter { get; }

    /// <summary>The starting time signature's beats, or null to leave it.</summary>
    public int? Numerator { get; }

    /// <summary>The starting time signature's note value, or null to leave it.</summary>
    public int? Denominator { get; }

    /// <summary>Whether everything keeps its time, rather than its tick, when the tempo changes.</summary>
    public bool KeepTimes { get; }

    /// <summary>The items the last apply's re-ticking left out, when it kept times.</summary>
    public IReadOnlyList<RetickCollision> Collisions =>
        _applied?.Commands.OfType<ChangeTempoMap>().FirstOrDefault()?.Collisions ?? [];

    private protected override ChartChange Do(EditableChart chart)
    {
        var map = chart.TempoMap;
        var commands = new List<IChartCommand>();
        if (MicrosecondsPerQuarter is { } tempo)
        {
            var atZero = map.Tempos.At(0);
            if (atZero.IsEmpty)
            {
                commands.Add(KeepTimes
                    ? KeepingTimes(map, [new TempoChange(0, tempo), .. map.Tempos])
                    : new AddTempo(new TempoChange(0, tempo)));
            }
            else if (atZero[^1].MicrosecondsPerQuarter != tempo)
            {
                var last = atZero[^1];
                if (KeepTimes)
                {
                    TempoChange[] tempos = [.. map.Tempos];
                    tempos[atZero.Length - 1] = last with { MicrosecondsPerQuarter = tempo };
                    commands.Add(KeepingTimes(map, tempos));
                }
                else
                {
                    commands.Add(new SetTempo(last, tempo));
                }
            }
        }

        if (Numerator is { } numerator && Denominator is { } denominator)
        {
            // A time signature moves nothing in time, so it keeps ticks either way.
            var atZero = map.TimeSignatures.At(0);
            if (atZero.IsEmpty)
                commands.Add(new AddTimeSignature(new TimeSignatureMarker(0, numerator, denominator)));
            else if (atZero[^1].Numerator != numerator || atZero[^1].Denominator != denominator)
                commands.Add(new SetTimeSignature(atZero[^1], numerator, denominator));
        }

        _applied = null;
        if (commands.Count == 0)
            return ChartChange.None;

        var command = new CompositeCommand(commands);
        command.Apply(chart);
        _applied = command;
        return command.Affected;
    }

    private protected override void Undo(EditableChart chart)
    {
        _applied?.Revert(chart);
        _applied = null;
    }

    private static ChangeTempoMap KeepingTimes(EditableTempoMap map, IEnumerable<TempoChange> tempos) =>
        new(tempos, map.TimeSignatures, map.Anchors, keepTimes: true);
}

/// <summary>Moves a tempo map's markers and anchors to a new resolution, for <see cref="ChangeResolution"/>.</summary>
internal static class ResolutionScaling
{
    /// <summary><paramref name="tick"/> at <paramref name="to"/> ticks per beat rather than <paramref name="from"/>, to the nearest tick, halves up.</summary>
    public static long Scale(long tick, int from, int to) => (tick * to * 2 + from) / (2L * from);

    /// <summary>
    /// The markers and anchors of <paramref name="map"/> at <paramref name="resolution"/>, in their order, and what landing
    /// on one tick does to them, in words. Markers that land on one tick are all kept, and the games read the last; an
    /// anchor that lands on another's tick is left out.
    /// </summary>
    public static (TempoChange[] Tempos, TimeSignatureMarker[] Signatures, TempoAnchor[] Anchors, IReadOnlyList<string> LeftOut) Markers(EditableTempoMap map, int resolution)
    {
        int from = map.Resolution;
        var leftOut = new List<string>();

        var tempos = map.Tempos.AsSpan().ToArray();
        for (int i = 0; i < tempos.Length; i++)
            tempos[i] = tempos[i] with { Tick = Scale(tempos[i].Tick, from, resolution) };
        for (int i = 1; i < tempos.Length; i++)
        {
            if (tempos[i].Tick == tempos[i - 1].Tick && map.Tempos[i].Tick != map.Tempos[i - 1].Tick)
                leftOut.Add(Invariant($"The tempo marker at tick {map.Tempos[i - 1].Tick} would land on tick {tempos[i].Tick} with the one at tick {map.Tempos[i].Tick}, and the games read only the last."));
        }

        var signatures = map.TimeSignatures.AsSpan().ToArray();
        for (int i = 0; i < signatures.Length; i++)
            signatures[i] = signatures[i] with { Tick = Scale(signatures[i].Tick, from, resolution) };
        for (int i = 1; i < signatures.Length; i++)
        {
            if (signatures[i].Tick == signatures[i - 1].Tick && map.TimeSignatures[i].Tick != map.TimeSignatures[i - 1].Tick)
                leftOut.Add(Invariant($"The time signature at tick {map.TimeSignatures[i - 1].Tick} would land on tick {signatures[i].Tick} with the one at tick {map.TimeSignatures[i].Tick}, and the games read only the last."));
        }

        var anchors = new List<TempoAnchor>(map.Anchors.Count);
        foreach (var anchor in map.Anchors)
        {
            long tick = Scale(anchor.Tick, from, resolution);
            if (anchors.Count > 0 && anchors[^1].Tick == tick)
            {
                leftOut.Add(Invariant($"The anchor of the tempo marker at tick {anchor.Tick} would land on another's, at tick {tick}, so it would be left out."));
                continue;
            }

            anchors.Add(anchor with { Tick = tick });
        }

        return (tempos, signatures, [.. anchors], leftOut);
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}

// <summary>What the time-signature commands share.</summary>
internal static class SignatureEdits
{
    /// <summary>Throws when <paramref name="marker"/> isn't a time signature the editor places.</summary>
    public static void Check(TickList<TimeSignatureMarker> signatures, TimeSignatureMarker marker)
    {
        signatures.Check(marker);
        if (marker.Numerator > AddTimeSignature.MaxNumerator)
            throw new ArgumentException(FormattableString.Invariant($"A time signature has 1 to {AddTimeSignature.MaxNumerator} beats, not {marker.Numerator}."), nameof(marker));
    }

    /// <summary>The index of <paramref name="marker"/> in <paramref name="signatures"/>.</summary>
    /// <exception cref="InvalidOperationException">There's no such marker.</exception>
    public static int IndexOf(TickList<TimeSignatureMarker> signatures, TimeSignatureMarker marker)
    {
        int index = signatures.IndexOf(marker);
        if (index < 0)
            throw new InvalidOperationException(FormattableString.Invariant($"There's no {marker.Numerator}/{marker.Denominator} time signature at tick {marker.Tick}."));
        return index;
    }
}
