using SDL;
using static SDL.SDL3;

namespace ChartPacker.Audio.Playback;

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
/// </remarks>
public sealed unsafe class SdlAudioOutput : IAudioOutput
{
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);

    private readonly Lock _lock = new();
    private SDL_AudioStream* _stream;

    /// <exception cref="AudioDeviceException">No output device could be opened.</exception>
    internal SdlAudioOutput(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;

        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = Channels, freq = sampleRate };
        _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
        if (_stream is null)
            throw new AudioDeviceException(SDL_GetError() ?? "No audio output device could be opened.");
    }

    public int SampleRate { get; }

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
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_stream is null)
                return;

            // Destroying a device stream closes its device too.
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }
    }
}
