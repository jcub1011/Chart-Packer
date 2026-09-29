using NLayer;

namespace ChartPacker.Audio.Decoding;

/// <summary>
/// One MPEG audio frame (ISO/IEC 11172-3 §2.4.1), read whole from the file, for NLayer's
/// <see cref="MpegFrameDecoder"/>. <see cref="ReadBits"/> reads the frame after its header and
/// CRC, most significant bit first.
/// </summary>
/// <remarks>One instance is loaded with each frame in turn, so its buffer is reused rather than allocated per frame.</remarks>
internal sealed class MpegFrame : IMpegFrame
{
    public const int HeaderLength = 4;

    // Kilobits per second by version (1 or 2/2.5), layer (I, II, III) and bitrate index.
    private static readonly int[][][] Bitrates =
    [
        [
            [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
            [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
            [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
        ],
        [
            [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
            [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
            [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
        ],
    ];

    private static readonly int[] Version1Rates = [44100, 48000, 32000];

    private byte[] _data = [];
    private int _length;
    private int _bitPosition;

    /// <summary>The frame loaded last. Nothing's read before the first <see cref="Load"/>.</summary>
    public MpegFrameHeader Header { get; private set; } = null!;

    /// <summary>The whole frame, header included.</summary>
    public ReadOnlySpan<byte> Data => _data.AsSpan(0, _length);

    public int SampleRate => Header.SampleRate;

    public int SampleRateIndex => Header.SampleRateIndex;

    public int FrameLength => Header.FrameLength;

    public int BitRate => Header.BitRate;

    public MpegVersion Version => Header.Version;

    public MpegLayer Layer => Header.Layer;

    public MpegChannelMode ChannelMode => Header.ChannelMode;

    public int ChannelModeExtension => Header.ChannelModeExtension;

    public int SampleCount => Header.SampleCount;

    public int BitRateIndex => Header.BitRateIndex;

    public bool IsCopyrighted => Header.IsCopyrighted;

    public bool HasCrc => Header.HasCrc;

    public bool IsCorrupted => false;

    /// <summary>Parses the four header bytes at the start of <paramref name="bytes"/>.</summary>
    /// <returns>The header, or <see langword="null"/> when they aren't a valid one, including free-format bitrates, which aren't supported.</returns>
    public static MpegFrameHeader? ParseHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength || bytes[0] != 0xFF || (bytes[1] & 0xE0) != 0xE0)
            return null;

        int versionBits = (bytes[1] >> 3) & 3;
        int layerBits = (bytes[1] >> 1) & 3;
        int bitrateIndex = bytes[2] >> 4;
        int rateIndex = (bytes[2] >> 2) & 3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || rateIndex == 3)
            return null;

        var version = versionBits switch { 3 => MpegVersion.Version1, 2 => MpegVersion.Version2, _ => MpegVersion.Version25 };
        var layer = layerBits switch { 3 => MpegLayer.LayerI, 2 => MpegLayer.LayerII, _ => MpegLayer.LayerIII };
        int sampleRate = Version1Rates[rateIndex] / (version switch { MpegVersion.Version1 => 1, MpegVersion.Version2 => 2, _ => 4 });
        int bitrate = Bitrates[version == MpegVersion.Version1 ? 0 : 1][(int)layer - 1][bitrateIndex] * 1000;
        int padding = (bytes[2] >> 1) & 1;

        int sampleCount = layer switch
        {
            MpegLayer.LayerI => 384,
            MpegLayer.LayerII => 1152,
            _ => version == MpegVersion.Version1 ? 1152 : 576,
        };
        int frameLength = layer == MpegLayer.LayerI
            ? ((12 * bitrate / sampleRate) + padding) * 4
            : (sampleCount / 8 * bitrate / sampleRate) + padding;

        return new MpegFrameHeader(
            version, layer, (MpegChannelMode)(bytes[3] >> 6), (bytes[3] >> 4) & 3,
            sampleRate, rateIndex, bitrate, bitrateIndex, sampleCount, frameLength,
            HasCrc: (bytes[1] & 1) == 0, IsCopyrighted: ((bytes[3] >> 3) & 1) != 0);
    }

    /// <summary>Loads the frame <paramref name="data"/>, whose header is <paramref name="header"/>, in place of the last.</summary>
    public void Load(ReadOnlySpan<byte> data, MpegFrameHeader header)
    {
        if (_data.Length < data.Length)
            _data = new byte[Math.Max(data.Length, _data.Length * 2)];

        data.CopyTo(_data);
        _length = data.Length;
        Header = header;
        Reset();
    }

    public void Reset() => _bitPosition = (HeaderLength + (HasCrc ? 2 : 0)) * 8;

    public int ReadBits(int bitCount)
    {
        if (bitCount is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (_bitPosition + bitCount > _length * 8)
            throw new InvalidDataException("An MPEG frame ended early.");
        if (bitCount == 0)
            return 0;

        // Up to 32 bits from any bit offset span at most five bytes: they're gathered into a
        // 40-bit window, first byte highest, and the bits wanted are shifted out of it at once.
        // NLayer reads every field and Huffman code through here, so a bit at a time is slow.
        int index = _bitPosition >> 3;
        ulong window = 0;
        for (int i = index; i < index + 5; i++)
            window = (window << 8) | (i < _length ? _data[i] : 0u);

        int shift = 40 - (_bitPosition & 7) - bitCount;
        _bitPosition += bitCount;
        return (int)((window >> shift) & ((1UL << bitCount) - 1));
    }
}

/// <summary>The fields of an MPEG audio frame header, with the frame's derived sizes.</summary>
/// <param name="SampleRateIndex">The header's two-bit sample-rate field.</param>
/// <param name="BitRate">In bits per second.</param>
/// <param name="SampleCount">Samples per channel the frame decodes to.</param>
/// <param name="FrameLength">The whole frame's length in bytes, header included.</param>
internal sealed record MpegFrameHeader(
    MpegVersion Version,
    MpegLayer Layer,
    MpegChannelMode ChannelMode,
    int ChannelModeExtension,
    int SampleRate,
    int SampleRateIndex,
    int BitRate,
    int BitRateIndex,
    int SampleCount,
    int FrameLength,
    bool HasCrc,
    bool IsCopyrighted)
{
    public int Channels => ChannelMode == MpegChannelMode.Mono ? 1 : 2;

    /// <summary>Whether <paramref name="other"/> is a frame of the same stream: frames can differ only in bitrate, padding and stereo coding.</summary>
    public bool IsCompatible(MpegFrameHeader other) =>
        other.Version == Version && other.Layer == Layer && other.SampleRate == SampleRate && other.Channels == Channels;
}
