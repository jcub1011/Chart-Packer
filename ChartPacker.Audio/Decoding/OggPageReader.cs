using System.Buffers.Binary;

namespace ChartPacker.Audio.Decoding;

/// <summary>One page of an Ogg logical stream (RFC 3533 §6).</summary>
/// <param name="Offset">Where the page starts in the file.</param>
/// <param name="BodyOffset">Where its body (the packet data) starts.</param>
/// <param name="Granule">The granule position: for Opus, the sample count at the end of the last packet that ends on this page; -1 when none does.</param>
/// <param name="Lacing">The segment table: each packet is a run of 255s ended by a smaller value.</param>
internal readonly record struct OggPage(long Offset, long BodyOffset, byte HeaderType, long Granule, byte[] Lacing)
{
    /// <summary>The first packet on the page continues one from the page before.</summary>
    public bool IsContinued => (HeaderType & 0x01) != 0;

    public int BodyLength => Lacing.Sum(l => (int)l);
}

/// <summary>
/// Indexes the pages of the first logical stream of an Ogg file, and reassembles its packets from
/// them. Written from RFC 3533 (The Ogg Encapsulation Format).
/// </summary>
/// <remarks>
/// The whole file's page headers are read once, up front, so a seek is a search of the index
/// rather than of the file. Pages of other, multiplexed streams are skipped. A header that isn't
/// where the previous page said it would be ends the index: a damaged file plays up to there.
/// </remarks>
internal sealed class OggPageReader
{
    private const int HeaderLength = 27;

    private readonly Stream _stream;
    private readonly List<OggPage> _pages = [];

    // The packet being read: bytes gathered so far, where it continues, and whether it's a
    // fragment whose start wasn't read, to be dropped.
    private byte[] _packet = new byte[8192];
    private int _packetLength;
    private bool _dropping;
    private int _pageIndex;
    private int _segment;
    private long _bodyPosition;
    private byte[] _body = [];

    /// <exception cref="InvalidDataException">The stream doesn't start with an Ogg page.</exception>
    public OggPageReader(Stream stream)
    {
        _stream = stream;

        long offset = 0;
        uint? serial = null;
        Span<byte> header = stackalloc byte[HeaderLength];
        while (offset + HeaderLength <= stream.Length)
        {
            stream.Position = offset;
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("OggS"u8) || header[4] != 0)
                break;

            byte headerType = header[5];
            long granule = BinaryPrimitives.ReadInt64LittleEndian(header[6..]);
            uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
            byte[] lacing = new byte[header[26]];
            if (offset + HeaderLength + lacing.Length > stream.Length)
                break;
            stream.ReadExactly(lacing);

            var page = new OggPage(offset, offset + HeaderLength + lacing.Length, headerType, granule, lacing);
            if (page.BodyOffset + page.BodyLength > stream.Length)
                break;

            serial ??= pageSerial;
            if (pageSerial == serial)
            {
                _pages.Add(page);
                if ((headerType & 0x04) != 0)
                    break; // End of stream; anything after it is a chained stream, not played.
            }

            offset = page.BodyOffset + page.BodyLength;
        }

        if (_pages.Count == 0)
            throw new InvalidDataException("Not an Ogg file.");
    }

    public IReadOnlyList<OggPage> Pages => _pages;

    /// <summary>The index of the page the last packet read ended on.</summary>
    public int LastPacketPage { get; private set; }

    /// <summary>
    /// Continues reading packets at page <paramref name="pageIndex"/>. With
    /// <paramref name="skipEndedOnPage"/>, the packets that end on that page are skipped, so the
    /// next packet read is the first to end after the page's granule position.
    /// </summary>
    public void Restart(int pageIndex, bool skipEndedOnPage)
    {
        _pageIndex = pageIndex;
        _segment = 0;
        _packetLength = 0;
        _body = [];

        // A packet that starts on an earlier page can't be put together, so its end is dropped.
        _dropping = pageIndex < _pages.Count && _pages[pageIndex].IsContinued;

        if (skipEndedOnPage && pageIndex < _pages.Count)
        {
            LoadPage();
            var lacing = _pages[_pageIndex].Lacing;
            int lastEnd = Array.FindLastIndex(lacing, l => l < 255);
            for (; _segment <= lastEnd; _segment++)
                _bodyPosition += lacing[_segment];
            _dropping = false;
        }
    }

    /// <summary>The next whole packet, or <see langword="false"/> at the end of the stream.</summary>
    /// <remarks>The packet is only valid until the next call.</remarks>
    public bool TryReadPacket(out ReadOnlySpan<byte> packet)
    {
        while (_pageIndex < _pages.Count)
        {
            if (_body.Length == 0 && _segment == 0)
                LoadPage();

            var page = _pages[_pageIndex];
            while (_segment < page.Lacing.Length)
            {
                int length = page.Lacing[_segment++];
                if (!_dropping)
                    Append(_body.AsSpan((int)(_bodyPosition - page.BodyOffset), length));
                _bodyPosition += length;

                if (length < 255)
                {
                    if (_dropping)
                    {
                        _dropping = false;
                        continue;
                    }

                    LastPacketPage = _pageIndex;
                    packet = _packet.AsSpan(0, _packetLength);
                    _packetLength = 0;
                    return true;
                }
            }

            _pageIndex++;
            _segment = 0;
            _body = [];
        }

        packet = default;
        return false;
    }

    private void LoadPage()
    {
        var page = _pages[_pageIndex];
        _body = new byte[page.BodyLength];
        _stream.Position = page.BodyOffset;
        _stream.ReadExactly(_body);
        _bodyPosition = page.BodyOffset;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_packetLength + data.Length > _packet.Length)
            Array.Resize(ref _packet, Math.Max(_packet.Length * 2, _packetLength + data.Length));
        data.CopyTo(_packet.AsSpan(_packetLength));
        _packetLength += data.Length;
    }
}
