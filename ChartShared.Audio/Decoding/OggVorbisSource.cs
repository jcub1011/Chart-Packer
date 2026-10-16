using System.Buffers.Binary;
using ChartShared.Ffmpeg;

namespace ChartShared.Audio.Decoding;

/// <summary>
/// Decodes Ogg Vorbis with FFmpeg's <c>vorbis</c> decoder: the container is read here, written from the
/// Vorbis I specification (§4.2 and Appendix A), and only the packets go to FFmpeg. Without FFmpeg,
/// <see cref="VorbisSource"/> decodes Vorbis with NVorbis instead, to the same frames.
/// </summary>
/// <remarks>
/// <para>Positions on the stream's timeline are granule positions: a page's is the sample position at
/// the end of the last packet that ends on it. The first packet decodes to nothing (§A.2), so where the
/// stream starts is the first audio page's granule position less the samples the packets ending on it
/// give. Frame 0 of this source is the first sample at or after granule position 0, and the last page's
/// granule position trims the end.</para>
/// <para>How many samples a packet gives depends on the packet before it, so positions are counted from
/// what the decoder gives rather than worked out. A seek starts decoding a page before the last page that
/// ends at least <see cref="PreRollFrames"/> before the target; once a packet ends past that page, what was
/// decoded ended at its granule position, and from there it's counted on to the target, so the result is
/// sample-exact.</para>
/// </remarks>
public sealed class OggVorbisSource : IPcmSource
{
    /// <summary>FFmpeg's native Vorbis decoder.</summary>
    internal const string DecoderName = "vorbis";

    // A packet gives at most half of each of two of the longest blocks, 8192 samples.
    private const int MaxPacketFrames = 8192;

    // Two of Vorbis's longest blocks, as VorbisSource decodes before a seek target.
    private const int PreRollFrames = 8192;

    private readonly Stream _stream;
    private readonly OggPageReader _ogg;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Future extensibility.")]
    private readonly IPacketDecoder _decoder;
    private readonly int _firstAudioPage;

    // Granule positions: where the first packet's samples would start, where frame 0 is, and where the stream ends.
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

    // After a seek, the page whose granule position the output reaches once a packet ends past it; -1 once it has.
    private int _anchorPage = -1;
    private long _anchorGranule;

    // Decoded frames before this granule position are discarded: seek pre-roll.
    private long _discardUntil;
    private bool _ended;

    private OggVorbisSource(Stream stream, OggPageReader ogg, int channels, int sampleRate, byte[] extradata, int firstAudioPage)
    {
        _stream = stream;
        _ogg = ogg;
        Channels = channels;
        SampleRate = sampleRate;
        _firstAudioPage = firstAudioPage;

        var pages = _ogg.Pages;
        for (int i = _firstAudioPage; i < pages.Count; i++)
        {
            if (pages[i].Granule >= 0)
                _seekPoints.Add((i, pages[i].Granule));
        }

        _pcm = new float[MaxPacketFrames * Channels];

        // FFmpeg drops the first packet's output itself, at the start and after every reset.
        _decoder = new FfmpegPacketDecoder(new FfmpegAudioDecoder(DecoderName, Channels, extradata, trimsStart: true), errorsAreSilence: false);
        try
        {
            // §A.2: the first audio page's granule position, less the samples that end on it, is where the stream
            // starts. It's normally 0; a negative start means the samples before 0 are dropped. When that page ends
            // the stream too, its granule position trims the end instead, and the stream starts at 0.
            if (_seekPoints.Count > 0 && !pages[_seekPoints[0].Page].IsEndOfStream)
            {
                var (anchorPage, anchorGranule) = _seekPoints[0];
                _ogg.Restart(_firstAudioPage, skipEndedOnPage: false);
                long samples = 0;
                while (_ogg.TryReadPacket(out var packet) && _ogg.LastPacketPage <= anchorPage)
                    samples += _decoder.Decode(packet, _pcm);
                _streamStart = anchorGranule - samples;
            }

            _origin = Math.Max(_streamStart, 0);
            _end = _seekPoints.Count > 0 ? _seekPoints[^1].Granule : _origin;
            Rewind(_origin);
        }
        catch
        {
            _decoder.Dispose();
            throw;
        }
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public long LengthFrames => Math.Max(0, _end - _origin);

    /// <summary>
    /// Reads Ogg Vorbis's headers from <paramref name="stream"/>, and opens it when FFmpeg is to decode it on
    /// <paramref name="backend"/>. The stream is disposed with the source.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when FFmpeg isn't to decode it, or on <see cref="AudioBackend.Auto"/> won't: the stream is
    /// then left for another decoder.
    /// </returns>
    /// <exception cref="InvalidDataException">The stream isn't Ogg Vorbis this can decode.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="backend"/> is <see cref="AudioBackend.Ffmpeg"/>, and FFmpeg can't decode the stream.</exception>
    internal static OggVorbisSource? TryOpen(Stream stream, AudioBackend backend, Func<string, bool> isAvailable)
    {
        var ogg = new OggPageReader(stream);
        ogg.Restart(0, skipEndedOnPage: false);

        // §4.2.2: the identification header.
        if (!ogg.TryReadPacket(out var identification) || !IsHeader(identification, 1) || identification.Length < 30)
            throw new InvalidDataException("Not an Ogg Vorbis stream.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(identification[7..]) != 0)
            throw new InvalidDataException("Only Vorbis I is supported.");

        int channels = identification[11];
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(identification[12..]);
        if (channels == 0 || rate == 0 || rate > int.MaxValue || (identification[29] & 1) == 0)
            throw new InvalidDataException("The Vorbis header is invalid.");

        if (!AudioDecoder.UsesFfmpeg(backend, DecoderName, channels, isAvailable))
            return null;

        byte[] first = identification.ToArray();

        // §4.2.3 and §4.2.4: the comment and setup headers. The setup header ends its page; audio starts on the next.
        if (!ogg.TryReadPacket(out var comments) || !IsHeader(comments, 3))
            throw new InvalidDataException("The Ogg Vorbis stream has no comment header.");
        byte[] second = comments.ToArray();
        if (!ogg.TryReadPacket(out var setup) || !IsHeader(setup, 5))
            throw new InvalidDataException("The Ogg Vorbis stream has no setup header.");

        try
        {
            return new OggVorbisSource(stream, ogg, channels, (int)rate, Extradata(first, second, setup), ogg.LastPacketPage + 1);
        }
        catch (InvalidDataException) when (backend == AudioBackend.Auto)
        {
            // FFmpeg won't take the setup, or can't decode the first page: NVorbis may.
            return null;
        }
    }

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

        // The last page ending a packet at or before the pre-roll point.
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

        // Decoding starts a page before it, so that its granule position anchors what's decoded; failing that, at the start.
        if (found < 1 || _seekPoints[found - 1].Granule <= _streamStart)
        {
            _ogg.Restart(_firstAudioPage, skipEndedOnPage: false);
            _nextPosition = _streamStart;
            _anchorPage = -1;
        }
        else
        {
            _ogg.Restart(_seekPoints[found - 1].Page, skipEndedOnPage: true);
            (_anchorPage, _anchorGranule) = _seekPoints[found];
        }
    }

    /// <summary>Decodes the next packet into the buffer, dropping what comes before <see cref="_discardUntil"/>.</summary>
    /// <returns><see langword="false"/> at the end of the stream.</returns>
    private bool DecodeNextPacket()
    {
        if (!_ogg.TryReadPacket(out var packet))
            return false;

        // A packet ending past the anchor page: what was decoded before it ended at that page's granule position.
        if (_anchorPage >= 0 && _ogg.LastPacketPage > _anchorPage)
        {
            _nextPosition = _anchorGranule;
            _anchorPage = -1;
        }

        int frames = _decoder.Decode(packet, _pcm);

        // Before the anchor, where the samples fall isn't known, but it's before the target: they're dropped.
        if (_anchorPage >= 0)
        {
            _bufferFrames = 0;
            return true;
        }

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

        return true;
    }

    /// <summary>Whether <paramref name="packet"/> is a Vorbis header packet of <paramref name="type"/> (§4.2.1).</summary>
    private static bool IsHeader(ReadOnlySpan<byte> packet, byte type) =>
        packet.Length >= 7 && packet[0] == type && packet[1..7].SequenceEqual("vorbis"u8);

    /// <summary>
    /// The three header packets as FFmpeg's decoder takes them: the byte 2, the first two packets' lengths
    /// Xiph-laced (a 255 for every whole 255, then the rest), then the three packets back to back.
    /// </summary>
    internal static byte[] Extradata(byte[] identification, byte[] comments, ReadOnlySpan<byte> setup)
    {
        int lacing = (identification.Length / 255) + 1 + (comments.Length / 255) + 1;
        var extradata = new byte[1 + lacing + identification.Length + comments.Length + setup.Length];
        int at = 0;
        extradata[at++] = 2;
        foreach (int length in (ReadOnlySpan<int>)[identification.Length, comments.Length])
        {
            for (int left = length; left >= 255; left -= 255)
                extradata[at++] = 255;
            extradata[at++] = (byte)(length % 255);
        }

        identification.CopyTo(extradata, at);
        at += identification.Length;
        comments.CopyTo(extradata, at);
        at += comments.Length;
        setup.CopyTo(extradata.AsSpan(at));
        return extradata;
    }
}
