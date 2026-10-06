namespace ChartPacker.Core.Charting;

/// <summary>
/// An event on an edited track that the model doesn't understand, such as an animation note, a face-off marker or an
/// unknown sysex, kept with its tick and written back unchanged (charter-spec §4.1). Two are equal when their ticks
/// and bytes are.
/// </summary>
/// <remarks>
/// The bytes are never changed once the event is made, so copies of the model share them rather than copying them.
/// </remarks>
public readonly struct RawEvent : IEquatable<RawEvent>
{
    private readonly byte[]? _bytes;

    /// <param name="tick">Where it is, in chart ticks.</param>
    /// <param name="bytes">
    /// The event as a MIDI track has it, status byte first, as <c>MidiMessage</c> holds it. The event takes the array
    /// over: it mustn't be changed afterwards.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is empty.</exception>
    internal RawEvent(long tick, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new ArgumentException("An event has at least a status byte.", nameof(bytes));
        Tick = tick;
        _bytes = bytes;
    }

    /// <summary>Where it is, in chart ticks.</summary>
    public long Tick { get; }

    /// <summary>The event, status byte first.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>The event's status byte.</summary>
    public byte Status => _bytes is { Length: > 0 } bytes ? bytes[0] : (byte)0;

    /// <summary>The bytes themselves, for the writer, which mustn't change them.</summary>
    internal byte[] Array => _bytes ?? [];

    /// <summary>The same event at <paramref name="tick"/>, sharing its bytes.</summary>
    internal RawEvent At(long tick) => new(tick, Array);

    public bool Equals(RawEvent other) => Tick == other.Tick && Bytes.Span.SequenceEqual(other.Bytes.Span);

    public override bool Equals(object? obj) => obj is RawEvent other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Tick);
        hash.AddBytes(Bytes.Span);
        return hash.ToHashCode();
    }

    public static bool operator ==(RawEvent left, RawEvent right) => left.Equals(right);

    public static bool operator !=(RawEvent left, RawEvent right) => !left.Equals(right);

    public override string ToString() => $"{Tick}: {Convert.ToHexString(Bytes.Span)}";
}
