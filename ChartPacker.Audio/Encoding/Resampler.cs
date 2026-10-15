using ChartPacker.Ffmpeg;

namespace ChartPacker.Audio.Encoding;

/// <summary>Converts interleaved float frames from one sample rate to another, fed chunk by chunk.</summary>
internal interface IResampler : IDisposable
{
    int Channels { get; }

    /// <summary>Converts <paramref name="input"/> into <paramref name="output"/> from its start.</summary>
    /// <param name="input">A whole number of frames; more may follow unless <paramref name="finished"/>.</param>
    /// <param name="output">Room for a whole number of frames.</param>
    /// <param name="finished">No more input follows; drain what's held back, holding the last frame.</param>
    /// <returns>How many frames were written; fewer than fit while waiting for more input.</returns>
    /// <exception cref="InvalidDataException">The audio can't be converted.</exception>
    int Convert(ReadOnlySpan<float> input, Span<float> output, bool finished);
}

/// <summary>Opens the best resampler for <paramref name="inputRate"/> to <paramref name="outputRate"/>: swresample when bundled, linear interpolation otherwise.</summary>
internal static class Resampler
{
    public static IResampler Open(int inputRate, int outputRate, int channels)
    {
        if (inputRate == outputRate || channels is not (1 or 2))
            return new LinearResampler(inputRate, outputRate, channels);

        if (FfmpegResampler.IsAvailable)
        {
            try
            {
                return new FfmpegResamplerAdapter(new FfmpegResampler(inputRate, outputRate, channels));
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // Fall through to the managed resampler.
            }
        }

        return new LinearResampler(inputRate, outputRate, channels);
    }

    private sealed class FfmpegResamplerAdapter(FfmpegResampler resampler) : IResampler
    {
        public int Channels => resampler.Channels;

        public int Convert(ReadOnlySpan<float> input, Span<float> output, bool finished)
        {
            int written = resampler.Convert(input, output);
            if (finished)
                written += resampler.Flush(output[(written * Channels)..]);
            return written;
        }

        public void Dispose() => resampler.Dispose();
    }
}

/// <summary>
/// Converts sample rates by linear interpolation, per channel: output frame <c>n</c> is input
/// position <c>n × input rate / output rate</c>, like the playback mixer's <c>StemReader</c>.
/// </summary>
internal sealed class LinearResampler : IResampler
{
    // Input frames per output frame.
    private readonly double _step;

    // Buffered input frames not yet consumed, from _start.
    private float[] _buffer = [];
    private int _start;
    private int _end;

    // Output frames written so far: frame n sits at input position n × _step.
    private long _produced;

    private bool _ended;

    /// <param name="inputRate">The rate of the audio fed to <see cref="Convert"/>.</param>
    /// <param name="outputRate">The rate <see cref="Convert"/> produces.</param>
    /// <param name="channels">Any positive channel count.</param>
    public LinearResampler(int inputRate, int outputRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _step = (double)inputRate / outputRate;
        Channels = channels;
    }

    public int Channels { get; }

    public int Convert(ReadOnlySpan<float> input, Span<float> output, bool finished)
    {
        if (input.Length % Channels != 0)
            throw new ArgumentException("The input must hold a whole number of frames.", nameof(input));
        if (output.Length % Channels != 0)
            throw new ArgumentException("The output must hold a whole number of frames.", nameof(output));

        if (finished)
            _ended = true;
        Append(input);

        int frames = output.Length / Channels;
        int available = _end - _start;
        int written = 0;
        while (written < frames)
        {
            double position = (_produced + written) * _step;
            int index = (int)position - _start;
            if (index < 0)
            {
                index = 0;
                position = _start;
            }

            if (index + 1 >= available)
            {
                if (!_ended)
                    break;

                // Past the end: hold the last frame (or silence when there is none).
                if (available == 0)
                {
                    output[(written * Channels)..].Clear();
                    return frames;
                }

                index = Math.Min(index, available - 1);
                int next = index;
                WriteFrame(output, written, index, next, 0);
                written++;
                continue;
            }

            float t = (float)(position - Math.Floor(position));
            WriteFrame(output, written, index, index + 1, t);
            written++;
        }

        _produced += written;

        // Drop the input frames the next output frame can't reach: anything before its
        // position less one (kept for interpolation), so the buffer stays small.
        int drop = Math.Clamp((int)(_produced * _step) - 1 - _start, 0, available);
        if (drop > 0)
        {
            Array.Copy(_buffer, _start * Channels, _buffer, 0, (available - drop) * Channels);
            _start = 0;
            _end = available - drop;
        }

        return written;
    }

    public void Dispose()
    {
    }

    private void WriteFrame(Span<float> output, int frame, int first, int second, float t)
    {
        for (int c = 0; c < Channels; c++)
        {
            float a = _buffer[((_start + first) * Channels) + c];
            float b = _buffer[((_start + second) * Channels) + c];
            output[(frame * Channels) + c] = a + ((b - a) * t);
        }
    }

    private void Append(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty)
            return;

        int available = _end - _start;
        int wanted = available + (input.Length / Channels);
        if (_buffer.Length < wanted * Channels)
            Array.Resize(ref _buffer, Math.Max(wanted * Channels, _buffer.Length * 2));
        else if (_start > 0)
        {
            Array.Copy(_buffer, _start * Channels, _buffer, 0, available * Channels);
            _start = 0;
            _end = available;
        }

        input.CopyTo(_buffer.AsSpan(_end * Channels));
        _end += input.Length / Channels;
    }
}
