namespace ChartPacker.Audio.Playback;

/// <summary>
/// Where to draw what's drawn against the audio (charter-spec C10 item 5, §4.4.1): the calibrated latency earlier than
/// the player's position, so it's seen as it's heard.
/// </summary>
/// <remarks>
/// <see cref="StemPlayer.Position"/> already allows for what's waiting in SDL's queue; the latency (C11) is everything
/// after it, the device's buffer and the output's own delay. It's in real time, so it's scaled by the speed to give
/// song time: at 50%, 40 ms of latency is 20 ms of song. The audio itself never moves.
/// </remarks>
public static class LatencyCompensation
{
    /// <summary>The song time to draw at: <paramref name="position"/> less <paramref name="latencyMilliseconds"/> × <paramref name="speed"/>.</summary>
    /// <param name="position">Where the audio is, from <see cref="StemPlayer.SmoothPosition"/> or <see cref="StemPlayer.Position"/>.</param>
    /// <param name="latencyMilliseconds">The calibrated latency, in real time.</param>
    /// <param name="speed">The playback speed, where 1 plays the song as it is.</param>
    public static TimeSpan Drawn(TimeSpan position, int latencyMilliseconds, double speed) =>
        position - TimeSpan.FromMilliseconds(latencyMilliseconds * (double.IsFinite(speed) && speed > 0 ? speed : 1));
}
