namespace ChartPacker.Audio.Decoding;

/// <summary>What decodes Opus, MP3 and Vorbis packets. WAV is read here whichever it is.</summary>
public enum AudioBackend
{
    /// <summary>
    /// FFmpeg's decoder when its libraries have it and the stream is mono or stereo, as they're several times
    /// faster; the managed decoders otherwise.
    /// </summary>
    Auto,

    /// <summary>The managed decoders: Concentus, NLayer and NVorbis.</summary>
    Managed,

    /// <summary>FFmpeg's decoders; opening a stream they can't be used for throws.</summary>
    Ffmpeg,
}
