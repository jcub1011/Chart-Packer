using Concentus;

namespace ChartPacker.Audio.Decoding;

/// <summary>Opens a decoder for one of the audio formats of the filename registry (sng-spec §8).</summary>
/// <remarks>
/// Vorbis is decoded by <see href="https://github.com/NVorbis/NVorbis">NVorbis</see>, MP3 by
/// <see href="https://github.com/naudio/NLayer">NLayer</see> and Opus by
/// <see href="https://github.com/lostromb/concentus">Concentus</see> (see
/// <c>THIRD-PARTY-NOTICES.md</c>); WAV and the Ogg container of Opus are read here.
/// </remarks>
public static class AudioDecoder
{
    static AudioDecoder()
    {
        // Concentus otherwise probes for a native libopus, which isn't shipped.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    /// <summary>Opens a decoder for <paramref name="stream"/>, chosen by <paramref name="fileName"/>'s extension.</summary>
    /// <param name="stream">A readable, seekable stream. The decoder owns it, and disposes it even when this throws.</param>
    /// <param name="fileName">The file's name, for its extension.</param>
    /// <exception cref="InvalidDataException">The extension isn't an audio format's, or the stream isn't audio in that format.</exception>
    public static IPcmSource Open(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fileName);

        try
        {
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));

            return Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".wav" => new WavSource(stream),
                ".ogg" => new VorbisSource(stream),
                ".mp3" => new Mp3Source(stream),
                ".opus" => new OpusSource(stream),
                string extension => throw new InvalidDataException($"'{extension}' isn't an audio format."),
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens <paramref name="path"/> and decodes its first frames, to check that it's audio this can play.</summary>
    /// <exception cref="InvalidDataException">It isn't audio in the format its extension says.</exception>
    /// <exception cref="IOException">It can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">It can't be read.</exception>
    public static void Check(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var source = Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), path);
        if (source.ReadFrames(new float[1024 * source.Channels]) == 0)
            throw new InvalidDataException("The file has no audio.");
    }

    /// <summary>
    /// Whether <paramref name="ex"/>, thrown by a decoding library, means the audio is damaged:
    /// the libraries report that as assorted exceptions. I/O failures and cancellation aren't.
    /// </summary>
    internal static bool IsDecoderFault(Exception ex) =>
        ex is not (InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException or OutOfMemoryException or ObjectDisposedException);

    /// <summary><paramref name="ex"/>, a decoder fault, as the exception sources throw for damaged audio.</summary>
    internal static InvalidDataException Damaged(Exception ex) => new(ex.Message, ex);
}
