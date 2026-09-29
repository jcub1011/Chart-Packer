using System.Buffers.Binary;
using ChartPacker.Audio.Decoding;
using ChartPacker.Ffmpeg;

namespace ChartPacker.Core.Tests.Audio;

/// <summary>Audio for the playback tests: WAV files built in code, the checked-in fixtures, and helpers to read sources.</summary>
internal static class TestAudio
{
    /// <summary>A checked-in fixture (see <c>Fixtures/README.md</c>).</summary>
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Audio", "Fixtures", name);

    /// <summary>
    /// The sweep every fixture holds on its left channel: half amplitude, 200 Hz rising to 2 kHz
    /// over one second. The right channel sweeps down.
    /// </summary>
    public static double Chirp(double seconds, bool rising = true) =>
        0.5 * Math.Sin(2 * Math.PI * (rising ? (200 * seconds) + (900 * seconds * seconds) : (2000 * seconds) - (900 * seconds * seconds)));

    /// <summary>How closely the left channel of <paramref name="interleaved"/> follows <see cref="Chirp"/> at the same time, from -1 to 1.</summary>
    public static double CorrelationWithChirp(float[] interleaved, int channels, int sampleRate)
    {
        // The ends are skipped: lossy codecs smear the very start and end.
        double xy = 0, xx = 0, yy = 0;
        for (int i = sampleRate / 10; i < sampleRate * 9 / 10; i++)
        {
            double x = interleaved[i * channels];
            double y = Chirp((double)i / sampleRate);
            xy += x * y;
            xx += x * x;
            yy += y * y;
        }

        return xy / Math.Sqrt(xx * yy);
    }

    /// <summary>Everything <paramref name="source"/> has from where it is, interleaved.</summary>
    public static float[] ReadAll(IPcmSource source, int chunkFrames = 1000)
    {
        var all = new List<float>();
        var buffer = new float[chunkFrames * source.Channels];
        int frames;
        while ((frames = source.ReadFrames(buffer)) > 0)
            all.AddRange(buffer.AsSpan(0, frames * source.Channels));
        return [.. all];
    }

    /// <summary>Up to <paramref name="frames"/> frames from where <paramref name="source"/> is, interleaved.</summary>
    public static float[] Read(IPcmSource source, int frames)
    {
        var buffer = new float[frames * source.Channels];
        int read = 0;
        int count;
        while (read < frames && (count = source.ReadFrames(buffer.AsSpan(read * source.Channels))) > 0)
            read += count;
        return buffer[..(read * source.Channels)];
    }

    /// <summary>A fixture, decoded on <paramref name="backend"/>; see <see cref="RequireBackend"/>.</summary>
    public static IPcmSource OpenFixture(string name, AudioBackend backend = AudioBackend.Managed)
    {
        RequireBackend(backend, name);
        return AudioDecoder.Open(new FileStream(Fixture(name), FileMode.Open, FileAccess.Read, FileShare.Read), name, backend);
    }

    /// <summary>
    /// Ends the test as inconclusive when <paramref name="backend"/> is FFmpeg and it can't decode the file called
    /// <paramref name="name"/>: its libraries are fetched for development (see <c>build/ffmpeg</c>), not checked in.
    /// </summary>
    public static void RequireBackend(AudioBackend backend, string name)
    {
        if (backend != AudioBackend.Ffmpeg)
            return;

        string decoder = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".ogg" => "vorbis",
            ".opus" => "opus",
            ".mp3" => "mp3float",
            _ => throw new ArgumentException($"'{name}' isn't decoded by FFmpeg.", nameof(name)),
        };
        if (!FfmpegAudioDecoder.IsAvailable(decoder))
            Assert.Inconclusive(FfmpegLibrary.UnavailableReason ?? $"FFmpeg's libraries have no '{decoder}' decoder.");
    }

    /// <summary>A WAV file of <paramref name="samples"/> (interleaved, in [-1, 1]) in the given sample format.</summary>
    /// <param name="format">1 for integer PCM, 3 for IEEE float.</param>
    /// <param name="extensible">Write a <c>WAVE_FORMAT_EXTENSIBLE</c> header carrying <paramref name="format"/>.</param>
    public static byte[] Wav(float[] samples, int sampleRate, int channels, int bits = 16, int format = 1, bool extensible = false)
    {
        int bytesPerSample = bits / 8;
        int dataLength = samples.Length * bytesPerSample;
        int fmtLength = extensible ? 40 : 16;
        byte[] wav = new byte[12 + 8 + fmtLength + 8 + dataLength];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        "WAVE"u8.CopyTo(span[8..]);

        var fmt = span[12..];
        "fmt "u8.CopyTo(fmt);
        BinaryPrimitives.WriteInt32LittleEndian(fmt[4..], fmtLength);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[8..], extensible ? (ushort)0xFFFE : (ushort)format);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[10..], (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(fmt[12..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(fmt[16..], sampleRate * channels * bytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[20..], (ushort)(channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[22..], (ushort)bits);
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(fmt[24..], 22);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt[26..], (ushort)bits);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt[32..], (ushort)format);
        }

        var data = span[(20 + fmtLength)..];
        "data"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], dataLength);
        var body = data[8..];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = Math.Clamp(samples[i], -1f, 1f);
            var at = body[(i * bytesPerSample)..];
            switch (bits, format)
            {
                case (8, 1):
                    at[0] = (byte)Math.Round((s * 127) + 128);
                    break;
                case (16, 1):
                    BinaryPrimitives.WriteInt16LittleEndian(at, (short)Math.Round(s * 32767));
                    break;
                case (24, 1):
                    int v24 = (int)Math.Round(s * 8388607);
                    at[0] = (byte)v24;
                    at[1] = (byte)(v24 >> 8);
                    at[2] = (byte)(v24 >> 16);
                    break;
                case (32, 1):
                    BinaryPrimitives.WriteInt32LittleEndian(at, (int)Math.Round(s * 2147483647.0));
                    break;
                case (32, 3):
                    BinaryPrimitives.WriteSingleLittleEndian(at, s);
                    break;
                case (64, 3):
                    BinaryPrimitives.WriteDoubleLittleEndian(at, s);
                    break;
                default:
                    throw new ArgumentException("Unsupported sample format.");
            }
        }

        return wav;
    }

    /// <summary>A 16-bit WAV whose every sample of channel <c>c</c> at frame <c>n</c> is <paramref name="value"/>(n, c).</summary>
    public static byte[] Wav(int frames, int sampleRate, int channels, Func<int, int, float> value, int bits = 16)
    {
        var samples = new float[frames * channels];
        for (int n = 0; n < frames; n++)
        {
            for (int c = 0; c < channels; c++)
                samples[(n * channels) + c] = value(n, c);
        }

        return Wav(samples, sampleRate, channels, bits);
    }

    /// <summary>A source of computed samples: channel <c>c</c> of frame <c>n</c> is <paramref name="value"/>(n, c).</summary>
    /// <remarks>It has <paramref name="frames"/> frames; set <see cref="LengthFrames"/> to -1 to hide how many.</remarks>
    public sealed class FakeSource(int sampleRate, int channels, long frames, Func<long, int, float> value) : IPcmSource
    {
        private readonly long _frames = frames;
        private long _position;

        public int SampleRate => sampleRate;

        public int Channels => channels;

        public long LengthFrames { get; init; } = frames;

        /// <summary>Makes the next read throw, as a damaged file does.</summary>
        public bool IsDamaged { get; set; }

        public bool IsDisposed { get; private set; }

        public int Reads { get; private set; }

        public int ReadFrames(Span<float> buffer)
        {
            Reads++;
            if (IsDamaged)
                throw new InvalidDataException("Damaged.");

            int count = (int)Math.Max(0, Math.Min(buffer.Length / channels, _frames - _position));
            for (int n = 0; n < count; n++)
            {
                for (int c = 0; c < channels; c++)
                    buffer[(n * channels) + c] = value(_position + n, c);
            }

            _position += count;
            return count;
        }

        public void SeekToFrame(long frame) => _position = Math.Min(frame, _frames);

        public void Dispose() => IsDisposed = true;
    }
}
