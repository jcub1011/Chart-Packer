namespace ChartPacker.Audio.Playback;

/// <summary>
/// The output device audio is playing on: its name, and a stable ID where the platform gives one
/// (on Windows, the endpoint ID). The calibrated latency is stored under both (C11).
/// </summary>
public sealed record AudioDevice(string Name, string? Id)
{
    /// <summary>Stands for a device that couldn't be found out.</summary>
    public static AudioDevice Unknown { get; } = new("", null);
}
