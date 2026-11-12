namespace ChartShared.Core.Optimization;

/// <summary>
/// Tells what an audio file really holds from its first bytes, where the extension doesn't say: an <c>.ogg</c> is
/// Vorbis, Opus or FLAC. Nothing is decoded, so it costs one small read per file.
/// </summary>
/// <remarks>
/// It matters twice. Opus in an <c>.ogg</c> is already what audio is transcoded to, so it's left alone rather than
/// decoded and encoded again. And Vorbis and Opus shrink very differently, so estimates group them apart
/// (<see cref="MediaType"/>).
/// </remarks>
public sealed class AudioHeaderProbe : IAudioProbe
{
    // An Ogg page header is 27 bytes and up to 255 segment lengths; the codec's own header starts the first page's data.
    private const int OggHeaderBytes = 27;
    private const int ReadBytes = OggHeaderBytes + 255 + 8;

    /// <inheritdoc/>
    public async Task<ProbeResult?> ProbeAsync(string name, Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stream);

        byte[] head = new byte[ReadBytes];
        int read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return CodecOf(head.AsSpan(0, read)) is { } codec ? new ProbeResult(codec) : null;
    }

    /// <summary>The codec key of a file that starts with <paramref name="head"/>, or null when it isn't an Ogg stream of a codec this knows.</summary>
    internal static string? CodecOf(ReadOnlySpan<byte> head)
    {
        if (head.Length < OggHeaderBytes + 1 || !head[..4].SequenceEqual("OggS"u8))
            return null;

        int segments = head[26];
        var data = head[Math.Min(head.Length, OggHeaderBytes + segments)..];
        if (data.StartsWith("OpusHead"u8))
            return "audio/opus";
        if (data.StartsWith("\u0001vorbis"u8))
            return "audio/vorbis";
        if (data.StartsWith("\u007fFLAC"u8))
            return "audio/flac";
        return null;
    }
}
