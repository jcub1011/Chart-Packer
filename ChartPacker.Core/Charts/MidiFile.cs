using System.Buffers.Binary;
using System.Text;

namespace ChartPacker.Core.Charts;

/// <summary>A chunk of a Standard MIDI File.</summary>
/// <param name="Start">Where its 8-byte header starts.</param>
/// <param name="DataStart">Where its data starts, after the header.</param>
internal readonly record struct MidiChunk(string Id, int Start, int DataStart, int Length)
{
    public int End => DataStart + Length;
}

/// <summary>
/// One event of a track. Its bytes are <see cref="Status"/> followed by the file's bytes from
/// <see cref="DataStart"/> for <see cref="DataLength"/>, whether the file stored the status or
/// relied on running status.
/// </summary>
internal readonly record struct MidiEvent(long Tick, byte Status, int DataStart, int DataLength)
{
    public bool IsMeta => Status == 0xFF;

    public bool IsSysex => Status is 0xF0 or 0xF7;

    public int Kind => Status & 0xF0;
}

/// <summary>Reads the structure of a Standard MIDI File: its chunks, and the events of its tracks.</summary>
internal static class MidiFile
{
    public const int EndOfTrack = 0x2F;

    // Throws on bytes that aren't UTF-8, so text that isn't is read as Latin-1 instead.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The bytes of a text event: Latin-1, as MIDI text has long been written, when every character
    /// is in it; otherwise UTF-8, so no character is lost.
    /// </summary>
    public static byte[] EncodeText(string text) =>
        text.All(c => c <= 'ÿ') ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text);

    /// <summary>A text event's text: UTF-8 when its bytes are, otherwise Latin-1. ASCII reads the same either way.</summary>
    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>The header's fields and every chunk, in file order.</summary>
    /// <exception cref="ChartFormatException">The file isn't a Standard MIDI File, or uses SMPTE timing.</exception>
    public static (int Format, int Division, List<MidiChunk> Chunks) ReadChunks(byte[] bytes)
    {
        var chunks = new List<MidiChunk>();
        int pos = 0;
        while (pos + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 4));
            // What's left isn't a chunk: junk after the last track, which other chart readers ignore too.
            if (length > (uint)(bytes.Length - pos - 8))
                break;
            chunks.Add(new MidiChunk(id, pos, pos + 8, (int)length));
            pos += 8 + (int)length;
        }

        if (chunks.Count == 0 || chunks[0].Id != "MThd" || chunks[0].Length < 6)
            throw new ChartFormatException("The file isn't a MIDI file.");

        var header = bytes.AsSpan(chunks[0].DataStart);
        int format = BinaryPrimitives.ReadUInt16BigEndian(header);
        int division = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if ((division & 0x8000) != 0 || division == 0)
            throw new ChartFormatException("MIDI files timed in SMPTE frames aren't supported.");

        return (format, division, chunks);
    }

    /// <summary>The events of the track <paramref name="chunk"/>, with their ticks from the start of the track.</summary>
    /// <exception cref="ChartFormatException">The track is malformed.</exception>
    public static List<MidiEvent> ReadEvents(byte[] bytes, MidiChunk chunk)
    {
        // Note events take 3 or 4 bytes with running status, so this is about enough, and seldom grows.
        var events = new List<MidiEvent>(chunk.Length / 3);
        var reader = new EventReader(chunk);
        while (reader.Next(bytes) is { } e)
            events.Add(e);

        return events;
    }

    /// <summary>
    /// The name of the track <paramref name="chunk"/>, from its first track-name event, as <see cref="NameOf(byte[], List{MidiEvent})"/>
    /// gives it, reading only as far as that event.
    /// </summary>
    /// <exception cref="ChartFormatException">The track is malformed before its name.</exception>
    public static string? NameOf(byte[] bytes, MidiChunk chunk)
    {
        var reader = new EventReader(chunk);
        while (reader.Next(bytes) is { } e)
        {
            if (TextOf(bytes, e, 0x03) is { } name)
                return name.Trim();
        }

        return null;
    }

    /// <summary>Reads the whole track <paramref name="chunk"/>, as <see cref="ReadEvents"/> does, without keeping its events.</summary>
    /// <exception cref="ChartFormatException">The track is malformed.</exception>
    public static void Check(byte[] bytes, MidiChunk chunk)
    {
        var reader = new EventReader(chunk);
        while (reader.Next(bytes) is not null)
        {
        }
    }

    /// <summary>Reads a track's events one at a time.</summary>
    internal struct EventReader(MidiChunk chunk)
    {
        private readonly int _end = chunk.End;
        private int _pos = chunk.DataStart;
        private long _tick;
        private byte _running;

        /// <summary>The next event, or <see langword="null"/> at the end of the track.</summary>
        /// <exception cref="ChartFormatException">The track is malformed.</exception>
        public MidiEvent? Next(byte[] bytes)
        {
            if (_pos >= _end)
                return null;

            _tick += ReadVlq(bytes, ref _pos, _end);
            if (_pos >= _end)
                throw Malformed();

            byte status;
            if (bytes[_pos] >= 0x80)
            {
                // Sysex and meta events should cancel running status, but charting tools rely on it
                // lasting past them, as other chart readers allow.
                status = bytes[_pos++];
                if (status < 0xF0)
                    _running = status;
            }
            else if (_running != 0)
            {
                status = _running;
            }
            else
            {
                throw Malformed();
            }

            int dataStart = _pos;
            switch (status)
            {
                case < 0xF0:
                    _pos += (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                    break;
                case 0xF0 or 0xF7:
                {
                    long length = ReadVlq(bytes, ref _pos, _end);
                    _pos += (int)length;
                    break;
                }

                case 0xFF:
                {
                    _pos++;
                    long length = ReadVlq(bytes, ref _pos, _end);
                    _pos += (int)length;
                    break;
                }
                default:
                    throw Malformed();
            }

            if (_pos > _end)
                throw Malformed();
            return new MidiEvent(_tick, status, dataStart, _pos - dataStart);
        }
    }

    /// <summary>A meta event's type, and where its data is.</summary>
    public static (int Type, int Start, int Length) MetaOf(byte[] bytes, MidiEvent e)
    {
        int pos = e.DataStart + 1;
        int end = e.DataStart + e.DataLength;
        int length = (int)ReadVlq(bytes, ref pos, end);
        return (bytes[e.DataStart], pos, length);
    }

    /// <summary>A sysex event's data, after its length.</summary>
    public static ReadOnlySpan<byte> SysexOf(byte[] bytes, MidiEvent e)
    {
        int pos = e.DataStart;
        int end = e.DataStart + e.DataLength;
        int length = (int)ReadVlq(bytes, ref pos, end);
        return bytes.AsSpan(pos, length);
    }

    /// <summary>A text meta event's text, or <see langword="null"/> for any other event; see <see cref="DecodeText"/>.</summary>
    public static string? TextOf(byte[] bytes, MidiEvent e, int type)
    {
        if (!e.IsMeta)
            return null;
        var (t, start, length) = MetaOf(bytes, e);
        return t == type ? DecodeText(bytes.AsSpan(start, length)) : null;
    }

    /// <summary>The name of the track, from its first track-name event.</summary>
    public static string? NameOf(byte[] bytes, List<MidiEvent> events)
    {
        foreach (var e in events)
        {
            if (TextOf(bytes, e, 0x03) is { } name)
                return name.Trim();
        }

        return null;
    }

    public static long ReadVlq(byte[] bytes, ref int pos, int end)
    {
        long value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (pos >= end)
                throw Malformed();
            byte b = bytes[pos++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                return value;
        }

        throw Malformed();
    }

    public static void WriteVlq(Stream output, long value)
    {
        Span<byte> buffer = stackalloc byte[5];
        int i = buffer.Length;
        buffer[--i] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0)
            buffer[--i] = (byte)(0x80 | (value & 0x7F));
        output.Write(buffer[i..]);
    }

    private static ChartFormatException Malformed() => new("A MIDI track is malformed.");
}
