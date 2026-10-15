using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// A tempo-map edit worked out against the anchors (charter-spec C4 item 2): the command that makes it, or why it can't
/// be made.
/// </summary>
/// <param name="Command">The command, or null when the edit is refused.</param>
/// <param name="Refusal">Why it's refused, in words; null when it isn't.</param>
/// <param name="Blocking">The anchor the edit can't keep, when that's why it's refused: it can be unanchored instead.</param>
/// <param name="OneBeatBefore">
/// The tick one beat before <paramref name="Blocking"/>, where a marker would let the edit keep it; null when there's no
/// room for one.
/// </param>
public sealed record TempoEdit(IChartCommand? Command, string? Refusal = null, TempoAnchor? Blocking = null, long? OneBeatBefore = null)
{
    public bool IsRefused => Command is null;

    internal static TempoEdit Refused(string why, TempoAnchor? blocking = null, long? oneBeatBefore = null) => new(null, why, blocking, oneBeatBefore);
}

/// <summary>What to do about an anchor an edit can't keep (charter-spec C4 item 2).</summary>
public enum BlockedAnchor
{
    /// <summary>Refuse the edit.</summary>
    Refuse,

    /// <summary>Unanchor the marker, and make the edit.</summary>
    Unanchor,

    /// <summary>Add a tempo marker one beat before the anchor, which takes up the change, and make the edit.</summary>
    AddMarkerBefore,
}

/// <summary>
/// The editor's tempo-map edits (charter-spec C4): each is worked out as the whole tempo map it leaves, so it's made and
/// undone as one <see cref="ChangeTempoMap"/>, keeping either every item's tick or its time.
/// </summary>
/// <remarks>
/// <para><b>Anchors stay put.</b> An edit that moves a later anchored marker in time is made up for by the last tempo
/// segment before that anchor, which must start after the edited marker: its tempo is worked out again so the anchor
/// stays where it is in the audio. When the edited segment runs straight into the anchor there's no such segment, and
/// the edit is refused, with the offer to unanchor the marker or to add a marker one beat before it.</para>
/// <para>Only the first anchor after the edit can move, as everything after it is timed from it.</para>
/// <para>A tick can hold more than one tempo marker in a loaded chart; the games read the last, so it's the one edited.</para>
/// </remarks>
public static class TempoMapEdits
{
    /// <summary>Sets the tempo of the marker at <paramref name="tick"/>.</summary>
    /// <param name="delay">The song's delay: audio time is chart time plus this (§4.4.1).</param>
    public static TempoEdit SetBpm(EditableChart chart, long tick, long microsecondsPerQuarter, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        var tempos = map.Tempos.AsSpan().ToArray();
        int index = LastAt(tempos, tick);
        if (index < 0)
            return TempoEdit.Refused(Invariant($"There's no tempo marker at tick {tick}."));
        if (microsecondsPerQuarter is < 1 or > EditableTempoMap.MaxMicrosecondsPerQuarter)
            return TempoEdit.Refused("That tempo is too fast or too slow for MIDI to hold.");
        if (tempos[index].MicrosecondsPerQuarter == microsecondsPerQuarter)
            return TempoEdit.Refused("The marker already has that tempo.");

        tempos[index] = tempos[index] with { MicrosecondsPerQuarter = microsecondsPerQuarter };
        return Fitted(chart, [.. tempos], [.. map.Anchors], tick, absorbFrom: tick + 1, delay, keepTimes, blocked);
    }

    /// <summary>
    /// Sets the tempo from <paramref name="tick"/> on: the marker there is changed, or one is added when there's none, as
    /// the Tempo map tab's BPM field does.
    /// </summary>
    public static TempoEdit PlaceBpm(EditableChart chart, long tick, long microsecondsPerQuarter, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        if (!map.Tempos.At(tick).IsEmpty)
            return SetBpm(chart, tick, microsecondsPerQuarter, delay, keepTimes, blocked);
        if (tick < 0)
            return TempoEdit.Refused("A tempo marker can't be before the start of the song.");
        if (microsecondsPerQuarter is < 1 or > EditableTempoMap.MaxMicrosecondsPerQuarter)
            return TempoEdit.Refused("That tempo is too fast or too slow for MIDI to hold.");

        var tempos = map.Tempos.AsSpan().ToArray().Append(new TempoChange(tick, microsecondsPerQuarter)).OrderBy(static t => t.Tick).ToList();
        return Fitted(chart, tempos, [.. map.Anchors], tick, absorbFrom: tick + 1, delay, keepTimes, blocked);
    }

    /// <summary>
    /// Moves the tempo marker and the time signature at <paramref name="tick"/>, whichever are there, to
    /// <paramref name="to"/>, together. A tempo marker moves as <see cref="MoveMarker"/> moves it: not when it's anchored,
    /// not past an anchor, and not onto another; nor does a time signature move onto another. Nothing moves off tick 0.
    /// </summary>
    public static TempoEdit MoveChange(EditableChart chart, long tick, long to, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        bool hasTempo = !map.Tempos.At(tick).IsEmpty;
        bool hasSignature = !map.TimeSignatures.At(tick).IsEmpty;
        if (!hasTempo && !hasSignature)
            return TempoEdit.Refused(Invariant($"There's no tempo or time signature change at tick {tick}."));
        if (to == tick)
            return TempoEdit.Refused("The change is already there.");
        if (tick == 0)
            return TempoEdit.Refused("The tempo and time signature the song starts with stay at the start.");
        if (to <= 0)
            return TempoEdit.Refused("A change can't be moved onto or before the start of the song: change the starting tempo or time signature instead.");
        if (hasSignature && !map.TimeSignatures.At(to).IsEmpty)
            return TempoEdit.Refused(Invariant($"There's already a time signature at tick {to}."));
        if (!hasTempo)
        {
            // A time signature moves nothing in time, so nothing's re-ticked.
            var moved = MovedSignatures(map, tick, to);
            return new TempoEdit(new ChangeTempoMap(map.Tempos.AsSpan().ToArray(), moved, map.Anchors.AsSpan().ToArray(), keepTimes: false));
        }

        if (!map.Anchors.At(tick).IsEmpty)
            return TempoEdit.Refused("An anchored marker is moved in time, not in ticks.");
        if (!map.Tempos.At(to).IsEmpty)
            return TempoEdit.Refused(Invariant($"There's already a tempo marker at tick {to}."));
        long from = Math.Min(tick, to), last = Math.Max(tick, to);
        if (map.Anchors.Any(a => a.Tick > from && a.Tick < last))
            return TempoEdit.Refused("A tempo marker can't be moved past an anchor.");

        var tempos = map.Tempos.AsSpan().ToArray().Select(t => t.Tick == tick ? t with { Tick = to } : t).OrderBy(static t => t.Tick).ToList();
        var signatures = hasSignature ? MovedSignatures(map, tick, to) : null;
        return Fitted(chart, tempos, [.. map.Anchors], from, absorbFrom: last + 1, delay, keepTimes, blocked, signatures);
    }

    /// <summary>
    /// Removes the tempo marker (and its anchor) and the time signature at <paramref name="tick"/>, together, as one step.
    /// The tempo marker's removal is taken up before the next anchor as <see cref="RemoveMarker"/>'s is.
    /// </summary>
    public static TempoEdit RemoveChange(EditableChart chart, long tick, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        bool hasTempo = !map.Tempos.At(tick).IsEmpty;
        bool hasSignature = !map.TimeSignatures.At(tick).IsEmpty;
        if (!hasTempo)
            return hasSignature ? RemoveSignature(chart, tick) : TempoEdit.Refused(Invariant($"There's no tempo or time signature change at tick {tick}."));
        if (!hasSignature)
            return RemoveMarker(chart, tick, delay, keepTimes, blocked);
        if (tick == 0)
            return TempoEdit.Refused("The tempo and time signature the song starts with can be changed but not removed.");

        var tempos = map.Tempos.Where(t => t.Tick != tick).ToList();
        var anchors = map.Anchors.Where(a => a.Tick != tick).ToList();
        var signatures = map.TimeSignatures.Where(s => s.Tick != tick).ToList();
        return Fitted(chart, tempos, anchors, tick, absorbFrom: tick + 1, delay, keepTimes, blocked, signatures);
    }

    /// <summary>
    /// Sets the time signature from <paramref name="tick"/> on: the one there is changed, or one is added when there's
    /// none (charter-spec C4 item 1). A time signature moves nothing in time.
    /// </summary>
    public static TempoEdit PlaceSignature(EditableChart chart, long tick, int numerator, int denominator)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (tick < 0)
            return TempoEdit.Refused("A time signature can't be before the start of the song.");
        if (numerator is < 1 or > AddTimeSignature.MaxNumerator)
            return TempoEdit.Refused(Invariant($"A time signature has 1 to {AddTimeSignature.MaxNumerator} beats."));
        if (denominator is < 1 or > 64 || (denominator & (denominator - 1)) != 0)
            return TempoEdit.Refused("A time signature's beat is a whole, half, quarter, eighth, 16th, 32nd or 64th note.");

        var at = chart.TempoMap.TimeSignatures.At(tick);
        if (at.IsEmpty)
            return new TempoEdit(new AddTimeSignature(new TimeSignatureMarker(tick, numerator, denominator)));
        if (at[^1].Numerator == numerator && at[^1].Denominator == denominator)
            return TempoEdit.Refused(Invariant($"The time signature there is already {numerator}/{denominator}."));
        return new TempoEdit(new SetTimeSignature(at[^1], numerator, denominator));
    }

    /// <summary>
    /// Removes the time signature at <paramref name="tick"/>, every one there when a loaded chart has more than one; the
    /// signature before it runs on in its place. The one the song starts with can be changed but not removed.
    /// </summary>
    public static TempoEdit RemoveSignature(EditableChart chart, long tick)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var at = chart.TempoMap.TimeSignatures.At(tick);
        if (at.IsEmpty)
            return TempoEdit.Refused(Invariant($"There's no time signature at tick {tick}."));
        if (tick == 0)
            return TempoEdit.Refused("The time signature the song starts with can be changed but not removed.");

        // The last first, so each is found where it was.
        var removes = new List<IChartCommand>(at.Length);
        for (int i = at.Length - 1; i >= 0; i--)
            removes.Add(new RemoveTimeSignature(at[i]));
        return new TempoEdit(removes.Count == 1 ? removes[0] : new CompositeCommand(removes));
    }

    /// <summary>The time signatures with those at <paramref name="tick"/> moved to <paramref name="to"/>, in order.</summary>
    private static List<TimeSignatureMarker> MovedSignatures(EditableTempoMap map, long tick, long to) =>
        [.. map.TimeSignatures.AsSpan().ToArray().Select(s => s.Tick == tick ? s with { Tick = to } : s).OrderBy(static s => s.Tick)];

    /// <summary>Adds a tempo marker at <paramref name="tick"/> with the tempo in force there, which moves nothing.</summary>
    public static TempoEdit AddMarker(EditableChart chart, long tick)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (!chart.TempoMap.Tempos.At(tick).IsEmpty)
            return TempoEdit.Refused(Invariant($"There's already a tempo marker at tick {tick}."));
        return new TempoEdit(new AddTempo(new TempoChange(tick, TempoAt(chart.TempoMap, tick))));
    }

    /// <summary>
    /// Removes the tempo marker at <paramref name="tick"/>, and its anchor; the segment before it runs on in its place. The
    /// marker the song starts with can't be removed.
    /// </summary>
    public static TempoEdit RemoveMarker(EditableChart chart, long tick, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        var tempos = map.Tempos.AsSpan().ToArray();
        if (LastAt(tempos, tick) < 0)
            return TempoEdit.Refused(Invariant($"There's no tempo marker at tick {tick}."));
        if (tick == 0)
            return TempoEdit.Refused("The tempo the song starts with can be changed but not removed.");

        var left = tempos.Where(t => t.Tick != tick).ToList();
        var anchors = map.Anchors.Where(a => a.Tick != tick).ToList();
        return Fitted(chart, left, anchors, tick, absorbFrom: tick + 1, delay, keepTimes, blocked);
    }

    /// <summary>
    /// Moves the unanchored tempo marker at <paramref name="tick"/> to <paramref name="to"/>, keeping its tempo. It can't
    /// move past an anchor, onto another marker, or off tick 0.
    /// </summary>
    public static TempoEdit MoveMarker(EditableChart chart, long tick, long to, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        var tempos = map.Tempos.AsSpan().ToArray();
        int index = LastAt(tempos, tick);
        if (index < 0)
            return TempoEdit.Refused(Invariant($"There's no tempo marker at tick {tick}."));
        if (to == tick)
            return TempoEdit.Refused("The marker is already there.");
        if (tick == 0 || to <= 0)
            return TempoEdit.Refused("The tempo the song starts with stays at the start.");
        if (!map.Anchors.At(tick).IsEmpty)
            return TempoEdit.Refused("An anchored marker is moved in time, not in ticks.");
        if (!map.Tempos.At(to).IsEmpty)
            return TempoEdit.Refused(Invariant($"There's already a tempo marker at tick {to}."));
        long from = Math.Min(tick, to), last = Math.Max(tick, to);
        if (map.Anchors.Any(a => a.Tick > from && a.Tick < last))
            return TempoEdit.Refused("A tempo marker can't be moved past an anchor.");

        var moved = tempos.Where(t => t.Tick != tick).Append(tempos[index] with { Tick = to }).OrderBy(static t => t.Tick).ToList();
        return Fitted(chart, moved, [.. map.Anchors], from, absorbFrom: last + 1, delay, keepTimes, blocked);
    }

    /// <summary>
    /// Drags the anchored tempo marker at <paramref name="tick"/> to <paramref name="audioSeconds"/> in the audio (charter-spec
    /// C4 item 2), fitting the tempo map to a recording beat by beat. The segment before it takes the change, so the marker
    /// lands there; the segments after it make up for it, so the next anchor stays put.
    /// </summary>
    public static TempoEdit DragAnchored(EditableChart chart, long tick, double audioSeconds, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        var tempos = map.Tempos.AsSpan().ToArray();
        if (LastAt(tempos, tick) < 0)
            return TempoEdit.Refused(Invariant($"There's no tempo marker at tick {tick}."));
        if (map.Anchors.At(tick).IsEmpty)
            return TempoEdit.Refused("Only an anchored marker is dragged in time.");
        if (tick == 0)
            return TempoEdit.Refused("The marker the song starts with is where the audio's delay puts it.");

        // The segment before the marker, which must start at or after the anchor before it.
        int before = LastBefore(tempos, tick);
        long previousAnchor = map.Anchors.Where(a => a.Tick < tick).Select(static a => a.Tick).DefaultIfEmpty(0).Max();
        if (before < 0 || tempos[before].Tick < previousAnchor)
            return TempoEdit.Refused("There's no tempo segment between this marker and the anchor before it to take the change.");

        var current = map.ToTempoMap();
        double segmentStart = current.SecondsAt(tempos[before].Tick);
        double target = audioSeconds - delay.TotalSeconds;
        long beforeTempo = MicrosecondsFor(target - segmentStart, tick - tempos[before].Tick, map.Resolution);
        if (beforeTempo is < 1 or > EditableTempoMap.MaxMicrosecondsPerQuarter)
            return TempoEdit.Refused("The marker can't go there: the segment before it would be too fast or too slow.");

        tempos[before] = tempos[before] with { MicrosecondsPerQuarter = beforeTempo };
        var anchors = map.Anchors.Select(a => a.Tick == tick ? a with { AudioMicroseconds = Microseconds(audioSeconds) } : a).ToList();
        return Fitted(chart, [.. tempos], anchors, tick, absorbFrom: tick, delay, keepTimes, blocked);
    }

    /// <summary>Anchors the marker at <paramref name="tick"/> where it is in the audio now, or unanchors it.</summary>
    public static TempoEdit SetAnchored(EditableChart chart, long tick, bool anchored, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        if (map.Tempos.At(tick).IsEmpty)
            return TempoEdit.Refused(Invariant($"There's no tempo marker at tick {tick}."));
        bool isAnchored = !map.Anchors.At(tick).IsEmpty;
        if (isAnchored == anchored)
            return TempoEdit.Refused(anchored ? "The marker is already anchored." : "The marker isn't anchored.");
        return anchored
            ? new TempoEdit(new SetAnchor(tick, Microseconds(map.ToTempoMap().SecondsAt(tick) + delay.TotalSeconds)))
            : new TempoEdit(new ClearAnchor(tick));
    }

    /// <summary>
    /// Sets the tempo from <paramref name="fromTick"/> so that <paramref name="beats"/> beats last as long as the span to
    /// <paramref name="toTick"/> lasts now (charter-spec C4 item 6). A marker is added at <paramref name="fromTick"/> when
    /// there's none.
    /// </summary>
    public static TempoEdit BpmFromRange(EditableChart chart, long fromTick, long toTick, double beats, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (toTick <= fromTick)
            return TempoEdit.Refused("Select a span of the song first.");
        if (!(beats > 0) || double.IsInfinity(beats))
            return TempoEdit.Refused("A span lasts some beats.");

        var map = chart.TempoMap;
        var current = map.ToTempoMap();
        double seconds = current.SecondsAt(toTick) - current.SecondsAt(fromTick);
        long tempo = (long)Math.Round(seconds * 1_000_000 / beats, MidpointRounding.AwayFromZero);
        if (tempo is < 1 or > EditableTempoMap.MaxMicrosecondsPerQuarter)
            return TempoEdit.Refused("That would be too fast or too slow for MIDI to hold.");

        var tempos = map.Tempos.AsSpan().ToArray().ToList();
        int index = LastAt([.. tempos], fromTick);
        if (index >= 0)
        {
            if (tempos[index].MicrosecondsPerQuarter == tempo)
                return TempoEdit.Refused("The span already has that tempo.");
            tempos[index] = tempos[index] with { MicrosecondsPerQuarter = tempo };
        }
        else
        {
            tempos.Add(new TempoChange(fromTick, tempo));
            tempos.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        }

        return Fitted(chart, tempos, [.. map.Anchors], fromTick, absorbFrom: fromTick + 1, delay, keepTimes, blocked);
    }

    /// <summary>
    /// Starts measure 2 on the downbeat at <paramref name="downbeatAudio"/> seconds of audio (charter-spec C14 item 5,
    /// lead-in): the song's first tempo is set so measure 1 lasts until then, and the delay is left alone. Measure 1's other
    /// unanchored markers go, measure 2 keeps the tempo that was in force there, and a later anchor stays where it is.
    /// </summary>
    /// <remarks>
    /// It's refused when the downbeat isn't after the chart's start (Delay is the way then), when measure 1 would be faster
    /// than 1000 BPM or slower than 10, or when an anchor inside measure 1 would move. An anchor on measure 2's line is
    /// blocked, with the offer to unanchor it, as any tempo edit that can't keep one is.
    /// </remarks>
    public static TempoEdit LeadIn(EditableChart chart, double downbeatAudio, TimeSpan delay, bool keepTimes, BlockedAnchor blocked = BlockedAnchor.Refuse)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        int resolution = map.Resolution;
        double seconds = downbeatAudio - delay.TotalSeconds;
        if (seconds <= 0.001)
            return TempoEdit.Refused("The downbeat is at or before the chart's start, so measure 1 can't lead in to it: use Delay to start the chart on it instead.");

        var current = map.ToTempoMap();
        var first = current.TimeSignatures[0];
        long measure2 = Math.Max(1, first.Numerator * resolution * 4L / first.Denominator);
        long tempo = MicrosecondsFor(seconds, measure2, resolution);
        double bpm = tempo > 0 ? 60_000_000.0 / tempo : 0;
        if (bpm is < NewChartSettings.MinBpm or > NewChartSettings.MaxBpm)
            return TempoEdit.Refused(Invariant($"Measure 1 would have to be {bpm:0.###} BPM to lead in to the downbeat: use Delay to start the chart on it instead."));
        if (map.Anchors.Any(a => a.Tick > 0 && a.Tick < measure2))
            return TempoEdit.Refused("An anchored marker inside measure 1 would move: unanchor it first.");

        long measure2Tempo = TempoAt(current, measure2);
        var tempos = map.Tempos.AsSpan().ToArray()
            .Where(t => t.Tick == 0 || t.Tick >= measure2)
            .Select(t => t.Tick == 0 ? t with { MicrosecondsPerQuarter = tempo } : t)
            .ToList();
        if (tempos.All(t => t.Tick != measure2))
            tempos.Add(new TempoChange(measure2, measure2Tempo));
        tempos.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        return Fitted(chart, tempos, [.. map.Anchors], 0, absorbFrom: 1, delay, keepTimes, blocked);
    }

    /// <summary>The tick of the segment in force at <paramref name="tick"/>: the last tempo marker at or before it.</summary>
    public static long SegmentAt(EditableTempoMap map, long tick)
    {
        ArgumentNullException.ThrowIfNull(map);
        long found = 0;
        foreach (var tempo in map.Tempos)
        {
            if (tempo.Tick > tick)
                break;
            found = tempo.Tick;
        }

        return found;
    }

    /// <summary>The tempo in force at <paramref name="tick"/>, in microseconds per quarter note.</summary>
    public static long TempoAt(EditableTempoMap map, long tick)
    {
        ArgumentNullException.ThrowIfNull(map);
        return TempoAt(map.ToTempoMap(), tick);
    }

    internal static long TempoAt(TempoMap map, long tick)
    {
        var tidied = map.Tempos;
        long tempo = tidied[0].MicrosecondsPerQuarter;
        foreach (var t in tidied)
        {
            if (t.Tick > tick)
                break;
            tempo = t.MicrosecondsPerQuarter;
        }

        return tempo;
    }

    /// <summary>
    /// The edit the new <paramref name="tempos"/> and <paramref name="anchors"/> make, with the first anchor after
    /// <paramref name="edited"/> kept where it is by the last segment before it that starts at or after
    /// <paramref name="absorbFrom"/>. The time signatures are <paramref name="signatures"/>, or kept as they are when null.
    /// </summary>
    internal static TempoEdit Fitted(EditableChart chart, List<TempoChange> tempos, List<TempoAnchor> anchors, long edited, long absorbFrom, TimeSpan delay, bool keepTimes, BlockedAnchor blocked, IReadOnlyList<TimeSignatureMarker>? signatures = null)
    {
        var map = chart.TempoMap;
        signatures ??= [.. map.TimeSignatures];
        var anchor = anchors.Where(a => a.Tick > edited).OrderBy(static a => a.Tick).Cast<TempoAnchor?>().FirstOrDefault();
        if (anchor is { } kept)
        {
            long? oneBeatBefore = OneBeatBefore(map, kept.Tick, absorbFrom);
            int absorber = LastSegmentBetween(tempos, absorbFrom, kept.Tick);
            if (absorber < 0)
            {
                switch (blocked)
                {
                    case BlockedAnchor.Unanchor:
                        // The next anchor, if there's one, is the one to keep now.
                        anchors.Remove(kept);
                        return Fitted(chart, tempos, anchors, edited, absorbFrom, delay, keepTimes, BlockedAnchor.Refuse, signatures);
                    case BlockedAnchor.AddMarkerBefore when oneBeatBefore is { } at:
                        var edits = new TempoMap(map.Resolution, tempos, signatures.Select(static s => s.ToTimeSignature()));
                        tempos.Add(new TempoChange(at, TempoAt(edits, at)));
                        tempos.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
                        absorber = LastSegmentBetween(tempos, absorbFrom, kept.Tick);
                        break;
                    default:
                        return TempoEdit.Refused(
                            Invariant($"The change can't keep the anchored marker at tick {kept.Tick} where it is: no tempo segment between the change and it can take the change up."),
                            kept,
                            oneBeatBefore);
                }
            }

            // The absorber runs straight into the anchor, as it's the last segment before it.
            var unfitted = new TempoMap(map.Resolution, tempos, signatures.Select(static s => s.ToTimeSignature()));
            double start = unfitted.SecondsAt(tempos[absorber].Tick);
            double target = (kept.AudioMicroseconds / 1_000_000.0) - delay.TotalSeconds;
            long tempo = MicrosecondsFor(target - start, kept.Tick - tempos[absorber].Tick, map.Resolution);
            if (tempo is < 1 or > EditableTempoMap.MaxMicrosecondsPerQuarter)
            {
                return TempoEdit.Refused(
                    Invariant($"The change can't keep the anchored marker at tick {kept.Tick} where it is: the segment from tick {tempos[absorber].Tick} would have to be too fast or too slow."),
                    kept,
                    oneBeatBefore);
            }

            tempos[absorber] = tempos[absorber] with { MicrosecondsPerQuarter = tempo };
        }

        return new TempoEdit(new ChangeTempoMap(tempos, signatures, anchors, keepTimes));
    }

    /// <summary>The tick one beat before <paramref name="anchor"/>, when it's at or after <paramref name="from"/>.</summary>
    private static long? OneBeatBefore(EditableTempoMap map, long anchor, long from)
    {
        var signature = map.ToTempoMap().TimeSignatures.Last(s => s.Tick < anchor);
        long tick = anchor - Math.Max(1, map.Resolution * 4L / signature.Denominator);
        return tick >= from ? tick : null;
    }

    /// <summary>The index of the last marker of the last tick from <paramref name="from"/> up to (not including) <paramref name="before"/>, or -1.</summary>
    private static int LastSegmentBetween(List<TempoChange> tempos, long from, long before)
    {
        for (int i = tempos.Count - 1; i >= 0; i--)
        {
            if (tempos[i].Tick < before)
                return tempos[i].Tick >= from ? i : -1;
        }

        return -1;
    }

    /// <summary>
    /// Stretches or squeezes the timing from <paramref name="from"/> to <paramref name="to"/> so it ends at
    /// <paramref name="newTo"/> (charter-spec C15 item 4), for the double- or half-time sections imported charts often have.
    /// Everything keeps its time: the ticks inside the span are scaled and the tempos there with them, everything after it
    /// moves by the change in length, and the passthrough tracks are re-ticked to match (§4.2). Anchors move with their
    /// markers and keep their times.
    /// </summary>
    public static TempoEdit Stretch(EditableChart chart, long from, long to, long newTo)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (from < 0 || to <= from)
            return TempoEdit.Refused("Select a span to stretch or squeeze.");
        if (newTo <= from)
            return TempoEdit.Refused("A span can't be squeezed to nothing.");
        if (newTo == to)
            return TempoEdit.Refused("The span already ends there.");

        var map = chart.TempoMap;
        var move = TickMaps.Stretch(from, to, newTo);
        double scale = (double)(to - from) / (newTo - from);
        var old = map.Tempos.AsSpan().ToArray();
        var tempos = new List<TempoChange>(old.Length + 2);
        foreach (var tempo in old)
        {
            long us = tempo.Tick >= from && tempo.Tick < to ? (long)Math.Round(tempo.MicrosecondsPerQuarter * scale) : tempo.MicrosecondsPerQuarter;
            tempos.Add(new TempoChange(move(tempo.Tick), us));
        }

        // The span starts and ends on markers of its own, so the tempos before and after it are kept as they were.
        if (!old.Any(t => t.Tick == from))
            tempos.Add(new TempoChange(from, (long)Math.Round(TempoAt(old, from) * scale)));
        if (!old.Any(t => t.Tick == to))
            tempos.Add(new TempoChange(newTo, TempoAt(old, to)));
        if (tempos.Any(static t => t.MicrosecondsPerQuarter is < 1 or > EditableTempoMap.MaxMicrosecondsPerQuarter))
            return TempoEdit.Refused("That would make a tempo too fast or too slow for MIDI to hold.");
        tempos = [.. tempos.OrderBy(static t => t.Tick)];

        var signatures = map.TimeSignatures.AsSpan().ToArray().Select(s => s with { Tick = move(s.Tick) }).ToList();
        var anchors = map.Anchors.AsSpan().ToArray().Select(a => a with { Tick = move(a.Tick) }).ToList();
        try
        {
            return new TempoEdit(new ChangeTempoMap(tempos, signatures, anchors, move));
        }
        catch (ArgumentException e)
        {
            return TempoEdit.Refused(e.Message);
        }
    }

    // The tempo in force at a tick: the last marker at or before it.
    private static long TempoAt(TempoChange[] tempos, long tick)
    {
        long tempo = TempoMap.DefaultMicrosecondsPerQuarter;
        foreach (var t in tempos)
        {
            if (t.Tick > tick)
                break;
            tempo = t.MicrosecondsPerQuarter;
        }

        return tempo;
    }

    private static int LastAt(TempoChange[] tempos, long tick)
    {
        for (int i = tempos.Length - 1; i >= 0; i--)
        {
            if (tempos[i].Tick == tick)
                return i;
        }

        return -1;
    }

    // The last marker of the last tick before tick.
    private static int LastBefore(TempoChange[] tempos, long tick)
    {
        for (int i = tempos.Length - 1; i >= 0; i--)
        {
            if (tempos[i].Tick < tick)
                return i;
        }

        return -1;
    }

    /// <summary>The tempo that makes <paramref name="ticks"/> last <paramref name="seconds"/>, in whole microseconds per quarter note.</summary>
    internal static long MicrosecondsFor(double seconds, long ticks, int resolution)
    {
        if (ticks <= 0 || !(seconds > 0))
            return 0;
        double tempo = Math.Round(seconds * 1_000_000 * resolution / ticks, MidpointRounding.AwayFromZero);
        return tempo > long.MaxValue ? long.MaxValue : (long)tempo;
    }

    internal static long Microseconds(double seconds) => (long)Math.Round(seconds * 1_000_000, MidpointRounding.AwayFromZero);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
