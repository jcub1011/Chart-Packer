namespace ChartShared.Audio.Decoding;

/// <summary>
/// Decodes one codec's packets, which a source has read from its container, to interleaved float frames. The
/// source keeps the timeline: what's trimmed, where a seek lands.
/// </summary>
internal interface IPacketDecoder : IDisposable
{
    /// <summary>Decodes <paramref name="packet"/> into <paramref name="output"/> from its start.</summary>
    /// <returns>How many frames were written; 0 is allowed.</returns>
    /// <exception cref="InvalidDataException">The packet is damaged.</exception>
    int Decode(ReadOnlySpan<byte> packet, Span<float> output);

    /// <summary>Forgets every packet decoded, as before the first, for a seek.</summary>
    void Reset();
}
