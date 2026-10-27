namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// The loop region as the tools see it (charter-spec C10 item 2): a stretch of the song that's played over and over. Its
/// times are seconds of audio, as playback has them; the session is given one so the Loop tool can draw, move and resize
/// it, and every lane and the timeline can mark it.
/// </summary>
public interface ILoopSpan
{
    /// <summary>The region's start and end, in seconds of audio; null with no region.</summary>
    (double Start, double End)? Span { get; }

    /// <summary>
    /// Puts the region where a gesture drags it, without turning looping on or moving what's playing: the gesture's
    /// preview.
    /// </summary>
    void DragTo(double start, double end);

    /// <summary>
    /// Sets the region as a gesture ends, leaving whether it's being repeated as it was: drawing, moving or resizing it
    /// doesn't start the song repeating it (charter-spec C10 item 2), which the Loop button and a tap of L do.
    /// </summary>
    void Define(double start, double end);

    /// <summary>Takes the region away, and stops looping.</summary>
    void Clear();
}

/// <summary>
/// Which part of the loop region under the pointer is grabbed: an end, to resize it, or its body, to move it.
/// </summary>
public enum LoopGrip
{
    Start,
    End,
    Body,
}
