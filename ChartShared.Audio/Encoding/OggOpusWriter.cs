using System.Buffers.Binary;
using System.Text;

namespace ChartShared.Audio.Encoding;

/// <summary>
/// Writes an Ogg Opus stream (RFC 7845): the <c>OpusHead</c> and <c>OpusTags</c> headers,
/// then one audio packet per page with granule positions, so <see cref="Decoding.OpusSource"/>
/// reads back exactly the frames written.
/// </summary>
/// <remarks>
/// Each packet fits one page (Opus packets are at most a few KB). The last audio page is
/// marked end-of-stream when <see cref="Finish"/> patches it, since only then is it known
/// to be last. output must be readable, writable and seekable.
/// </remarks>
internal sealed class OggOpusWriter : IDisposable
{
    private const int HeaderLength = 27;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly Stream _output;
    private readonly uint _serial;
    private readonly int _channels;
    private readonly int _preSkip;
    private uint _sequence;
    private long _lastPageStart = -1;
    private bool _finished;
    private bool _hasAudio;

    /// <param name="output">Where the stream is written. Owned by the caller.</param>
    /// <param name="channels">1 for mono, 2 for stereo.</param>
    /// <param name="preSkip">Encoder lookahead in 48 kHz samples, stored in <c>OpusHead</c>.</param>
    /// <param name="inputSampleRate">The original sample rate, stored in <c>OpusHead</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channels"/> isn't 1 or 2.</exception>
    public OggOpusWriter(Stream output, int channels, int preSkip, int inputSampleRate)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "Only mono and stereo Opus streams are written.");
        ArgumentOutOfRangeException.ThrowIfNegative(preSkip);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputSampleRate, 1);

        _output = output;
        _channels = channels;
        _preSkip = preSkip;
        _serial = (uint)Random.Shared.Next(int.MinValue, int.MaxValue);

        WriteHeaders(inputSampleRate);
    }

    /// <summary>Writes one audio packet, ending at <paramref name="decodedFrames"/> decoded frames altogether.</summary>
    /// <param name="packet">The encoded Opus packet.</param>
    /// <param name="decodedFrames">How many frames precede the packet's end on the decoded timeline (pre-skip included), for its granule position.</param>
    public void WriteAudioPacket(ReadOnlySpan<byte> packet, long decodedFrames)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        ArgumentOutOfRangeException.ThrowIfNegative(decodedFrames);

        _lastPageStart = _output.Position;
        WritePage(headerType: 0x00, granule: decodedFrames, packet);
        _hasAudio = true;
    }

    /// <summary>
    /// Gives the last audio page its final granule position and marks it end-of-stream.
    /// The stream must hold at least one audio packet.
    /// </summary>
    /// <param name="finalGranule">The decoded timeline's end: pre-skip plus valid frames, trimming any encoder flush.</param>
    /// <exception cref="InvalidOperationException">No audio packet was written.</exception>
    public void Finish(long finalGranule)
    {
        if (_finished)
            return;
        if (!_hasAudio)
            throw new InvalidOperationException("An Ogg Opus stream needs at least one audio packet.");

        // Patch the last page: its final granule position, the end-of-stream flag, and a fresh checksum.
        _output.Position = _lastPageStart;
        byte[] header = new byte[HeaderLength];
        _output.ReadExactly(header);
        int segments = header[26];
        byte[] lacing = new byte[segments];
        _output.ReadExactly(lacing);
        int bodyLength = 0;
        foreach (byte segment in lacing)
            bodyLength += segment;
        byte[] body = new byte[bodyLength];
        _output.ReadExactly(body);

        byte[] page = new byte[HeaderLength + segments + bodyLength];
        header.CopyTo(page, 0);
        lacing.CopyTo(page, HeaderLength);
        body.CopyTo(page, HeaderLength + segments);
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), finalGranule);
        page[5] |= 0x04;
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Checksum(page));

        _output.Position = _lastPageStart;
        _output.Write(page, 0, page.Length);
        _finished = true;
    }

    public void Dispose()
    {
    }

    private void WriteHeaders(int inputSampleRate)
    {
        // RFC 7845 §5.1: the identification header.
        byte[] head = new byte[19];
        System.Text.Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1; // Version.
        head[9] = (byte)_channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)Math.Min(_preSkip, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)inputSampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), 0); // Output gain.
        head[18] = 0; // Mapping family 0: mono or stereo.
        WritePage(headerType: 0x02, granule: 0, head);

        // RFC 7845 §5.2: the comment header, with no user comments.
        byte[] vendor = System.Text.Encoding.UTF8.GetBytes("ChartPacker");
        byte[] tags = new byte[8 + 4 + vendor.Length + 4];
        System.Text.Encoding.ASCII.GetBytes("OpusTags").CopyTo(tags, 0);
        BinaryPrimitives.WriteInt32LittleEndian(tags.AsSpan(8), vendor.Length);
        vendor.CopyTo(tags, 12);
        BinaryPrimitives.WriteInt32LittleEndian(tags.AsSpan(12 + vendor.Length), 0);
        WritePage(headerType: 0x00, granule: 0, tags);
    }

    /// <summary>Writes one packet as one page, with <paramref name="headerType"/> flags and <paramref name="granule"/>.</summary>
    private void WritePage(byte headerType, long granule, ReadOnlySpan<byte> packet)
    {
        // A packet is runs of 255-byte segments ended by a smaller one; a multiple of 255
        // needs a zero-length segment to say where it ends (RFC 3533 §6).
        int full = packet.Length / 255;
        int segments = full + 1;
        if (segments > 255)
            throw new ArgumentException("The packet is too long for one Ogg page.", nameof(packet));

        byte[] page = new byte[HeaderLength + segments + packet.Length];
        System.Text.Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
        page[4] = 0; // Version.
        page[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), _serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _sequence++);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), 0); // Checksum, filled in below.
        page[26] = (byte)segments;
        for (int i = 0; i < full; i++)
            page[HeaderLength + i] = 255;
        page[HeaderLength + full] = (byte)(packet.Length % 255);
        packet.CopyTo(page.AsSpan(HeaderLength + segments));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Checksum(page));

        _output.Write(page, 0, page.Length);
    }

    private static uint Checksum(byte[] page)
    {
        uint crc = 0;
        foreach (byte b in page)
            crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int j = 0; j < 8; j++)
                r = (r << 1) ^ ((r & 0x80000000) != 0 ? 0x04C11DB7u : 0);
            table[i] = r;
        }

        return table;
    }
}
