using System.Buffers.Binary;
using ChartShared.Ffmpeg;
using NLayer;

namespace ChartShared.Audio.Decoding;

/// <summary>
/// Decodes MPEG audio (MP3). The frames are found and indexed here, and each is decoded by FFmpeg's
/// float decoder for its layer or by <see href="https://github.com/naudio/NLayer">NLayer</see>'s frame
/// decoder (see <see cref="AudioBackend"/>).
/// </summary>
/// <remarks>
/// <para>The frame index makes seeking exact: decoding restarts a few frames before the target,
/// so the bit reservoir and the filter bank have filled by the time the target is reached, and
/// the frames before it are discarded.</para>
/// <para>When the first frame is a Xing/Info frame with a LAME tag, the encoder delay and padding
/// it records are trimmed, plus the decoder's own delay of 529 samples, as mpg123 and FFmpeg do,
/// so the audio lines up with other formats' encodings of the same song. Without the tag nothing
/// is trimmed.</para>
/// </remarks>
public sealed class Mp3Source : IPcmSource
{
    // The synthesis filter bank's delay, which every decoder adds (528 + 1 samples).
    private const int DecoderDelay = 529;

    // Frames decoded and thrown away before a seek target, so the bit reservoir and the filter
    // bank are full again when it's reached. By testing, NLayer decodes the target exactly after
    // 8; 10 leaves a margin.
    private const int PreRollFrames = 10;

    // How far into the file to look for the first frame, past junk the ID3 tag doesn't cover.
    private const int MaxSyncSearch = 64 * 1024;

    private readonly Stream _stream;
    private readonly IPacketDecoder _decoder;
    private readonly List<long> _frames = [];
    private readonly MpegFrameHeader _format;
    private readonly List<MpegFrameHeader> _headers = [];

    // Samples per channel trimmed from the start, and the stream's length after trimming both ends.
    private readonly long _skip;
    private readonly long _length;

    private readonly float[] _pcm;

    // The frame being decoded, read whole; grown to the longest frame, not allocated for each.
    private byte[] _frameBytes = [];
    private int _bufferOffset;
    private int _bufferFrames;
    private int _nextFrame;

    // The position, in samples from the first decoded one, of the frame at _bufferOffset.
    private long _bufferPosition;
    private long _discardUntil;

    /// <param name="backend">What decodes the frames.</param>
    /// <exception cref="InvalidDataException">No MPEG audio frames were found.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="backend"/> is <see cref="AudioBackend.Ffmpeg"/>, and FFmpeg can't decode the stream.</exception>
    public Mp3Source(Stream stream, AudioBackend backend = AudioBackend.Auto)
        : this(stream, backend, FfmpegAudioDecoder.IsAvailable)
    {
    }

    internal Mp3Source(Stream stream, AudioBackend backend, Func<string, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;

        long offset = SkipId3v2(stream);
        long end = stream.Length;

        // Trailing tags (ID3v1, APE) aren't frames; the scan stops at the first thing that isn't.
        offset = FindFirstFrame(stream, offset, end) ?? throw new InvalidDataException("No MPEG audio frames were found.");

        Span<byte> bytes = stackalloc byte[MpegFrame.HeaderLength];
        MpegFrameHeader? first = null;
        while (offset + MpegFrame.HeaderLength <= end)
        {
            stream.Position = offset;
            stream.ReadExactly(bytes);
            if (MpegFrame.ParseHeader(bytes) is not { } header || (first is not null && !first.IsCompatible(header)) || offset + header.FrameLength > end)
                break;

            first ??= header;
            _frames.Add(offset);
            _headers.Add(header);
            offset += header.FrameLength;
        }

        _format = first ?? throw new InvalidDataException("No MPEG audio frames were found.");
        _pcm = new float[_format.SampleCount * _format.Channels];

        // The Xing/Info frame is silent; it isn't audio.
        var gapless = ReadGaplessInfo(stream, _frames[0], _format);
        if (gapless is not null)
        {
            _frames.RemoveAt(0);
            _headers.RemoveAt(0);
        }

        if (_frames.Count == 0)
            throw new InvalidDataException("No MPEG audio frames were found.");

        long decoded = (long)_frames.Count * _format.SampleCount;
        if (gapless is { Delay: { } delay, Padding: { } padding })
        {
            _skip = delay + DecoderDelay;
            _length = Math.Max(0, decoded - delay - padding);
        }
        else
        {
            _length = decoded;
        }

        // Made last, once nothing else can throw, so that a stream that isn't MP3 leaves nothing to free.
        string decoder = DecoderName(_format.Layer);
        IPacketDecoder? ffmpeg = AudioDecoder.UsesFfmpeg(backend, decoder, _format.Channels, isAvailable)
            ? FfmpegPacketDecoder.Open(backend, decoder, _format.Channels, [], trimsStart: false, errorsAreSilence: true)
            : null;
        _decoder = ffmpeg ?? new NLayerDecoder();
        Rewind(0);
    }

    public int SampleRate => _format.SampleRate;

    public int Channels => _format.Channels;

    public long LengthFrames => _length;

    public int ReadFrames(Span<float> buffer)
    {
        int wanted = buffer.Length / Channels;
        int written = 0;
        while (written < wanted)
        {
            if (_bufferFrames > 0)
            {
                long toEnd = _skip + _length - _bufferPosition;
                if (toEnd <= 0)
                {
                    _bufferFrames = 0;
                    _nextFrame = _frames.Count;
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

            if (!DecodeNextFrame())
                break;
        }

        return written;
    }

    public void SeekToFrame(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        Rewind(Math.Min(frame, _length));
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _stream.Dispose();
    }

    /// <summary>FFmpeg's float decoder for <paramref name="layer"/>.</summary>
    internal static string DecoderName(MpegLayer layer) => layer switch
    {
        MpegLayer.LayerI => "mp1float",
        MpegLayer.LayerII => "mp2float",
        _ => "mp3float",
    };

    /// <summary>Restarts decoding far enough before output frame <paramref name="frame"/> to reach it exactly.</summary>
    private void Rewind(long frame)
    {
        long target = _skip + frame;
        int index = (int)Math.Min(target / _format.SampleCount, _frames.Count);
        _nextFrame = Math.Max(0, index - PreRollFrames);
        _bufferPosition = (long)_nextFrame * _format.SampleCount;
        _bufferFrames = 0;
        _discardUntil = target;
        _decoder.Reset();
    }

    private bool DecodeNextFrame()
    {
        if (_nextFrame >= _frames.Count)
            return false;

        var header = _headers[_nextFrame];
        if (_frameBytes.Length < header.FrameLength)
            _frameBytes = new byte[Math.Max(header.FrameLength, _frameBytes.Length * 2)];
        var bytes = _frameBytes.AsSpan(0, header.FrameLength);
        _stream.Position = _frames[_nextFrame];
        _stream.ReadExactly(bytes);
        int frames = _decoder.Decode(bytes, _pcm);
        _nextFrame++;

        // A frame that can't be decoded (its bit reservoir is missing after a seek, say) is silence.
        if (frames <= 0)
        {
            frames = _format.SampleCount;
            _pcm.AsSpan(0, frames * Channels).Clear();
        }

        _bufferOffset = 0;
        _bufferFrames = frames;
        if (_bufferPosition < _discardUntil)
        {
            int drop = (int)Math.Min(frames, _discardUntil - _bufferPosition);
            _bufferOffset = drop;
            _bufferFrames -= drop;
            _bufferPosition += drop;
        }

        return true;
    }

    /// <summary>Skips an ID3v2 tag at the start (ID3v2.4 §3.1), returning where the audio starts.</summary>
    private static long SkipId3v2(Stream stream)
    {
        Span<byte> header = stackalloc byte[10];
        if (stream.Length < header.Length)
            return 0;

        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..3].SequenceEqual("ID3"u8) || (header[6] | header[7] | header[8] | header[9]) >= 0x80)
            return 0;

        // A syncsafe size (seven bits per byte), not counting the header or an optional footer.
        int size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
        bool hasFooter = (header[5] & 0x10) != 0;
        return 10 + size + (hasFooter ? 10 : 0);
    }

    /// <summary>The first offset from <paramref name="start"/> where two consecutive, matching frame headers are found.</summary>
    private static long? FindFirstFrame(Stream stream, long start, long end)
    {
        int length = (int)Math.Min(MaxSyncSearch + 8192, end - start);
        if (length < MpegFrame.HeaderLength)
            return null;

        byte[] window = new byte[length];
        stream.Position = start;
        stream.ReadExactly(window);

        for (int i = 0; i + MpegFrame.HeaderLength <= Math.Min(window.Length, MaxSyncSearch); i++)
        {
            if (MpegFrame.ParseHeader(window.AsSpan(i)) is not { } header)
                continue;

            // One header can be a false sync in other data; the next frame must follow it.
            long next = i + header.FrameLength;
            if (next + MpegFrame.HeaderLength > window.Length)
            {
                if (start + next == end)
                    return start + i;
                continue;
            }

            if (MpegFrame.ParseHeader(window.AsSpan((int)next)) is { } second && header.IsCompatible(second))
                return start + i;
        }

        return null;
    }

    /// <summary>
    /// Reads a Xing or Info frame's LAME tag, which records the encoder's delay and padding in
    /// samples.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the frame is ordinary audio; otherwise the delay and padding,
    /// which are <see langword="null"/> when there's no LAME tag to give them.
    /// </returns>
    private static (int? Delay, int? Padding)? ReadGaplessInfo(Stream stream, long offset, MpegFrameHeader header)
    {
        byte[] frame = new byte[header.FrameLength];
        stream.Position = offset;
        stream.ReadExactly(frame);

        // The tag follows the side information, whose size depends on the version and channels.
        int sideInfo = header.Version == MpegVersion.Version1
            ? (header.Channels == 1 ? 17 : 32)
            : (header.Channels == 1 ? 9 : 17);
        int tag = MpegFrame.HeaderLength + (header.HasCrc ? 2 : 0) + sideInfo;
        if (header.Layer != MpegLayer.LayerIII || tag + 8 > frame.Length)
            return null;

        var id = frame.AsSpan(tag, 4);
        if (!id.SequenceEqual("Xing"u8) && !id.SequenceEqual("Info"u8))
            return null;

        uint flags = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(tag + 4));
        int lame = tag + 8
            + ((flags & 1) != 0 ? 4 : 0)    // frame count
            + ((flags & 2) != 0 ? 4 : 0)    // byte count
            + ((flags & 4) != 0 ? 100 : 0)  // seek table
            + ((flags & 8) != 0 ? 4 : 0);   // quality

        // The LAME extension: a nine-byte encoder name, then twelve bits each of delay and padding at +21.
        if (lame + 24 > frame.Length || !(frame.AsSpan(lame, 4).SequenceEqual("LAME"u8) || frame.AsSpan(lame, 4).SequenceEqual("Lavf"u8) || frame.AsSpan(lame, 4).SequenceEqual("Lavc"u8)))
            return (null, null);

        int delay = (frame[lame + 21] << 4) | (frame[lame + 22] >> 4);
        int padding = ((frame[lame + 22] & 0x0F) << 8) | frame[lame + 23];
        return (delay, padding);
    }
}
