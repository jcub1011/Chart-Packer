using System.Runtime.InteropServices;
using ChartShared.Ffmpeg.Interop;

namespace ChartShared.Ffmpeg;

/// <summary>
/// Decodes a media file's best audio stream as interleaved float samples, on the file's video timeline: a silent stretch
/// before the audio starts is kept, and audio before the video starts is dropped.
/// </summary>
/// <remarks>
/// <para>The reader is for one pass over the stream, as a waveform read is. It doesn't seek.</para>
/// <para>Only mono and stereo are read: the waveform summariser averages channels, and video soundtracks are almost always
/// one or the other.</para>
/// </remarks>
public sealed unsafe class FfmpegAudioTrackReader : IDisposable
{
    // A packet's decoded audio is normally far shorter than this; PCM in a video can be a second or more.
    private const int MaxPacketFrames = 65536;

    private readonly FfmpegStreamInput _input;
    private readonly AVFormatContext* _format;
    private readonly int _audioIndex;
    private readonly FfmpegAudioDecoder _decoder;
    private readonly AVPacket* _packet;
    private readonly float[] _pcm;

    private int _pcmFrames;
    private int _pcmIndex;
    private long _leadingSilenceFrames;
    private long _skipFrames;
    private long _outputFrame;
    private bool _ended;

    private FfmpegAudioTrackReader(FfmpegStreamInput input, int audioIndex, int videoIndex)
    {
        _input = input;
        _format = input.Format;
        _audioIndex = audioIndex;

        AVStream* audioStream = _format->streams[audioIndex];
        AVCodecParameters* parameters = audioStream->codecpar;
        Channels = parameters->ch_layout.nb_channels;
        SampleRate = parameters->sample_rate;
        if (Channels is not (1 or 2))
            throw new InvalidDataException($"The video's audio has {Channels} channels; only mono and stereo are supported.");
        if (SampleRate <= 0)
            throw new InvalidDataException("The video's audio has no sample rate.");

        AVCodec* codec = Native.avcodec_find_decoder(parameters->codec_id);
        if (codec == null)
            throw new InvalidDataException("FFmpeg has no decoder for the video's audio.");
        string name = Marshal.PtrToStringUTF8((nint)codec->name) ?? throw new InvalidDataException("The video's audio decoder has no name.");

        byte[] extradata = parameters->extradata_size > 0 && parameters->extradata != null
            ? new ReadOnlySpan<byte>(parameters->extradata, parameters->extradata_size).ToArray()
            : [];
        _decoder = new FfmpegAudioDecoder(name, Channels, extradata, trimsStart: true, SampleRate);
        _packet = Native.av_packet_alloc();
        if (_packet == null)
            throw new OutOfMemoryException();
        _pcm = new float[MaxPacketFrames * Channels];

        AVStream* videoStream = videoIndex >= 0 ? _format->streams[videoIndex] : null;
        double videoStart = SecondsAt(videoStream, videoStream != null ? videoStream->start_time : 0);
        double audioStart = SecondsAt(audioStream, audioStream->start_time);
        double offset = audioStart - videoStart;
        if (offset >= 0)
            _leadingSilenceFrames = (long)Math.Round(offset * SampleRate, MidpointRounding.AwayFromZero);
        else
            _skipFrames = (long)Math.Round(-offset * SampleRate, MidpointRounding.AwayFromZero);

        double seconds = DurationSeconds(audioStream);
        if (seconds <= 0)
            throw new InvalidDataException("The video's audio length isn't known.");
        long audioFrames = (long)Math.Round(seconds * SampleRate, MidpointRounding.AwayFromZero);
        LengthFrames = Math.Max(0, _leadingSilenceFrames + audioFrames - _skipFrames);
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>How many frames the audio covers on the video's timeline.</summary>
    public long LengthFrames { get; }

    /// <summary>
    /// Opens <paramref name="stream"/> and finds its best audio stream; <see langword="null"/> when it has none. The stream
    /// is owned by the result, and is disposed even when this returns <see langword="null"/> or throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available.</exception>
    /// <exception cref="InvalidDataException">The audio can't be decoded, or has more than two channels or no known length.</exception>
    public static FfmpegAudioTrackReader? TryOpen(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        FfmpegStreamInput? input = null;
        try
        {
            input = FfmpegStreamInput.Open(stream);
            int audio = Native.av_find_best_stream(input.Format, Native.MediaTypeAudio, -1, -1, null, 0);
            if (audio < 0)
            {
                input.Dispose();
                return null;
            }

            int video = Native.av_find_best_stream(input.Format, Native.MediaTypeVideo, -1, -1, null, 0);
            var reader = new FfmpegAudioTrackReader(input, audio, video);
            input = null;
            return reader;
        }
        finally
        {
            input?.Dispose();
        }
    }

    /// <summary>Reads the next frames into <paramref name="buffer"/>, interleaved; 0 at the end.</summary>
    /// <exception cref="InvalidDataException">The audio is damaged.</exception>
    public int ReadFrames(Span<float> buffer)
    {
        if (buffer.Length % Channels != 0)
            throw new ArgumentException("The buffer must hold a whole number of frames.", nameof(buffer));

        int wanted = buffer.Length / Channels;
        int written = 0;
        while (written < wanted)
        {
            if (_outputFrame < _leadingSilenceFrames)
            {
                int silence = (int)Math.Min(wanted - written, _leadingSilenceFrames - _outputFrame);
                buffer.Slice(written * Channels, silence * Channels).Clear();
                written += silence;
                _outputFrame += silence;
                continue;
            }

            if (_pcmIndex >= _pcmFrames)
            {
                if (_ended || !ReadPacket())
                    break;
                continue;
            }

            if (_skipFrames > 0)
            {
                int drop = (int)Math.Min(_pcmFrames - _pcmIndex, _skipFrames);
                _pcmIndex += drop;
                _skipFrames -= drop;
                continue;
            }

            int copy = Math.Min(_pcmFrames - _pcmIndex, wanted - written);
            _pcm.AsSpan(_pcmIndex * Channels, copy * Channels).CopyTo(buffer[(written * Channels)..]);
            _pcmIndex += copy;
            written += copy;
            _outputFrame += copy;
        }

        return written;
    }

    public void Dispose()
    {
        if (_packet != null)
        {
            AVPacket* packet = _packet;
            Native.av_packet_free(&packet);
        }

        _decoder.Dispose();
        _input.Dispose();
    }

    private bool ReadPacket()
    {
        while (true)
        {
            int read = Native.av_read_frame(_format, _packet);
            if (read < 0)
            {
                _ended = true;
                return false;
            }

            if (_packet->stream_index == _audioIndex && _packet->size > 0)
            {
                int frames;
                try
                {
                    frames = _decoder.Decode(new ReadOnlySpan<byte>(_packet->data, _packet->size), _pcm);
                }
                finally
                {
                    Native.av_packet_unref(_packet);
                }

                if (frames > 0)
                {
                    _pcmFrames = frames;
                    _pcmIndex = 0;
                    return true;
                }
            }
            else
            {
                Native.av_packet_unref(_packet);
            }
        }
    }

    private double DurationSeconds(AVStream* stream)
    {
        if (stream->duration > 0 && stream->duration != Native.NoPtsValue && stream->time_base.den != 0)
            return stream->duration * (double)stream->time_base.num / stream->time_base.den;

        long duration = _format->duration;
        return duration > 0 && duration != Native.NoPtsValue ? duration / (double)Native.TimeBase : 0;
    }

    private static double SecondsAt(AVStream* stream, long timestamp)
    {
        if (stream == null || timestamp == Native.NoPtsValue || stream->time_base.den == 0)
            return 0;
        return timestamp * (double)stream->time_base.num / stream->time_base.den;
    }
}
