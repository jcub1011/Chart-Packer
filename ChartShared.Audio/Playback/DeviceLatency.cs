namespace ChartShared.Audio.Playback;

/// <summary>
/// The latency the app can read for an output device (charter-spec C10 item 5, C11, issue #140): SDL's
/// device buffer, which <see cref="IAudioOutput.QueuedFrames"/> doesn't count, and what the OS says
/// its output adds (on Windows, WASAPI's stream latency and period, <see cref="WindowsAudioEndpoint"/>).
/// </summary>
/// <remarks>
/// It can't see a Bluetooth link's delay, nor the display's, so it is only what a device that hasn't
/// been calibrated uses; the calibration tool measures the rest.
/// </remarks>
public static class DeviceLatency
{
    /// <summary>The most it can be, in milliseconds; the same as <c>AudioLatencyFile.MaxMilliseconds</c>.</summary>
    public const int MaxMilliseconds = 500;

    /// <summary>
    /// The latency of a device buffer of <paramref name="sampleFrames"/> at <paramref name="deviceRate"/>, plus
    /// <paramref name="osLatency"/>, rounded to the millisecond and within 0 to <see cref="MaxMilliseconds"/>; null
    /// when neither is known.
    /// </summary>
    public static int? Milliseconds(int sampleFrames, int deviceRate, TimeSpan? osLatency)
    {
        bool hasBuffer = sampleFrames > 0 && deviceRate > 0;
        bool hasOs = osLatency is { } os && os > TimeSpan.Zero;
        if (!hasBuffer && !hasOs)
            return null;

        double ms = (hasBuffer ? sampleFrames * 1000.0 / deviceRate : 0) + (hasOs ? osLatency!.Value.TotalMilliseconds : 0);
        return (int)Math.Round(Math.Clamp(ms, 0, MaxMilliseconds));
    }
}
