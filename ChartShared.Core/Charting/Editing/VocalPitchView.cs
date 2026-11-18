using System.Collections.Concurrent;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// The window of pitches a vocal lane's piano roll shows: a run of semitones from <see cref="Top"/> down, as many as
/// <see cref="Span"/>. The lane can sing 36 to 84 (<see cref="VocalTrackContent"/>), and shows all of it until the user
/// scrolls or zooms the keyboard. Every change gives a new view kept within that range, so the canvas and the keyboard
/// gestures never have to check it.
/// </summary>
/// <param name="Top">The highest pitch shown, at the top row.</param>
/// <param name="Span">How many pitches are shown, one a row.</param>
public readonly record struct VocalPitchView(int Top, int Span)
{
    /// <summary>The fewest pitches a view can narrow to: an octave.</summary>
    public const int MinSpan = 12;

    /// <summary>How many pitches one zoom step adds or takes away.</summary>
    public const int ZoomStep = 4;

    private const int Lowest = VocalTrackContent.LowestPitch;
    private const int Highest = VocalTrackContent.HighestPitch;

    // The slots of each view, made once: every layer of the lane asks for them every frame it's drawn.
    private static readonly ConcurrentDictionary<VocalPitchView, LaneSlots> Made = new();

    /// <summary>The view of every pitch a part can sing.</summary>
    public static VocalPitchView Full => new(Highest, Highest - Lowest + 1);

    /// <summary>The view of <paramref name="top"/> and <paramref name="span"/> brought into range: no narrower than <see cref="MinSpan"/>, nor wider or higher or lower than the lane sings.</summary>
    public static VocalPitchView Of(int top, int span)
    {
        span = Math.Clamp(span, MinSpan, Highest - Lowest + 1);
        top = Math.Clamp(top, Lowest + span - 1, Highest);
        return new VocalPitchView(top, span);
    }

    /// <summary>The view saved as <paramref name="top"/> and <paramref name="span"/>, or the full one when either is missing.</summary>
    public static VocalPitchView FromSaved(int? top, int? span) =>
        top is { } t && span is { } s ? Of(t, s) : Full;

    /// <summary>The lowest pitch shown, at the bottom row.</summary>
    public int Bottom => Top - Span + 1;

    /// <summary>Whether the view shows every pitch a part can sing.</summary>
    public bool IsFull => Span >= Highest - Lowest + 1;

    /// <summary>The pitches shown, from the highest down, as a lane's rows.</summary>
    public LaneSlots Slots => Made.GetOrAdd(Of(Top, Span), static view => new LaneSlots([.. Enumerable.Range(0, view.Span).Select(i => view.Top - i)], null));

    /// <summary>The row of <paramref name="pitch"/> from the top, which is negative or past the last row when it's outside the view.</summary>
    public int RowOf(int pitch) => Top - pitch;

    /// <summary>The pitch at <paramref name="row"/> from the top, which may be outside the view.</summary>
    public int PitchAt(int row) => Top - row;

    /// <summary>Whether <paramref name="pitch"/> is a row of the view.</summary>
    public bool Contains(int pitch) => pitch >= Bottom && pitch <= Top;

    /// <summary>This view moved <paramref name="semitones"/> up, or down when negative, stopping at the range's ends.</summary>
    public VocalPitchView Scrolled(int semitones) => Of(Top + semitones, Span);

    /// <summary>
    /// This view zoomed in <paramref name="steps"/> steps, or out when negative: each takes <see cref="ZoomStep"/> pitches off
    /// the span or adds them. <paramref name="anchor"/>, the pitch under the pointer, stays the same fraction of the way
    /// down, so the zoom closes in on it.
    /// </summary>
    public VocalPitchView Zoomed(int steps, int anchor)
    {
        int span = Math.Clamp(Span - (steps * ZoomStep), MinSpan, Highest - Lowest + 1);
        if (span == Span)
            return this;

        anchor = Math.Clamp(anchor, Bottom, Top);
        double fraction = (Top - anchor + 0.5) / Span;
        int top = (int)Math.Round(anchor + (fraction * span) - 0.5);
        return Of(top, span);
    }

    /// <summary>
    /// This view moved the least that shows <paramref name="pitch"/> with a row or so to spare, or as it is when it's
    /// already shown; where a click on an indicator of notes out of view goes.
    /// </summary>
    public VocalPitchView Revealing(int pitch)
    {
        int margin = Math.Min(2, Span / 4);
        if (pitch > Top - margin)
            return Of(pitch + margin, Span);
        if (pitch < Bottom + margin)
            return Of(pitch - margin + Span - 1, Span);
        return this;
    }

    /// <summary>
    /// How many of <paramref name="pitches"/> are above the view and how many below it, for the indicators that say there
    /// are notes to scroll to.
    /// </summary>
    public (int Above, int Below) Hidden(IEnumerable<int> pitches)
    {
        int above = 0, below = 0;
        foreach (int pitch in pitches)
        {
            if (pitch > Top)
                above++;
            else if (pitch < Bottom)
                below++;
        }

        return (above, below);
    }
}
