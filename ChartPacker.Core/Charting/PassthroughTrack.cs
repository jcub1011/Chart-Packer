using System.Text;

namespace ChartPacker.Core.Charting;

/// <summary>
/// A chunk of the loaded <c>notes.mid</c> that Chart Builder doesn't edit, such as vocals, venue or an unknown track,
/// kept as its bytes and written back byte for byte, in its place among the others (charter-spec §4.2). Two are equal
/// when their bytes and names are.
/// </summary>
/// <remarks>
/// It never changes, and its bytes are never changed, so copies of the model share it rather than copying it.
/// </remarks>
public sealed class PassthroughTrack : IEquatable<PassthroughTrack>
{
    /// <summary>The length of a chunk's header: its four-letter type, then the length of its data.</summary>
    public const int HeaderLength = 8;

    private readonly byte[] _chunk;

    /// <param name="chunk">
    /// The whole chunk, header included. The track takes the array over: it mustn't be changed afterwards.
    /// </param>
    /// <param name="name">The track's name, from its first track-name event, or null when it has none.</param>
    /// <exception cref="ArgumentException"><paramref name="chunk"/> is shorter than a chunk's header.</exception>
    internal PassthroughTrack(byte[] chunk, string? name)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length < HeaderLength)
            throw new ArgumentException("A chunk starts with an 8-byte header.", nameof(chunk));
        _chunk = chunk;
        Name = name;
    }

    /// <summary>The whole chunk, header included.</summary>
    public ReadOnlyMemory<byte> Chunk => _chunk;

    /// <summary>The track's name, or null when it has none or isn't a track.</summary>
    public string? Name { get; }

    /// <summary>The chunk's four-letter type, <c>MTrk</c> for a track.</summary>
    public string ChunkType => Encoding.Latin1.GetString(_chunk, 0, 4);

    /// <summary>Whether the chunk is a track (<c>MTrk</c>), rather than some other kind of chunk.</summary>
    public bool IsTrack => _chunk.AsSpan(0, 4).SequenceEqual("MTrk"u8);

    /// <summary>The bytes themselves, for the writer, which mustn't change them.</summary>
    internal byte[] Array => _chunk;

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
