using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>How a lane draws the song (charter-spec C3 item 1).</summary>
public enum LaneView
{
    /// <summary>Vertical: time runs upwards, as in the games.</summary>
    Highway,

    /// <summary>Horizontal: time runs left to right, each lane a row.</summary>
    Timeline,
}

/// <summary>The lanes a part's notes are drawn in, in order across the view, and the lane drawn across all of them.</summary>
/// <param name="Slots">The note lanes (<see cref="EditableNote.Lane"/>) of each column or row, in order.</param>
/// <param name="Across">
/// The lane drawn across every column rather than in one (an open note, or the drums' kick), in a highway; null in a
/// timeline, where it has a row of its own in <paramref name="Slots"/>.
/// </param>
/// <param name="Shared">
/// The lane that shares each of the first columns or rows with <paramref name="Slots"/>' (GHL's black frets, stacked
/// with the white, charter-spec C7 item 1), which has the first half of it across the view: the left of a highway's
/// column, the top of a timeline's row. Null when no lanes share.
/// </param>
public sealed record LaneSlots(IReadOnlyList<int> Slots, int? Across, IReadOnlyList<int>? Shared = null)
{
    /// <summary>The slots of a part of <paramref name="kind"/> in <paramref name="view"/>.</summary>
    /// <param name="fiveLaneDrums">Whether drums have five pads rather than four.</param>
    public static LaneSlots Of(ChartInstrumentKind kind, LaneView view, bool fiveLaneDrums = false)
    {
        IReadOnlyList<int> lanes = kind switch
        {
            ChartInstrumentKind.FiveFret => [0, 1, 2, 3, 4],
            ChartInstrumentKind.SixFret => [0, 1, 2],
            _ => fiveLaneDrums ? [1, 2, 3, 4, 5] : [1, 2, 3, 4],
        };
        IReadOnlyList<int>? shared = kind == ChartInstrumentKind.SixFret ? [3, 4, 5] : null;
        int across = kind == ChartInstrumentKind.Drums ? 0 : ChartNote.OpenLane;
        return view == LaneView.Highway ? new LaneSlots(lanes, across, shared) : new LaneSlots([.. lanes, across], null, shared);
    }

    /// <summary>
    /// The lanes the number keys place, from 1 (charter-spec §9): each column's in order, then those sharing them, so GHL's
    /// are white 1–3, then black 1–3. Taken from a highway's slots, which leave out the lane drawn across.
    /// </summary>
    public IReadOnlyList<int> KeyLanes => Shared is null ? Slots : [.. Slots, .. Shared];

    /// <summary>The column or row of <paramref name="lane"/>, or -1 when it's drawn across them all or not at all.</summary>
    public int IndexOf(int lane)
    {
        for (int i = 0; i < Slots.Count; i++)
        {
            if (Slots[i] == lane)
                return i;
        }

        if (Shared is not null)
        {
            for (int i = 0; i < Shared.Count; i++)
            {
                if (Shared[i] == lane)
                    return i;
            }
        }

        return -1;
    }

    /// <summary>Whether <paramref name="lane"/> shares a column or row, in the first half of it.</summary>
    public bool IsShared(int lane) => Shared?.Contains(lane) == true;

    /// <summary>The lane sharing <paramref name="index"/>'s column or row, or null when none does.</summary>
    public int? SharedOf(int index) => Shared is not null && index >= 0 && index < Shared.Count ? Shared[index] : null;

    /// <summary>The other lane of <paramref name="lane"/>'s column or row, when it's shared: a GHL fret's white or black.</summary>
    public int? PartnerOf(int lane)
    {
        int index = IndexOf(lane);
        if (SharedOf(index) is not { } shared)
            return null;
        return lane == shared ? Slots[index] : shared;
    }

    /// <summary>
    /// The lane at <paramref name="fraction"/> of the way across <paramref name="index"/>'s column or row: in a shared
    /// one, its first half's lane before the middle and its own after.
    /// </summary>
    public int LaneIn(int index, double fraction) => SharedOf(index) is { } shared && fraction < 0.5 ? shared : Slots[index];
}

/// <summary>
/// Where a lane draws the song (charter-spec C3): time along one axis and the note lanes across the other, for either
/// view. Both canvases map the pointer to the chart, and the chart to the screen, through this, so the editing tools see
/// the same points whichever view the lane is in (AC C3.2).
/// </summary>
/// <param name="View">The orientation.</param>
/// <param name="TimeFrom">Where the time axis starts, in pixels: the top of a highway, the left of a timeline.</param>
/// <param name="TimeLength">How long the time axis is, in pixels.</param>
/// <param name="LaneFrom">Where the first column or row starts, in pixels: the left of a highway, the top of a timeline.</param>
/// <param name="LaneSize">How wide a column or how tall a row is, in pixels.</param>
/// <param name="Slots">The lanes in their columns or rows.</param>
/// <param name="Start">The audio time at the strike end: the bottom of a highway, the left of a timeline.</param>
/// <param name="SecondsOnScreen">How much audio time the time axis spans.</param>
public sealed record LaneProjection(LaneView View, double TimeFrom, double TimeLength, double LaneFrom, double LaneSize, LaneSlots Slots, double Start, double SecondsOnScreen)
{
    /// <summary>The audio time at the far end of the time axis.</summary>
    public double End => Start + SecondsOnScreen;

    /// <summary>Pixels per second of audio time.</summary>
    public double PixelsPerSecond => TimeLength / SecondsOnScreen;

    /// <summary>
    /// A lane laid out as given that shows <paramref name="lookAhead"/> seconds beyond its strike line, which is on
    /// <paramref name="viewport"/>'s cursor, <see cref="EditorViewport.StrikeFraction"/> of the way along.
    /// </summary>
    public static LaneProjection Of(LaneView view, double timeFrom, double timeLength, double laneFrom, double laneSize, LaneSlots slots, EditorViewport viewport, double lookAhead)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var (start, end) = viewport.SpanOf(lookAhead);
        return new LaneProjection(view, timeFrom, timeLength, laneFrom, laneSize, slots, start, end - start);
    }

    /// <summary>Where the strike line is drawn along the time axis, in pixels.</summary>
    public double StrikeAlong => AlongOf(Start + (SecondsOnScreen * EditorViewport.StrikeFraction));

    /// <summary>Where along the time axis <paramref name="seconds"/> of audio time is drawn, in pixels.</summary>
    public double AlongOf(double seconds)
    {
        double fraction = (seconds - Start) / SecondsOnScreen;
        return View == LaneView.Highway ? TimeFrom + (TimeLength * (1 - fraction)) : TimeFrom + (TimeLength * fraction);
    }

    /// <summary>The audio time drawn at <paramref name="along"/> pixels along the time axis.</summary>
    public double SecondsAt(double along)
    {
        double fraction = (along - TimeFrom) / TimeLength;
        return Start + (SecondsOnScreen * (View == LaneView.Highway ? 1 - fraction : fraction));
    }

    /// <summary>Where the middle of the column or row at <paramref name="index"/> is, across the lanes, in pixels.</summary>
    public double AcrossOf(int index) => LaneFrom + ((index + 0.5) * LaneSize);

    /// <summary>
    /// The lane at <paramref name="across"/> pixels, or null when it's outside the columns or rows: in a shared one, the
    /// lane of the half it's in (<see cref="LaneSlots.LaneIn"/>).
    /// </summary>
    public int? LaneAt(double across)
    {
        double position = (across - LaneFrom) / LaneSize;
        double index = Math.Floor(position);
        return index >= 0 && index < Slots.Slots.Count ? Slots.LaneIn((int)index, position - index) : null;
    }

    /// <summary>How many ticks <paramref name="pixels"/> spans at <paramref name="tick"/>, for hit-testing.</summary>
    public double TicksOf(double pixels, double tick, TempoMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        double seconds = pixels / PixelsPerSecond;
        double at = map.SecondsAt(tick);
        return Math.Abs(map.TickAt(at + seconds) - tick);
    }
}
