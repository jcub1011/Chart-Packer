namespace ChartShared.Audio.Playback;

/// <summary>
/// The output device audio is playing on: its name, and a stable ID where the platform gives one
/// (on Windows, the endpoint ID). The calibrated latency is stored under both (C11).
/// </summary>
/// <param name="DetectedLatencyMs">
/// The device's latency as far as the app can read it (<see cref="DeviceLatency"/>), in milliseconds,
/// or null when it couldn't be read. Used for a device that hasn't been calibrated (C11, issue #140).
/// </param>
public sealed record AudioDevice(string Name, string? Id, int? DetectedLatencyMs = null)
{
    /// <summary>Stands for a device that couldn't be found out.</summary>
    public static AudioDevice Unknown { get; } = new("", null);
}
