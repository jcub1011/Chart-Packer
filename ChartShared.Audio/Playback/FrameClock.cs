using System.Runtime.CompilerServices;

namespace ChartShared.Audio.Playback;

/// <summary>
/// The moment the frame being drawn is for, on the screen's even beat, for every view that draws against playback in either
/// app: one <see cref="FramePacer"/> for each clock, so that everything drawn in a frame, in any window, is drawn for the
/// same moment, and each frame a frame's time after the last.
/// </summary>
/// <remarks>
/// For drawing: what times a press, a tap or a recording reads the clock itself. Read as a frame is drawn; read between
/// frames, as a pointer moves a view, it's the moment it's read (see <see cref="FramePacer"/>).
/// </remarks>
public static class FrameClock
{
    private static readonly ConditionalWeakTable<TimeProvider, FramePacer> Pacers = [];
    private static readonly Lock Gate = new();

    /// <summary>The moment the frame being drawn is for, as a timestamp of <paramref name="clock"/>.</summary>
    public static long Timestamp(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (Gate)
            return Pacers.GetValue(clock, _ => new FramePacer()).Next(clock.GetTimestamp(), clock.TimestampFrequency);
    }

    /// <summary>The time from <paramref name="start"/>, a timestamp of <paramref name="clock"/>, to the moment the frame being drawn is for.</summary>
    public static TimeSpan ElapsedSince(TimeProvider clock, long start) => clock.GetElapsedTime(start, Timestamp(clock));
}
