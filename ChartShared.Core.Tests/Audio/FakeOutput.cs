using ChartShared.Audio.Playback;

namespace ChartShared.Core.Tests.Audio;

/// <summary>Is given what's written, from the frame it starts at.</summary>
internal delegate void WriteHandler(long firstFrame, ReadOnlySpan<float> stereo);

/// <summary>An output whose queue only drains when <see cref="Drain"/> says the device played it.</summary>
internal sealed class FakeOutput(int sampleRate = 48000) : IAudioOutput
{
    private readonly Lock _lock = new();
    private int _queued;
    private int _largestWrite;
    private float? _firstAfterClear;
    private readonly List<float> _writtenLeft = [];
    private long _writtenFrames;

    public int SampleRate => sampleRate;

    public bool FailWrites { get; init; }

    /// <summary>Whether what's written is kept for <see cref="WrittenLeft"/>; off for tests that play minutes of it.</summary>
    public bool KeepsWritten { get; init; } = true;

    /// <summary>Given everything written, as it's written.</summary>
    public WriteHandler? Written { get; init; }

    public bool IsPlaying { get; private set; }

    public bool IsDisposed { get; private set; }

    public int Clears { get; private set; }

    public float Volume { get; set; } = 1;

    public float Speed { get; set; } = 1;

    /// <summary>The most frames written at once.</summary>
    public int LargestWrite
    {
        get
        {
            lock (_lock)
                return _largestWrite;
        }
    }

    /// <summary>The first left sample written since the last <see cref="Clear"/>, or since the start.</summary>
    public float? FirstSampleAfterLastClear
    {
        get
        {
            lock (_lock)
                return _firstAfterClear;
        }
    }

    public int QueuedFrames
    {
        get
        {
            lock (_lock)
                return _queued;
        }
    }

    /// <summary>How many frames have been written since the start.</summary>
    public long WrittenFrames
    {
        get
        {
            lock (_lock)
                return _writtenFrames;
        }
    }

    /// <summary>The left channel of everything written since the start.</summary>
    public float[] WrittenLeft()
    {
        lock (_lock)
            return [.. _writtenLeft];
    }

    public void Write(ReadOnlySpan<float> stereo)
    {
        if (FailWrites)
            throw new AudioDeviceException("The device went away.");

        lock (_lock)
        {
            if (_queued == 0 && _firstAfterClear is null)
                _firstAfterClear = stereo[0];
            _queued += stereo.Length / 2;
            _largestWrite = Math.Max(_largestWrite, stereo.Length / 2);
            Written?.Invoke(_writtenFrames, stereo);
            _writtenFrames += stereo.Length / 2;
            if (KeepsWritten)
            {
                for (int i = 0; i < stereo.Length; i += 2)
                    _writtenLeft.Add(stereo[i]);
            }
        }
    }

    public void Drain(int frames)
    {
        lock (_lock)
            _queued = Math.Max(0, _queued - frames);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queued = 0;
            _firstAfterClear = null;
            Clears++;
        }
    }

    public void Pause() => IsPlaying = false;

    public void Resume() => IsPlaying = true;

    public void Dispose() => IsDisposed = true;

    public AudioDevice Device { get; private set; } = new("Speakers", "{speakers}");

    public event Action<AudioDevice>? DeviceChanged;

    /// <summary>Moves the queue to <paramref name="device"/>, as the OS does when its default output changes.</summary>
    public void MoveTo(AudioDevice device)
    {
        Device = device;
        DeviceChanged?.Invoke(device);
    }
}
