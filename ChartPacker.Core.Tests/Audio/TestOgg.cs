using System.Buffers.Binary;
using ChartPacker.Audio.Decoding;
using ChartPacker.Ffmpeg;

namespace ChartPacker.Core.Tests.Audio;

/// <summary>Ogg streams rebuilt for the decoder tests, written from RFC 3533 (The Ogg Encapsulation Format).</summary>
internal static class TestOgg
{
    /// <summary>
    /// Ogg Vorbis <paramref name="ogg"/> rewritten with <paramref name="packetsPerPage"/> audio packets a page, so a
    /// one-second fixture, which is a single audio page, has pages to seek between. The audio is the same.
    /// </summary>
    /// <remarks>
    /// Each page's granule position is the samples decoded to the end of its last packet, counted by FFmpeg's
    /// decoder, which gives none for the first packet (Vorbis I §A.2). The last page keeps the original's, which
    /// trims the end. Needs FFmpeg's <c>vorbis</c> decoder; see <see cref="TestAudio.RequireBackend"/>.
    /// </remarks>
    public static byte[] RepaginateVorbis(byte[] ogg, int packetsPerPage)
    {
        var reader = new OggPageReader(new MemoryStream(ogg));
        long endGranule = reader.Pages[^1].Granule;
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(14));

        reader.Restart(0, skipEndedOnPage: false);
        var packets = new List<byte[]>();
        while (reader.TryReadPacket(out var packet))
            packets.Add(packet.ToArray());

        int channels = packets[0][11];
        using var decoder = new FfmpegAudioDecoder("vorbis", channels, OggVorbisSource.Extradata(packets[0], packets[1], packets[2]), trimsStart: true);
        var pcm = new float[8192 * channels];

        using var result = new MemoryStream();
        uint sequence = 0;

        // The identification header alone on the first page; the comment and setup headers end the second.
        WritePage(result, serial, sequence++, 0x02, 0, [packets[0]]);
        WritePage(result, serial, sequence++, 0x00, 0, [packets[1], packets[2]]);

        long granule = 0;
        for (int i = 3; i < packets.Count; i += packetsPerPage)
        {
            var page = packets.Skip(i).Take(packetsPerPage).ToList();
            foreach (var packet in page)
                granule += decoder.Decode(packet, pcm);

            bool last = i + packetsPerPage >= packets.Count;
            WritePage(result, serial, sequence++, last ? (byte)0x04 : (byte)0x00, last ? endGranule : granule, page);
        }

        return result.ToArray();
    }

    /// <summary>One page holding whole <paramref name="packets"/>.</summary>
    private static void WritePage(Stream output, uint serial, uint sequence, byte headerType, long granule, IReadOnlyList<byte[]> packets)
    {
        var lacing = new List<byte>();
        foreach (var packet in packets)
        {
            for (int left = packet.Length; left >= 255; left -= 255)
                lacing.Add(255);
            lacing.Add((byte)(packet.Length % 255));
        }

        if (lacing.Count > 255)
            throw new ArgumentException("The packets don't fit on one page.", nameof(packets));

        var page = new byte[27 + lacing.Count + packets.Sum(p => p.Length)];
        "OggS"u8.CopyTo(page);
        page[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);

        int at = 27 + lacing.Count;
        foreach (var packet in packets)
        {
            packet.CopyTo(page, at);
            at += packet.Length;
        }

        // The checksum is over the whole page with its own field zeroed.
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
        output.Write(page);
    }

    /// <summary>Ogg's CRC-32: polynomial 0x04C11DB7, most significant bit first, starting from 0, not inverted.</summary>
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        return crc;
    }
}
