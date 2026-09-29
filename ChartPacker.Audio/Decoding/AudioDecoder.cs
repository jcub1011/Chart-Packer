using ChartPacker.Ffmpeg;
using Concentus;

namespace ChartPacker.Audio.Decoding;

/// <summary>Opens a decoder for one of the audio formats of the filename registry (sng-spec §8).</summary>
/// <remarks>
/// <para>The containers are read here: Ogg for Opus and for Vorbis on FFmpeg, MPEG frames, and WAV. Their
/// packets are decoded by FFmpeg's native decoders when its libraries have them and the stream is mono or
/// stereo (see <see cref="AudioBackend"/>), as they're several times faster than the managed ones.</para>
/// <para>Otherwise Vorbis is decoded by <see href="https://github.com/NVorbis/NVorbis">NVorbis</see>, MP3 by
/// <see href="https://github.com/naudio/NLayer">NLayer</see> and Opus by
/// <see href="https://github.com/lostromb/concentus">Concentus</see> (see <c>THIRD-PARTY-NOTICES.md</c>).
/// Either way a stream decodes to the same frames at the same positions, so stems in different formats and on
/// different decoders line up.</para>
/// </remarks>
public static class AudioDecoder
{
    static AudioDecoder()
    {
        // Concentus otherwise probes for a native libopus, which isn't shipped.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    /// <summary>Opens a decoder for <paramref name="stream"/>, chosen by <paramref name="fileName"/>'s extension, on <see cref="AudioBackend.Auto"/>.</summary>
    /// <param name="stream">A readable, seekable stream. The decoder owns it, and disposes it even when this throws.</param>
    /// <param name="fileName">The file's name, for its extension.</param>
    /// <exception cref="InvalidDataException">The extension isn't an audio format's, or the stream isn't audio in that format.</exception>
    public static IPcmSource Open(Stream stream, string fileName) => Open(stream, fileName, AudioBackend.Auto);

    /// <summary>Opens a decoder for <paramref name="stream"/>, chosen by <paramref name="fileName"/>'s extension.</summary>
    /// <param name="stream">A readable, seekable stream. The decoder owns it, and disposes it even when this throws.</param>
    /// <param name="fileName">The file's name, for its extension.</param>
    /// <param name="backend">What decodes the packets.</param>
    /// <exception cref="InvalidDataException">The extension isn't an audio format's, or the stream isn't audio in that format.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="backend"/> is <see cref="AudioBackend.Ffmpeg"/>, and FFmpeg can't decode the stream.</exception>
    public static IPcmSource Open(Stream stream, string fileName, AudioBackend backend) =>
        Open(stream, fileName, backend, FfmpegAudioDecoder.IsAvailable);

    /// <summary>As <see cref="Open(Stream, string, AudioBackend)"/>, with <paramref name="isAvailable"/> saying which of FFmpeg's decoders can be used.</summary>
    internal static IPcmSource Open(Stream stream, string fileName, AudioBackend backend, Func<string, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(isAvailable);

        try
        {
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
            if (!Enum.IsDefined(backend))
                throw new ArgumentOutOfRangeException(nameof(backend));

            return Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".wav" => new WavSource(stream),
                ".ogg" => OpenVorbis(stream, backend, isAvailable),
                ".mp3" => new Mp3Source(stream, backend, isAvailable),
                ".opus" => new OpusSource(stream, backend, isAvailable),
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
    /// Whether FFmpeg's decoder called <paramref name="decoder"/> decodes a stream of <paramref name="channels"/> on
    /// <paramref name="backend"/>. Only mono and stereo are: FFmpeg reorders more channels into its own layout, and
    /// the mixer takes them in the order the managed decoders give.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="backend"/> is <see cref="AudioBackend.Ffmpeg"/>, and it can't be used.</exception>
    internal static bool UsesFfmpeg(AudioBackend backend, string decoder, int channels, Func<string, bool> isAvailable)
    {
        if (backend == AudioBackend.Managed)
            return false;

        if (channels is 1 or 2 && isAvailable(decoder))
            return true;

        if (backend == AudioBackend.Ffmpeg)
        {
            throw new InvalidOperationException(channels is 1 or 2
                ? FfmpegLibrary.UnavailableReason ?? $"FFmpeg has no '{decoder}' decoder."
                : $"FFmpeg decodes only mono and stereo audio here, not {channels} channels.");
        }

        return false;
    }

    /// <summary>Ogg Vorbis, read here and decoded by FFmpeg when it can be, otherwise by NVorbis.</summary>
    private static IPcmSource OpenVorbis(Stream stream, AudioBackend backend, Func<string, bool> isAvailable)
    {
        if (backend == AudioBackend.Managed || (backend == AudioBackend.Auto && !isAvailable(OggVorbisSource.DecoderName)))
            return new VorbisSource(stream);

        if (OggVorbisSource.TryOpen(stream, backend, isAvailable) is { } source)
            return source;

        // More channels than FFmpeg is used for.
        stream.Position = 0;
        return new VorbisSource(stream);
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
