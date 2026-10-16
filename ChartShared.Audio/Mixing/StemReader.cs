using ChartShared.Audio.Decoding;

namespace ChartShared.Audio.Mixing;

/// <summary>
/// Reads a source as stereo at the mixer's rate: mono is sent to both sides, more than two
/// channels are folded into two, and a different sample rate is converted by linear interpolation.
/// </summary>
/// <remarks>
/// Output frame <c>n</c> is source position <c>n × source rate / output rate</c>, so every stem
/// read the same number of frames is at the same point in time. After the source ends, reads
/// return silence rather than fewer frames, which keeps it aligned with the others.
/// </remarks>
internal sealed class StemReader(IPcmSource source, int outputRate)
{
    private const int ChunkFrames = 1024;
    private readonly double _step = (double)source.SampleRate / outputRate;
    private readonly float[] _raw = new float[ChunkFrames * source.Channels];

    // Source frames converted to stereo, not yet used; one frame of history is kept for interpolating.
    private float[] _stereo = new float[(ChunkFrames + 2) * 2];
    private int _stereoFrames;

    // For resampling: the position in _stereo of the next output frame.
    private double _cursor;
    private bool _sourceEnded;

    public bool IsResampled => source.SampleRate != outputRate;

    /// <summary>The source's length at the output rate, or -1 when it isn't known.</summary>
    public long LengthFrames => source.LengthFrames < 0 ? -1 : (long)Math.Ceiling(source.LengthFrames / _step);

    /// <summary>The source has no more audio; reads return silence.</summary>
    public bool IsEnded => _sourceEnded && (IsResampled ? (int)_cursor >= _stereoFrames : _stereoFrames == 0);

    /// <summary>Fills <paramref name="stereo"/> (interleaved left, right) completely, with silence after the source ends.</summary>
    /// <exception cref="InvalidDataException">The audio is damaged.</exception>
    public void Read(Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        if (!IsResampled)
        {
            int written = 0;
            while (written < frames)
            {
                if (_stereoFrames == 0 && !Fill())
                    break;

                int count = Math.Min(_stereoFrames, frames - written);
                _stereo.AsSpan(0, count * 2).CopyTo(stereo[(written * 2)..]);
                Consume(count);
                written += count;
            }

            stereo[(written * 2)..].Clear();
            return;
        }

        for (int i = 0; i < frames; i++)
        {
            // Two source frames are needed around the cursor.
            while (_cursor + 1 >= _stereoFrames && !_sourceEnded)
                Fill();

            int index = (int)_cursor;
            if (index >= _stereoFrames)
            {
                stereo[(i * 2)..].Clear();
                return;
            }

            float t = (float)(_cursor - index);
            int next = Math.Min(index + 1, _stereoFrames - 1);
            stereo[i * 2] = _stereo[index * 2] + ((_stereo[next * 2] - _stereo[index * 2]) * t);
            stereo[(i * 2) + 1] = _stereo[(index * 2) + 1] + ((_stereo[(next * 2) + 1] - _stereo[(index * 2) + 1]) * t);
            _cursor += _step;

            // Drop the frames the cursor has passed, keeping the one it's between.
            int passed = (int)_cursor;
            if (passed > ChunkFrames)
            {
                Consume(passed);
                _cursor -= passed;
            }
        }
    }

    /// <summary>Moves to output frame <paramref name="frame"/>.</summary>
    /// <exception cref="InvalidDataException">The audio is damaged.</exception>
    public void Seek(long frame)
    {
        double position = frame * _step;
        long sourceFrame = (long)Math.Floor(position);
        source.SeekToFrame(sourceFrame);
        _stereoFrames = 0;
        _cursor = position - sourceFrame;
        _sourceEnded = false;
    }

    /// <summary>Reads more of the source onto the end of <see cref="_stereo"/>.</summary>
    /// <returns><see langword="false"/> when the source has ended.</returns>
    private bool Fill()
    {
        int frames = source.ReadFrames(_raw);
        if (frames == 0)
        {
            _sourceEnded = true;
            return false;
        }

        if (_stereo.Length < (_stereoFrames + frames) * 2)
            Array.Resize(ref _stereo, (_stereoFrames + frames) * 2);

        int channels = source.Channels;
        var raw = _raw.AsSpan(0, frames * channels);
        var dest = _stereo.AsSpan(_stereoFrames * 2, frames * 2);
        switch (channels)
        {
            case 1:
                for (int i = 0; i < frames; i++)
                    dest[i * 2] = dest[(i * 2) + 1] = raw[i];
                break;
            case 2:
                raw.CopyTo(dest);
                break;
            default:
                // Even channels to the left, odd to the right, averaged so the level holds.
                int left = (channels + 1) / 2;
                int right = channels / 2;
                for (int i = 0; i < frames; i++)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        if (c % 2 == 0)
                            l += raw[(i * channels) + c];
                        else
                            r += raw[(i * channels) + c];
                    }

                    dest[i * 2] = l / left;
                    dest[(i * 2) + 1] = r / right;
                }
                break;
        }

        _stereoFrames += frames;
        return true;
    }

    private void Consume(int frames)
    {
        _stereo.AsSpan(frames * 2, (_stereoFrames - frames) * 2).CopyTo(_stereo);
        _stereoFrames -= frames;
    }
}
