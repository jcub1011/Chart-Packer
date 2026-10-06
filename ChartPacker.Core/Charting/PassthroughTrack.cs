using System.Text;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>
/// A chunk of the loaded <c>notes.mid</c> that Chart Builder doesn't edit, such as vocals, venue or an unknown track,
/// kept as its bytes and written back byte for byte, in its place among the others (charter-spec §4.2), until a tempo
/// change that keeps times moves its events (<see cref="Retick"/>). Two are equal when their bytes and names are,
/// whether or not they're <see cref="IsOriginal"/>.
/// </summary>
/// <remarks>
/// It never changes, and its bytes are never changed, so copies of the model share it rather than copying it.
/// Re-ticking makes a new track.
/// </remarks>
public sealed class PassthroughTrack : IEquatable<PassthroughTrack>
{
    /// <summary>The length of a chunk's header: its four-letter type, then the length of its data.</summary>
    public const int HeaderLength = 8;

    // The furthest a MIDI delta reaches: four bytes of seven bits.
    private const long MaxDelta = 0x0FFF_FFFF;

    private readonly byte[] _chunk;

    /// <param name="chunk">
    /// The whole chunk, header included. The track takes the array over: it mustn't be changed afterwards.
    /// </param>
    /// <param name="name">The track's name, from its first track-name event, or null when it has none.</param>
    /// <exception cref="ArgumentException"><paramref name="chunk"/> is shorter than a chunk's header.</exception>
    internal PassthroughTrack(byte[] chunk, string? name)
        : this(chunk, name, isOriginal: true)
    {
    }

    private PassthroughTrack(byte[] chunk, string? name, bool isOriginal)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length < HeaderLength)
            throw new ArgumentException("A chunk starts with an 8-byte header.", nameof(chunk));
        _chunk = chunk;
        Name = name;
        IsOriginal = isOriginal;
    }

    /// <summary>The whole chunk, header included.</summary>
    public ReadOnlyMemory<byte> Chunk => _chunk;

    /// <summary>The track's name, or null when it has none or isn't a track.</summary>
    public string? Name { get; }

    /// <summary>The chunk's four-letter type, <c>MTrk</c> for a track.</summary>
    public string ChunkType => Encoding.Latin1.GetString(_chunk, 0, 4);

    /// <summary>Whether the chunk is a track (<c>MTrk</c>), rather than some other kind of chunk.</summary>
    public bool IsTrack => _chunk.AsSpan(0, 4).SequenceEqual("MTrk"u8);

    /// <summary>
    /// Whether the bytes are still the loaded file's own: true as loaded, false once <see cref="Retick"/> has moved an
    /// event. Byte-for-byte passthrough is promised only while it's true (charter-spec §4.2). It isn't part of equality,
    /// so a re-ticked track saved and read back is the track that was saved.
    /// </summary>
    public bool IsOriginal { get; }

    /// <summary>The bytes themselves, for the writer, which mustn't change them.</summary>
    internal byte[] Array => _chunk;

    /// <summary>
    /// The track with each event, its end included, moved to the tick <paramref name="map"/> gives (charter-spec §4.2):
    /// the same events, with the same bytes, in the same order, only their deltas changed. A chunk that isn't a track,
    /// and a track none of whose events move, is returned as it is; this one is never changed.
    /// </summary>
    /// <remarks>
    /// Every event is written with its status byte, so running status isn't kept. An event <paramref name="map"/> would
    /// put before the one before it stays at that one's tick, so the order is kept whatever the map.
    /// </remarks>
    /// <param name="map">The tick each tick moves to, as <see cref="TickMaps.KeepTimes"/> gives it.</param>
    /// <exception cref="ChartFormatException">The track is malformed, or an event moves too far for a MIDI delta.</exception>
    public PassthroughTrack Retick(Func<long, long> map) => Retick(map, out _);

    /// <inheritdoc cref="Retick(Func{long, long})"/>
    /// <param name="map">The tick each tick moves to.</param>
    /// <param name="moved">How many events moved.</param>
    internal PassthroughTrack Retick(Func<long, long> map, out int moved)
    {
        ArgumentNullException.ThrowIfNull(map);
        moved = 0;
        if (!IsTrack)
            return this;

        var events = MidiFile.ReadEvents(_chunk, new MidiChunk(ChunkType, 0, HeaderLength, _chunk.Length - HeaderLength));
        long previous = 0;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            long tick = Math.Max(previous, map(e.Tick));
            if (tick - previous > MaxDelta)
                throw new ChartFormatException($"An event of the passthrough track {Name ?? "(unnamed)"} would be too late for a MIDI file.");
            if (tick != e.Tick)
            {
                moved++;
                events[i] = e with { Tick = tick };
            }

            previous = tick;
        }

        if (moved == 0)
            return this;

        using var output = new MemoryStream(_chunk.Length + (events.Count / 8) + 16);
        MidiWriter.WriteTrack(output, _chunk, events);
        return new PassthroughTrack(output.ToArray(), Name, isOriginal: false);
    }

    public bool Equals(PassthroughTrack? other) =>
        other is not null && (ReferenceEquals(this, other) || (Name == other.Name && _chunk.AsSpan().SequenceEqual(other._chunk)));

    public override bool Equals(object? obj) => Equals(obj as PassthroughTrack);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.AddBytes(_chunk);
        return hash.ToHashCode();
    }

    public override string ToString() => $"{ChunkType} {Name ?? "(unnamed)"}, {_chunk.Length} bytes";
}
