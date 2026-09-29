using System.Buffers.Binary;
using ChartPacker.Ffmpeg;
using Concentus.Structs;

namespace ChartPacker.Audio.Decoding;

/// <summary>
/// Decodes Ogg Opus: the container is read here, written from RFC 7845 (Ogg Encapsulation for the
/// Opus Audio Codec), and the packets are decoded by FFmpeg's <c>opus</c> decoder or by
/// <see href="https://github.com/lostromb/concentus">Concentus</see> (see <see cref="AudioBackend"/>).
/// </summary>
/// <remarks>
/// <para>Opus always decodes at 48 kHz. Positions on the stream's timeline are granule positions
/// (RFC 7845 §4): frame 0 of this source is the first sample after the header's pre-skip, and the
/// last page's granule position trims the end. The pre-skip and the header's output gain are applied
/// here, never by the decoder, so both decoders give the same timeline.</para>
/// <para>A seek finds the last page that ends at least 160 ms before the target, decodes from
/// there and discards up to the target (RFC 7845 §4.6), so the result is sample-exact.</para>
/// </remarks>
public sealed class OpusSource : IPcmSource
{
    private const int Rate = 48000;

    // The longest Opus packet: 120 ms.
    private const int MaxPacketFrames = 5760;

    // Decoding from this far before a seek target lets the decoder converge. RFC 7845 §4.6 asks
    // for at least 80 ms; by testing, twice that gives what a straight decode gives.
    private const int PreRollFrames = 7680;

    private readonly Stream _stream;
    private readonly OggPageReader _ogg;
    private readonly IPacketDecoder _decoder;
    private readonly float _gain;
    private readonly int _firstAudioPage;

    // Granule positions: where the first audio packet starts, where frame 0 is, and where the stream ends.
    private readonly long _streamStart;
    private readonly long _origin;
    private readonly long _end;

    // Pages that end a packet, with their granule positions, for seeking.
    private readonly List<(int Page, long Granule)> _seekPoints = [];

    private readonly float[] _pcm;
    private int _bufferOffset;
    private int _bufferFrames;

    // The granule position of the frame at _bufferOffset, and of the next packet's first frame.
    private long _bufferPosition;
    private long _nextPosition;

    // Decoded frames before this granule position are discarded: pre-skip, or seek pre-roll.
    private long _discardUntil;
    private bool _ended;

    /// <param name="backend">What decodes the packets.</param>
    /// <exception cref="InvalidDataException">The stream isn't Ogg Opus this can decode.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="backend"/> is <see cref="AudioBackend.Ffmpeg"/>, and FFmpeg can't decode the stream.</exception>
    public OpusSource(Stream stream, AudioBackend backend = AudioBackend.Auto)
        : this(stream, backend, FfmpegAudioDecoder.IsAvailable)
    {
    }

    internal OpusSource(Stream stream, AudioBackend backend, Func<string, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _ogg = new OggPageReader(stream);

        // RFC 7845 §5.1: the identification header.
        _ogg.Restart(0, skipEndedOnPage: false);
        if (!_ogg.TryReadPacket(out var head) || head.Length < 19 || !head[..8].SequenceEqual("OpusHead"u8))
            throw new InvalidDataException("Not an Ogg Opus stream.");
        if (head[8] >> 4 != 0)
            throw new InvalidDataException($"Ogg Opus version {head[8]} isn't supported.");

        Channels = head[9];
        int preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head[10..]);
        short outputGain = BinaryPrimitives.ReadInt16LittleEndian(head[16..]);
        int mappingFamily = head[18];
        if (Channels == 0)
            throw new InvalidDataException("The Opus header has no channels.");

        // Output gain is in Q7.8 dB (§5.1).
        _gain = MathF.Pow(10, outputGain / (20f * 256f));

        (int Streams, int Coupled, byte[] Table)? mapping = null;
        if (mappingFamily == 0)
        {
            if (Channels > 2)
                throw new InvalidDataException("An Opus stream without a channel mapping has more than two channels.");
        }
        else
        {
            // §5.1.1: a mapping table follows for every other family.
            if (head.Length < 21 + Channels)
                throw new InvalidDataException("The Opus channel mapping table is cut short.");
            mapping = (head[19], head[20], head.Slice(21, Channels).ToArray());
        }

        // FFmpeg reads the whole header itself, mapping included. Its gain is zeroed, as it's applied here.
        byte[]? ffmpegHead = null;
        if (AudioDecoder.UsesFfmpeg(backend, DecoderName, Channels, isAvailable))
        {
            ffmpegHead = head.ToArray();
            ffmpegHead.AsSpan(16, 2).Clear();
        }

        // §5.2: the comment header, which ends its page; audio starts on the next one.
        if (!_ogg.TryReadPacket(out var tags) || tags.Length < 8 || !tags[..8].SequenceEqual("OpusTags"u8))
            throw new InvalidDataException("The Ogg Opus stream has no comment header.");
        _firstAudioPage = _ogg.LastPacketPage + 1;

        var pages = _ogg.Pages;
        for (int i = _firstAudioPage; i < pages.Count; i++)
        {
            if (pages[i].Granule >= 0)
                _seekPoints.Add((i, pages[i].Granule));
        }

        // §4.3: the first audio page's granule position, less the samples that end on it, is
        // where the stream starts. It's normally 0.
        _streamStart = 0;
        if (_seekPoints.Count > 0 && _seekPoints[0].Page == _firstAudioPage)
        {
            _ogg.Restart(_firstAudioPage, skipEndedOnPage: false);
            long samples = 0;
            while (_ogg.TryReadPacket(out var packet) && _ogg.LastPacketPage == _firstAudioPage)
                samples += PacketFrames(packet);
            _streamStart = _seekPoints[0].Granule - samples;
        }

        // A negative start means samples before 0 are dropped, on top of the pre-skip (§4.5).
        _origin = Math.Max(_streamStart, 0) + preSkip;
        _end = _seekPoints.Count > 0 ? _seekPoints[^1].Granule : _origin;

        _pcm = new float[MaxPacketFrames * Channels];

        // Made last, once nothing else can throw, so that a stream that isn't Opus leaves nothing to free.
        IPacketDecoder? ffmpeg = ffmpegHead is not null
            ? FfmpegPacketDecoder.Open(backend, DecoderName, Channels, ffmpegHead, trimsStart: false, errorsAreSilence: false)
            : null;
        _decoder = ffmpeg ?? new ConcentusDecoder(Channels, mapping);
        Rewind(_origin);
    }

    /// <summary>FFmpeg's native Opus decoder.</summary>
    internal const string DecoderName = "opus";

    public int SampleRate => Rate;

    public int Channels { get; }

    public long LengthFrames => Math.Max(0, _end - _origin);

    public int ReadFrames(Span<float> buffer)
    {
        int wanted = buffer.Length / Channels;
        int written = 0;
        while (written < wanted)
        {
            if (_bufferFrames > 0)
            {
                long toEnd = _end - _bufferPosition;
                if (toEnd <= 0)
                {
                    _bufferFrames = 0;
                    _ended = true;
                    break;
                }

                int count = (int)Math.Min(Math.Min(_bufferFrames, wanted - written), toEnd);
                _pcm.AsSpan(_bufferOffset * Channels, count * Channels).CopyTo(buffer[(written * Channels)..]);
                _bufferOffset += count;
                _bufferFrames -= count;
                _bufferPosition += count;
                written += count;
                continue;
            }

            if (_ended || !DecodeNextPacket())
            {
                _ended = true;
                break;
            }
        }

        return written;
    }

    public void SeekToFrame(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (frame >= LengthFrames)
        {
            _bufferFrames = 0;
            _ended = true;
            return;
        }

        Rewind(_origin + frame);
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _stream.Dispose();
    }

    /// <summary>Starts decoding far enough before the granule position <paramref name="target"/> to reach it exactly.</summary>
    private void Rewind(long target)
    {
        _bufferFrames = 0;
        _ended = false;
        _discardUntil = target;
        _decoder.Reset();

        // The last page ending a packet at or before the pre-roll point; failing that, the start.
        long from = target - PreRollFrames;
        int low = 0, high = _seekPoints.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_seekPoints[mid].Granule <= from)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found < 0 || _seekPoints[found].Granule <= _streamStart)
        {
            _ogg.Restart(_firstAudioPage, skipEndedOnPage: false);
            _nextPosition = _streamStart;
        }
        else
        {
            _ogg.Restart(_seekPoints[found].Page, skipEndedOnPage: true);
            _nextPosition = _seekPoints[found].Granule;
        }
    }

    /// <summary>Decodes the next packet into the buffer, dropping what comes before <see cref="_discardUntil"/>.</summary>
    /// <returns><see langword="false"/> at the end of the stream.</returns>
    private bool DecodeNextPacket()
    {
        if (!_ogg.TryReadPacket(out var packet))
            return false;

        // An empty packet holds no samples on the granule timeline (see PacketFrames); decoding it
        // would conceal a lost packet instead, adding audio that isn't there.
        if (packet.IsEmpty)
        {
            _bufferFrames = 0;
            return true;
        }

        int frames = _decoder.Decode(packet, _pcm);
        _bufferOffset = 0;
        _bufferFrames = frames;
        _bufferPosition = _nextPosition;
        _nextPosition += frames;

        if (_bufferPosition < _discardUntil)
        {
            int drop = (int)Math.Min(frames, _discardUntil - _bufferPosition);
            _bufferOffset = drop;
            _bufferFrames -= drop;
            _bufferPosition += drop;
        }

        if (_gain != 1f)
        {
            foreach (ref float sample in _pcm.AsSpan(_bufferOffset * Channels, _bufferFrames * Channels))
                sample *= _gain;
        }

        return true;
    }

    private static int PacketFrames(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
            return 0;

        try
        {
            return Math.Max(0, OpusPacketInfo.GetNumSamples(packet, Rate));
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }
    }
}
