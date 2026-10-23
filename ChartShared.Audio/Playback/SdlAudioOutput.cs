using SDL;
using static SDL.SDL3;

namespace ChartShared.Audio.Playback;

/// <summary>
/// Plays audio on the default output device through <see href="https://libsdl.org">SDL3</see>'s
/// audio subsystem (via <see href="https://github.com/ppy/SDL3-CS">ppy/SDL3-CS</see>; see
/// <c>THIRD-PARTY-NOTICES.md</c>). Nothing else of SDL is used.
/// </summary>
/// <remarks>
/// SDL converts the stream to whatever the device wants, and moves it to the new default device
/// when that changes, e.g. when headphones are unplugged. Opened by <see cref="SdlAudio.Open"/>,
/// which keeps SDL's audio subsystem running; opening and closing a device stream is safe from
/// any thread.
/// <para>
/// SDL moves the stream without saying so, and only reports device events to an event loop this
/// app doesn't run, so <see cref="Device"/> is checked when playback starts and once a second.
/// </para>
/// <para>
/// The device carries the latency the app can read for it (<see cref="DeviceLatency"/>): SDL's device
/// buffer, and on Windows what WASAPI reports for the endpoint (issue #140).
/// </para>
/// </remarks>
public sealed unsafe class SdlAudioOutput : IAudioOutput
{
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);
    private static readonly TimeSpan DeviceCheckInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private readonly Lock _deviceLock = new();
    private readonly Lock _checkGate = new();
    private readonly Timer _deviceCheck;
    private SDL_AudioStream* _stream;
    private AudioDevice _device;
    private (string Name, string? Id, TimeSpan? Latency)? _osLatency;

    /// <exception cref="AudioDeviceException">No output device could be opened.</exception>
    internal SdlAudioOutput(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;

        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = Channels, freq = sampleRate };
        _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
        if (_stream is null)
            throw new AudioDeviceException(SDL_GetError() ?? "No audio output device could be opened.");

        _device = ReadDevice() ?? AudioDevice.Unknown;
        _deviceCheck = new Timer(_ => CheckDevice(), null, DeviceCheckInterval, DeviceCheckInterval);
    }

    public int SampleRate { get; }

    public AudioDevice Device
    {
        get
        {
            lock (_deviceLock)
                return _device;
        }
    }

    public event Action<AudioDevice>? DeviceChanged;

    public int QueuedFrames
    {
        get
        {
            lock (_lock)
                return _stream is null ? 0 : Math.Max(0, SDL_GetAudioStreamQueued(_stream)) / BytesPerFrame;
        }
    }

    public float Volume
    {
        get
        {
            lock (_lock)
                return _stream is null ? 0 : SDL_GetAudioStreamGain(_stream);
        }
        set
        {
            lock (_lock)
            {
                if (_stream is not null)
                    SDL_SetAudioStreamGain(_stream, Math.Clamp(value, 0f, 1f));
            }
        }
    }

    public float Speed
    {
        get
        {
            lock (_lock)
                return _stream is null ? 1 : SDL_GetAudioStreamFrequencyRatio(_stream);
        }
        set
        {
            lock (_lock)
            {
                // SDL resamples the stream on its way to the device, so it plays faster and higher, or slower and lower.
                if (_stream is not null)
                    SDL_SetAudioStreamFrequencyRatio(_stream, float.IsFinite(value) ? Math.Clamp(value, 0.01f, 100f) : 1f);
            }
        }
    }

    public void Write(ReadOnlySpan<float> stereo)
    {
        lock (_lock)
        {
            if (_stream is null || stereo.IsEmpty)
                return;

            fixed (float* data = stereo)
            {
                if (!SDL_PutAudioStreamData(_stream, (IntPtr)data, stereo.Length * sizeof(float)))
                    throw new AudioDeviceException(SDL_GetError() ?? "Audio couldn't be queued.");
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_stream is not null)
                SDL_ClearAudioStream(_stream);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_stream is not null)
                SDL_PauseAudioStreamDevice(_stream);
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_stream is not null)
                SDL_ResumeAudioStreamDevice(_stream);
        }

        // Off the caller's thread: reading the endpoint is a COM call.
        ThreadPool.QueueUserWorkItem(_ => CheckDevice());
    }

    public void Dispose()
    {
        _deviceCheck.Dispose();
        lock (_lock)
        {
            if (_stream is null)
                return;

            // Destroying a device stream closes its device too.
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }
    }

    /// <summary>Raises <see cref="DeviceChanged"/> when the stream is on another device than it was.</summary>
    private void CheckDevice()
    {
        // One check at a time, so the changes are raised in order; a check that finds one running skips.
        if (!_checkGate.TryEnter())
            return;
        try
        {
            if (ReadDevice() is not { } device)
                return;

            lock (_deviceLock)
            {
                if (device == _device)
                    return;
                _device = device;
            }
            DeviceChanged?.Invoke(device);
        }
        finally
        {
            _checkGate.Exit();
        }
    }

    /// <summary>The device the stream is on now, with the latency read for it, or null once it's closed.</summary>
    private AudioDevice? ReadDevice()
    {
        string? name;
        int sampleFrames = 0;
        int deviceRate = 0;
        lock (_lock)
        {
            if (_stream is null)
                return null;

            // The stream's device is the default device, which SDL resolves to the physical one it's on.
            var device = SDL_GetAudioStreamDevice(_stream);
            name = SDL_GetAudioDeviceName(device);

            // SDL's device buffer, which the stream's queue doesn't count (C10 item 5).
            SDL_AudioSpec spec;
            int frames;
            if (SDL_GetAudioDeviceFormat(device, &spec, &frames))
            {
                sampleFrames = frames;
                deviceRate = spec.freq;
            }
        }
        name ??= "";
        if (!OperatingSystem.IsWindows())
            return new AudioDevice(name, null, DeviceLatency.Milliseconds(sampleFrames, deviceRate, null));

        // The endpoint's ID is cheap to read, so it's read every second; its latency isn't, so it's cached per
        // device. On a miss both are re-read from the same endpoint in one pass, so they can't come from different
        // devices when the default changes between reads.
        var id = WindowsAudioEndpoint.DefaultRenderId();
        if (_osLatency is { } known && known.Name == name && known.Id == id)
            return new AudioDevice(name, id, DeviceLatency.Milliseconds(sampleFrames, deviceRate, known.Latency));

        var (freshId, freshLatency) = WindowsAudioEndpoint.DefaultRenderIdAndLatency();
        _osLatency = (name, freshId, freshLatency);
        return new AudioDevice(name, freshId, DeviceLatency.Milliseconds(sampleFrames, deviceRate, freshLatency));
    }
}
