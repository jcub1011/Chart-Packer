namespace ChartPacker.Audio.Playback;

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

    /// <summary>Queues <paramref name="stereo"/> (interleaved left, right) to play after what's already queued.</summary>
    void Write(ReadOnlySpan<float> stereo);

    /// <summary>Drops everything queued.</summary>
    void Clear();

    /// <summary>Stops draining the queue, keeping what's in it.</summary>
    void Pause();

    /// <summary>Starts, or carries on, playing the queue.</summary>
    void Resume();
}

/// <summary>The audio device couldn't be opened or used.</summary>
public sealed class AudioDeviceException(string message) : Exception(message);
