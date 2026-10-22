namespace ChartShared.Audio.Playback;

/// <summary>
/// Where mixed audio goes: a queue of interleaved stereo float frames that the audio device drains
/// at <see cref="SampleRate"/>. Every member can be called from any thread.
/// </summary>
public interface IAudioOutput : IDisposable
{
    int SampleRate { get; }

    /// <summary>How many frames are queued and not yet played.</summary>
    int QueuedFrames { get; }

    /// <summary>The playback volume, from 0 (silent) to 1 (as mixed).</summary>
    float Volume { get; set; }

    /// <summary>
    /// How fast the queue plays, from 0.01 to 100; 1 plays it as written. The pitch moves with it.
    /// <see cref="QueuedFrames"/> still counts frames as they were written.
    /// </summary>
    float Speed { get; set; }

    /// <summary>Queues <paramref name="stereo"/> (interleaved left, right) to play after what's already queued.</summary>
    void Write(ReadOnlySpan<float> stereo);

    /// <summary>Drops everything queued.</summary>
    void Clear();

    /// <summary>Stops draining the queue, keeping what's in it.</summary>
    void Pause();

    /// <summary>Starts, or carries on, playing the queue.</summary>
    void Resume();

    /// <summary>The device the queue is playing on, with the latency read for it.</summary>
    AudioDevice Device { get; }

    /// <summary>
    /// Raised with the new device when the queue moves to another device, as it does when the OS's
    /// default output changes, or when the latency read for it changes. It may be raised on any thread.
    /// </summary>
    event Action<AudioDevice>? DeviceChanged;
}

/// <summary>The audio device couldn't be opened or used.</summary>
public sealed class AudioDeviceException(string message) : Exception(message);
