using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Ffmpeg;

/// <summary>
/// Converts interleaved float audio between sample rates with FFmpeg's swresample.
/// Mono stays mono and stereo stays stereo; other channel counts aren't supported.
/// </summary>
/// <remarks>
/// Not thread-safe: one thread uses it at a time. When swresample isn't bundled,
/// <see cref="IsAvailable"/> is false and callers fall back to managed resampling —
/// swresample is probed on its own so installs that predate it keep video working.
/// </remarks>
public sealed unsafe class FfmpegResampler : IDisposable
{
    private static bool? _available;

    private void* _ctx;

    /// <param name="inputRate">The sample rate of the audio fed to <see cref="Convert"/>.</param>
    /// <param name="outputRate">The sample rate <see cref="Convert"/> produces.</param>
    /// <param name="channels">1 for mono, 2 for stereo.</param>
    /// <exception cref="ArgumentOutOfRangeException">A rate isn't positive, or <paramref name="channels"/> isn't 1 or 2.</exception>
    /// <exception cref="InvalidOperationException">swresample isn't bundled, or won't convert this setup.</exception>
    public FfmpegResampler(int inputRate, int outputRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "swresample is used here for mono and stereo only.");

        if (!IsAvailable)
            throw new InvalidOperationException("FFmpeg's swresample library isn't bundled in this build.");

        Channels = channels;
        InputRate = inputRate;
        OutputRate = outputRate;

        AVChannelLayout input = Layout(channels);
        AVChannelLayout output = Layout(channels);
        void* ctx = null;
        int allocated = Native.swr_alloc_set_opts2(
            &ctx, &output, Native.SampleFormatFloat, outputRate,
            &input, Native.SampleFormatFloat, inputRate, 0, null);
        if (allocated < 0 || ctx == null)
            throw new InvalidOperationException($"FFmpeg's resampler can't be set up (FFmpeg error {allocated}).");
        _ctx = ctx;

        int result = Native.swr_init(_ctx);
        if (result < 0)
        {
            Dispose();
            throw new InvalidOperationException($"FFmpeg's resampler can't be started (FFmpeg error {result}).");
        }
    }

    public int Channels { get; }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Whether swresample is bundled at the version the bindings are written for.</summary>
    public static bool IsAvailable
    {
        get
        {
            _available ??= Probe();
            return _available.Value;
        }
    }

    /// <summary>Converts <paramref name="input"/> (interleaved) into <paramref name="output"/> from its start.</summary>
    /// <param name="input">Room for a whole number of frames.</param>
    /// <param name="output">Room for a whole number of frames.</param>
    /// <returns>How many frames were written; fewer than fit when the resampler holds some back.</returns>
    /// <exception cref="InvalidDataException">The audio can't be converted.</exception>
    public int Convert(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_ctx == null, this);
        if (input.Length % Channels != 0)
            throw new ArgumentException("The input must hold a whole number of frames.", nameof(input));
        if (output.Length % Channels != 0)
            throw new ArgumentException("The output must hold a whole number of frames.", nameof(output));

        fixed (float* inPtr = input)
        fixed (float* outPtr = output)
        {
            byte* inBytes = (byte*)inPtr;
            byte* outBytes = (byte*)outPtr;
            int written = Native.swr_convert(_ctx, &outBytes, output.Length / Channels, &inBytes, input.Length / Channels);
            if (written < 0)
                throw new InvalidDataException($"The audio can't be resampled (FFmpeg error {written}).");
            return written;
        }
    }

    /// <summary>Drains what <see cref="Convert"/> held back into <paramref name="output"/> from its start.</summary>
    /// <returns>How many frames were written.</returns>
    /// <exception cref="InvalidDataException">The audio can't be converted.</exception>
    public int Flush(Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_ctx == null, this);
        if (output.Length % Channels != 0)
            throw new ArgumentException("The output must hold a whole number of frames.", nameof(output));

        fixed (float* outPtr = output)
        {
            byte* outBytes = (byte*)outPtr;
            int written = Native.swr_convert(_ctx, &outBytes, output.Length / Channels, null, 0);
            if (written < 0)
                throw new InvalidDataException($"The audio can't be resampled (FFmpeg error {written}).");
            return written;
        }
    }

    public void Dispose()
    {
        if (_ctx != null)
        {
            void* ctx = _ctx;
            Native.swr_free(&ctx);
            _ctx = null;
        }
    }

    private static AVChannelLayout Layout(int channels) => new()
    {
        order = Native.ChannelOrderNative,
        nb_channels = channels,
        u = channels == 1 ? Native.ChannelMaskMono : Native.ChannelMaskStereo,
        opaque = null,
    };

    private static bool Probe()
    {
        try
        {
            // Force the load through the assembly's resolver (now mapping swresample).
            FfmpegLibrary.EnsureAvailable();
            return (int)(Native.swresample_version() >> 16) == Native.SwResampleMajor;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }
}
