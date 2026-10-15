using System.Buffers.Binary;
using System.Text;

namespace ChartPacker.Core.Tests.Charts;

/// <summary>Builds a format-1 MIDI file from tracks of events at absolute ticks.</summary>
internal sealed class MidiBuilder(int resolution = 480)
{
    private readonly List<List<(long Tick, byte[] Bytes)>> _tracks = [];

    /// <summary>Starts a track, with a track name when <paramref name="name"/> is given.</summary>
    public MidiBuilder Track(string? name = null)
    {
        _tracks.Add([]);
        if (name is not null)
            Meta(0, 0x03, Encoding.ASCII.GetBytes(name));
        return this;
    }

    public MidiBuilder Tempo(long tick, double bpm)
    {
        int us = (int)Math.Round(60_000_000 / bpm);
        return Meta(tick, 0x51, [(byte)(us >> 16), (byte)(us >> 8), (byte)us]);
    }

    public MidiBuilder TimeSignature(long tick, int numerator, int denominatorPower) =>
        Meta(tick, 0x58, [(byte)numerator, (byte)denominatorPower, 24, 8]);

    public MidiBuilder Text(long tick, string text) => Meta(tick, 0x01, Encoding.ASCII.GetBytes(text));

    /// <summary>A note from <paramref name="tick"/> held for <paramref name="length"/>, its end a note-on at velocity 0.</summary>
    public MidiBuilder Note(long tick, int pitch, long length = 60, int velocity = 100)
    {
        Current.Add((tick, [0x90, (byte)pitch, (byte)velocity]));
        Current.Add((tick + length, [0x90, (byte)pitch, 0]));
        return this;
    }

    /// <summary>An event of the given bytes, its status first: for a note-on or note-off on its own.</summary>
    public MidiBuilder Raw(long tick, params byte[] bytes)
    {
        Current.Add((tick, bytes));
        return this;
    }

    /// <summary>A Phase Shift sysex event: 'P' 'S' 0 0 difficulty type enable.</summary>
    public MidiBuilder PhaseShift(long tick, byte difficulty, byte type, bool enable)
    {
        byte[] data = [0x50, 0x53, 0, 0, difficulty, type, (byte)(enable ? 1 : 0), 0xF7];
        Current.Add((tick, [0xF0, (byte)data.Length, .. data]));
        return this;
    }

    public MidiBuilder Meta(long tick, byte type, byte[] data)
    {
        Current.Add((tick, [0xFF, type, (byte)data.Length, .. data]));
        return this;
    }

    public byte[] Build()
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[14];
        "MThd"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 6);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)_tracks.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[12..], (ushort)resolution);
        output.Write(header);

        foreach (var track in _tracks)
        {
            using var data = new MemoryStream();
            long tick = 0;
            foreach (var (at, bytes) in track.OrderBy(e => e.Tick))
            {
                WriteVlq(data, at - tick);
                data.Write(bytes);
                tick = at;
            }

            data.Write([0, 0xFF, 0x2F, 0]);
            byte[] chunk = new byte[8];
            "MTrk"u8.CopyTo(chunk);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), (uint)data.Length);
            output.Write(chunk);
            data.WriteTo(output);
        }

        return output.ToArray();
    }

    private List<(long Tick, byte[] Bytes)> Current => _tracks[^1];

    private static void WriteVlq(Stream output, long value)
    {
        var bytes = new Stack<byte>();
        bytes.Push((byte)(value & 0x7F));
        while ((value >>= 7) > 0)
            bytes.Push((byte)(0x80 | (value & 0x7F)));
        foreach (byte b in bytes)
            output.WriteByte(b);
    }
}
