using System.Buffers.Binary;
using System.Text;

namespace ChartPacker.Core.Charts;

/// <summary>A MIDI event with its own bytes: the status, then its data.</summary>
internal readonly record struct MidiMessage(long Tick, byte[] Bytes)
{
    public byte Status => Bytes[0];

    public bool IsEndOfTrack => Bytes is [0xFF, MidiFile.EndOfTrack, ..];

    /// <summary>The event <paramref name="e"/> of the file <paramref name="bytes"/>, copied out.</summary>
    public static MidiMessage Of(byte[] bytes, MidiEvent e)
    {
        byte[] data = new byte[1 + e.DataLength];
        data[0] = e.Status;
        bytes.AsSpan(e.DataStart, e.DataLength).CopyTo(data.AsSpan(1));
        return new MidiMessage(e.Tick, data);
    }

    public static MidiMessage NoteOn(long tick, int pitch, int velocity = 100) => new(tick, [0x90, (byte)pitch, (byte)velocity]);

    public static MidiMessage NoteOff(long tick, int pitch) => new(tick, [0x80, (byte)pitch, 0]);

    public static MidiMessage Meta(long tick, byte type, ReadOnlySpan<byte> data)
    {
        using var buffer = new MemoryStream(data.Length + 6);
        buffer.WriteByte(0xFF);
        buffer.WriteByte(type);
        MidiFile.WriteVlq(buffer, data.Length);
        buffer.Write(data);
        return new MidiMessage(tick, buffer.ToArray());
    }

    public static MidiMessage Text(long tick, byte type, string text) => Meta(tick, type, Encoding.Latin1.GetBytes(text));

    /// <summary>A Phase Shift sysex event: <c>'P' 'S' 0 0</c>, the difficulty, the kind and on or off.</summary>
    public static MidiMessage PhaseShift(long tick, int difficulty, int kind, bool on) =>
        new(tick, [0xF0, 8, 0x50, 0x53, 0, 0, (byte)difficulty, (byte)kind, (byte)(on ? 1 : 0), 0xF7]);
}

/// <summary>Writes Standard MIDI File tracks and files, without running status.</summary>
internal static class MidiWriter
{
    /// <summary>Writes an <c>MTrk</c> of <paramref name="events"/> of the file <paramref name="bytes"/>, which are in tick order.</summary>
    public static void WriteTrack(Stream output, byte[] bytes, IEnumerable<MidiEvent> events)
    {
        using var track = new MemoryStream();
        long tick = 0;
        foreach (var e in events)
        {
            MidiFile.WriteVlq(track, e.Tick - tick);
            tick = e.Tick;
            track.WriteByte(e.Status);
            track.Write(bytes, e.DataStart, e.DataLength);
        }

        WriteChunk(output, track);
    }

    /// <summary>
    /// Writes an <c>MTrk</c> of <paramref name="messages"/>, sorted by tick with ties kept in order,
    /// and ended by a single End-of-Track no earlier than the last of them.
    /// </summary>
    public static void WriteTrack(Stream output, IEnumerable<MidiMessage> messages)
    {
        var placed = messages.Where(m => !m.IsEndOfTrack).OrderBy(m => m.Tick).ToList();
        long end = Math.Max(0, placed.Count > 0 ? placed[^1].Tick : 0);

        using var track = new MemoryStream();
        long tick = 0;
        foreach (var m in placed.Append(new MidiMessage(end, [0xFF, MidiFile.EndOfTrack, 0])))
        {
            MidiFile.WriteVlq(track, m.Tick - tick);
            tick = m.Tick;
            track.Write(m.Bytes);
        }

        WriteChunk(output, track);
    }

    /// <summary>A format-1 file of <paramref name="division"/> ticks per beat, with the given <c>MTrk</c> chunks, headers included.</summary>
    public static byte[] WriteFile(int division, IReadOnlyList<byte[]> trackChunks)
    {
        using var output = new MemoryStream(14 + trackChunks.Sum(c => c.Length));
        Span<byte> header = stackalloc byte[14];
        "MThd"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 6);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], checked((ushort)trackChunks.Count));
        BinaryPrimitives.WriteUInt16BigEndian(header[12..], checked((ushort)division));
        output.Write(header);
        foreach (byte[] chunk in trackChunks)
            output.Write(chunk);
        return output.ToArray();
    }

    /// <summary>An <c>MTrk</c> chunk of <paramref name="messages"/>; see <see cref="WriteTrack(Stream, IEnumerable{MidiMessage})"/>.</summary>
    public static byte[] Track(IEnumerable<MidiMessage> messages)
    {
        using var output = new MemoryStream();
        WriteTrack(output, messages);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, MemoryStream track)
    {
        Span<byte> header = stackalloc byte[8];
        "MTrk"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)track.Length);
        output.Write(header);
        track.WriteTo(output);
    }
}
