namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// Where the editor is in the song (charter-spec C3, C10 item 6): the cursor, in audio time (§4.4.1), shared by every
/// lane and both views. The cursor is the strike line: each lane draws it <see cref="StrikeFraction"/> of the way from
/// its strike end (the bottom of a highway, the left of a timeline), and the song moves past it.
/// </summary>
/// <remarks>
/// <para>Scrolling a lane moves the cursor, so the strike line moves through the song as the user scrolls, and while
/// the song plays the cursor is the playhead. A move by the user (<see cref="MoveCursor"/>, <see cref="ScrollBy"/>)
/// raises <see cref="Moved"/>, so playback seeks there; following the playhead (<see cref="Follow"/>) doesn't.</para>
/// <para>How much of the song a lane shows is its own look-ahead: the seconds from the strike line to the far end
/// (<see cref="SpanOf"/>). Switching a lane between the views keeps it, and the cursor (AC C3.1).</para>
/// </remarks>
public sealed class EditorViewport
{
    /// <summary>How far from the strike end the strike line is: a quarter of the way (charter-spec C10 item 6).</summary>
    public const double StrikeFraction = 0.25;

    /// <summary>The shortest look-ahead a lane can have, in seconds.</summary>
    public const double MinLookAhead = 0.5;

    /// <summary>The longest look-ahead a lane can have, in seconds.</summary>
    public const double MaxLookAhead = 60;

    /// <summary>The step look-aheads move in, in seconds: half a second.</summary>
    public const double LookAheadStep = 0.5;

    /// <summary>Raised when the cursor moves, by the user or with the playhead.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the user moves the cursor (a key or scrolling), for playback to seek there.</summary>
    public event EventHandler? Moved;

    /// <summary>The cursor, in audio seconds: the strike line, where keys place notes and playback starts, and the playhead while playing.</summary>
    public double Cursor { get; private set; }

    /// <summary><paramref name="seconds"/> as a look-ahead a lane can have: within range, to the nearest half second.</summary>
    public static double ClampLookAhead(double seconds) =>
        double.IsFinite(seconds) ? QuantizeLookAhead(Math.Clamp(seconds, MinLookAhead, MaxLookAhead)) : MinLookAhead;

    /// <summary><paramref name="seconds"/> rounded to the nearest half second.</summary>
    public static double QuantizeLookAhead(double seconds) =>
        Math.Round(seconds / LookAheadStep, MidpointRounding.AwayFromZero) * LookAheadStep;

    /// <summary>
    /// The span of audio time a lane with <paramref name="lookAhead"/> seconds beyond its strike line shows, with the
    /// strike line on the cursor: from its strike end to its far end.
    /// </summary>
    public (double Start, double End) SpanOf(double lookAhead)
    {
        double ahead = ClampLookAhead(lookAhead);
        return (Cursor - (ahead * StrikeFraction / (1 - StrikeFraction)), Cursor + ahead);
    }

    /// <summary>Moves the cursor to <paramref name="seconds"/>, for the user: playback seeks there.</summary>
    public void MoveCursor(double seconds)
    {
        if (Cursor == seconds)
            return;
        Cursor = seconds;
        Changed?.Invoke(this, EventArgs.Empty);
        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scrolls by <paramref name="seconds"/>, later when positive: the song moves past the strike line.</summary>
    public void ScrollBy(double seconds) => MoveCursor(Cursor + seconds);

    /// <summary>Follows the playhead at <paramref name="seconds"/>, as it plays or is scrubbed, without seeking.</summary>
    public void Follow(double seconds)
    {
        if (Cursor == seconds)
            return;
        Cursor = seconds;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
