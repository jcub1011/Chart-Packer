using ChartShared.Core.Optimization;
using ChartShared.Ffmpeg;
using ChartShared.Ffmpeg.Interop;
using Serilog;

namespace ChartShared.Video;

/// <summary>
/// Re-encodes a background video to VP8 in a WebM file with no audio track, the one video format that plays on every
/// platform in both Clone Hero and YARG, and estimates what that would weigh by encoding a few short windows of it.
/// </summary>
/// <remarks>
/// <para>The picture is fitted inside the target (1920×1080 for FHD, 3840×2160 for UHD) with its shape kept and is never
/// scaled up; both sides are made even, as VP8's 4:2:0 needs. It keeps the source's frame rate and each frame's time, so
/// a video with gaps keeps them. The audio track, and any other stream, is left out.</para>
/// <para>Every scale and colour conversion is swscale's <c>sws_scale_frame</c> into frames FFmpeg allocated itself:
/// swscale writes past the ends of rows, and into a managed array that corrupted the heap
/// (<see cref="VideoDecoder"/> describes it).</para>
/// <para>The encoder is libvpx's (<c>libvpx_vp8</c> at build time), so builds of FFmpeg from before it was added can't
/// encode: see <see cref="IsAvailable"/>.</para>
/// </remarks>
public static class VideoReencoder
{
    private const int IoBufferSize = 64 * 1024;

    // What a WebM file carries beyond its packets: a header and the cues, and per packet a block's header (a few bytes)
    // and its share of the cluster headers. Measured on written files; only the estimate uses them.
    private const int WebmFixedBytes = 600;
    private const int WebmBytesPerPacket = 10;

    private static ILogger Logger => Log.ForContext(typeof(VideoReencoder));

    /// <summary>Whether this build can encode VP8: FFmpeg loaded, with the libvpx encoder.</summary>
    public static bool IsAvailable => FfmpegLibrary.HasEncoder(Vp8Encoder.EncoderName);

    /// <summary>Why <see cref="IsAvailable"/> is false, or <see langword="null"/> when it's true.</summary>
    public static string? UnavailableReason =>
        !FfmpegLibrary.IsAvailable ? FfmpegLibrary.UnavailableReason
        : IsAvailable ? null
        : "This build of FFmpeg has no VP8 encoder (libvpx); rebuild it with build/ffmpeg/build.sh.";

    /// <summary>
    /// The size a <paramref name="width"/>×<paramref name="height"/> video is encoded at: fitted inside the target with its
    /// shape kept and both sides even, as 4:2:0 needs. A video that's scaled has each side rounded to the nearest even
    /// number (and never past the target); one that fits is left at its own size, with an odd side rounded down by one
    /// pixel, as it's never scaled up.
    /// </summary>
    public static (int Width, int Height) OutputSize(int width, int height, VideoTarget target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var (maxWidth, maxHeight) = new VideoReencodeSettings(target).MaxSize;

        double scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        if (scale >= 1)
            return (Math.Max(2, width & ~1), Math.Max(2, height & ~1));

        return (Math.Max(2, Math.Min(maxWidth & ~1, NearestEven(width * scale))), Math.Max(2, Math.Min(maxHeight & ~1, NearestEven(height * scale))));

        static int NearestEven(double value) => (int)Math.Round(value / 2) * 2;
    }

    /// <summary>
    /// Re-encodes the video at <paramref name="sourcePath"/> to <paramref name="destinationPath"/>. It's written to a temp
    /// file in the destination's folder and moved over only once it's complete, so a cancel or a failure leaves nothing
    /// behind and whatever was at the destination as it was. The source is never changed.
    /// </summary>
    /// <param name="progress">Reports 0 to 1, by how far into the video it is.</param>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available, or has no VP8 encoder; see <see cref="IsAvailable"/>.</exception>
    /// <exception cref="InvalidDataException">The video can't be decoded, has no frames, or can't be encoded.</exception>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The quality is outside libvpx's range.</exception>
    /// <exception cref="OperationCanceledException">The encode was cancelled.</exception>
    public static unsafe void Reencode(
        string sourcePath, string destinationPath, VideoReencodeSettings settings, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        EnsureAvailable();

        string folder = Path.GetDirectoryName(Path.GetFullPath(destinationPath)) ?? throw new IOException($"'{destinationPath}' has no folder.");
        string temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(destinationPath)}.{Guid.NewGuid():N}.webm");
        bool moved = false;
        try
        {
            using (var decoder = OpenDecoder(sourcePath))
            using (var pipeline = new Pipeline(decoder, settings))
            {
                WebmWriter? writer = null;
                try
                {
                    double seconds = decoder.Duration.TotalSeconds;
                    Action<TimeSpan>? onFrame = progress is null || seconds <= 0
                        ? null
                        : time => progress.Report(Math.Clamp(time.TotalSeconds / seconds, 0, 1));

                    // Time zero is the start of the video, not its first frame, so a lead-in is kept.
                    int frames = pipeline.Run(int.MaxValue, TimeSpan.Zero, encoder => writer = new WebmWriter(temp, encoder), packet => writer!.Write(packet), onFrame, cancellationToken);
                    if (frames == 0)
                        throw new InvalidDataException("The video has no frames that can be decoded.");

                    writer!.Finish();
                }
                finally
                {
                    writer?.Dispose();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, destinationPath, overwrite: true);
            moved = true;
            progress?.Report(1);
        }
        finally
        {
            if (!moved)
                TryDelete(temp);
        }
    }

    /// <summary>
    /// Estimates the size of <see cref="Reencode"/>'s output without writing it, by encoding <paramref name="windowCount"/>
    /// windows of <paramref name="windowFrames"/> frames, equally spaced through the video, and scaling what they weigh per
    /// frame to the whole video. Each window starts at its time (the frames from the keyframe the seek lands on up to it are
    /// decoded and dropped, so the windows are where they're spaced, however long the source's keyframe interval) and is a
    /// stream of its own, so it has the keyframe a whole encode has once per <see cref="VideoReencodeSettings.GopFrames"/>. A video short enough that the
    /// windows would cover it (or of unknown length) is encoded whole, which makes its estimate exact.
    /// </summary>
    /// <exception cref="InvalidOperationException">FFmpeg isn't available, or has no VP8 encoder.</exception>
    /// <exception cref="InvalidDataException">The video can't be decoded, or has no frames in the windows.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="OperationCanceledException">The estimate was cancelled.</exception>
    public static unsafe long EstimateSize(
        string sourcePath, VideoReencodeSettings settings, int windowCount = 5, int windowFrames = VideoReencodeSettings.GopFrames, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowFrames, 1);
        settings.Validate();
        EnsureAvailable();

        using var decoder = OpenDecoder(sourcePath);
        using var pipeline = new Pipeline(decoder, settings);
        var counter = new PacketCounter();
        double fps = pipeline.FrameRate;
        double duration = decoder.Duration.TotalSeconds;
        double totalFrames = duration * fps;

        if (duration <= 0 || totalFrames <= (double)windowCount * windowFrames)
        {
            int all = pipeline.Run(int.MaxValue, TimeSpan.Zero, null, counter.Add, null, cancellationToken);
            if (all == 0)
                throw new InvalidDataException("The video has no frames that can be decoded.");
            return WebmFixedBytes + counter.Bytes + (counter.Packets * WebmBytesPerPacket);
        }

        double windowSeconds = windowFrames / fps;
        long frames = 0;
        for (int i = 0; i < windowCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The middle of the i-th of equal parts of the video.
            double start = Math.Clamp(((i + 0.5) * duration / windowCount) - (windowSeconds / 2), 0, Math.Max(0, duration - windowSeconds));
            var from = TimeSpan.FromSeconds(start);
            decoder.Seek(from);
            frames += pipeline.Run(windowFrames, null, null, counter.Add, null, cancellationToken, notBefore: from);
        }

        if (frames == 0)
            throw new InvalidDataException("The video has no frames that can be decoded.");

        // Bytes per frame of what was sampled, over every frame, with the container's overhead per packet; the header once.
        double perFrame = (counter.Bytes + (counter.Packets * (double)WebmBytesPerPacket)) / frames;
        return WebmFixedBytes + (long)Math.Round(perFrame * totalFrames);
    }

    private static void EnsureAvailable()
    {
        if (UnavailableReason is { } reason)
            throw new InvalidOperationException(reason);
    }

    private static VideoDecoder OpenDecoder(string path) =>
        VideoDecoder.Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, IoBufferSize), int.MaxValue);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug(ex, "A temp video file couldn't be deleted");
        }
    }

    /// <summary>Adds up the bytes of the packets an estimate's encodes produce.</summary>
    private sealed unsafe class PacketCounter
    {
        public long Bytes { get; private set; }

        public long Packets { get; private set; }

        public void Add(AVPacket* packet)
        {
            Bytes += packet->size;
            Packets++;
        }
    }

    /// <summary>
    /// Decoded frames to encoded packets: each frame is scaled and converted to the output's size and 4:2:0 by swscale into
    /// a frame FFmpeg allocated, stamped with its time in frames, and encoded. The scaler and the frame it writes are kept
    /// from one <see cref="Run"/> to the next.
    /// </summary>
    private sealed unsafe class Pipeline : IDisposable
    {
        private readonly VideoDecoder _decoder;
        private readonly VideoReencodeSettings _settings;
        private void* _scaler;
        private AVFrame* _scaled;
        private Vp8Encoder.FrameColour? _colour;
        private (int Width, int Height)? _size;
        private bool _logged;

        public Pipeline(VideoDecoder decoder, VideoReencodeSettings settings)
        {
            _decoder = decoder;
            _settings = settings;
            var rate = decoder.FrameRate;
            Rate = rate.num > 0 && rate.den > 0 ? rate : new AVRational { num = 30, den = 1 };
        }

        /// <summary>The frame rate it encodes at: the source's, or 30 when the source doesn't say.</summary>
        public AVRational Rate { get; }

        /// <summary>The frame rate in frames per second.</summary>
        public double FrameRate => (double)Rate.num / Rate.den;

        /// <summary>
        /// Decodes frames from where the decoder is, and encodes up to <paramref name="maxFrames"/> of them into one new VP8
        /// stream, ending it when it's done.
        /// </summary>
        /// <param name="origin">The time that's frame 0; <see langword="null"/> takes the first frame's own.</param>
        /// <param name="onOpened">Called once the encoder is open, with it, before any packet.</param>
        /// <param name="onFrame">Called with each frame's time as it's encoded.</param>
        /// <param name="notBefore">Frames earlier than this are decoded and dropped, as a seek lands on the keyframe before its time.</param>
        /// <returns>How many frames were encoded.</returns>
        public int Run(
            int maxFrames, TimeSpan? origin, Action<Vp8Encoder>? onOpened, PacketHandler handler, Action<TimeSpan>? onFrame, CancellationToken cancellationToken,
            TimeSpan? notBefore = null)
        {
            Vp8Encoder? encoder = null;
            try
            {
                PacketHandler guarded = packet =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    handler(packet);
                };

                int count = 0;
                long lastPts = -1;
                TimeSpan? first = null;
                while (count < maxFrames && _decoder.Next(out var time))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AVFrame* frame = _decoder.CurrentFrame;
                    if (frame == null || frame->width <= 0 || frame->height <= 0 || frame->data[0] == 0)
                        continue;
                    if (notBefore is { } start && time < start)
                        continue;

                    // The frame's time in frames, from the origin. Sources whose frames are closer together than the rate
                    // says (or that repeat a time) would otherwise run on past the video's length, so a frame whose time
                    // rounds to one already used moves on to the next, as the encoder needs each stamp to be later than the
                    // last, but only while that leaves it no more than a frame late; one later than that is dropped.
                    first ??= time;
                    double seconds = Math.Max(0, (time - (origin ?? first.Value)).TotalSeconds);
                    long target = (long)Math.Round(seconds * FrameRate);
                    if (target < lastPts)
                        continue;
                    long pts = Math.Max(lastPts + 1, target);

                    Normalise(frame);
                    if (encoder is null)
                    {
                        encoder = new Vp8Encoder(SetupFor(frame), _settings);
                        onOpened?.Invoke(encoder);
                    }

                    Scale(frame);
                    _scaled->pts = lastPts = pts;

                    encoder.Encode(_scaled, guarded);
                    count++;
                    onFrame?.Invoke(time);
                }

                encoder?.Encode(null, guarded);
                return count;
            }
            finally
            {
                encoder?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_scaler != null)
            {
                void* scaler = _scaler;
                Native.sws_free_context(&scaler);
                _scaler = null;
            }

            if (_scaled != null)
            {
                AVFrame* scaled = _scaled;
                Native.av_frame_free(&scaled);
                _scaled = null;
            }
        }

        // The colour of the first frame is the colour of every frame, as in VideoDecoder: swscale rebuilds its whole
        // conversion when the tags change between frames, and some streams flip them.
        private void Normalise(AVFrame* frame)
        {
            // Converted whole, as one picture; swscale won't turn an interlaced frame into a progressive one.
            frame->flags &= ~Native.FrameFlagInterlaced;

            // HDR10+ metadata is part of what swscale compares, and it changes from frame to frame.
            Native.av_frame_remove_side_data(frame, Native.FrameDataDynamicHdrPlus);

            _colour ??= new Vp8Encoder.FrameColour(frame->color_range, frame->colorspace, frame->color_primaries, frame->color_trc, frame->chroma_location);
            var colour = _colour.Value;
            (frame->color_range, frame->colorspace, frame->color_primaries, frame->color_trc, frame->chroma_location) =
                (colour.Range, colour.Space, colour.Primaries, colour.Transfer, colour.ChromaLocation);
        }

        private Vp8Encoder.Setup SetupFor(AVFrame* first)
        {
            _size ??= OutputSize(first->width, first->height, _settings.Target);
            var (width, height) = _size.Value;
            return new Vp8Encoder.Setup(width, height, Rate, first->sample_aspect_ratio, _colour!.Value);
        }

        // Scales the frame into _scaled, FFmpeg memory of the right size and format, allocated once.
        private void Scale(AVFrame* source)
        {
            var (width, height) = _size!.Value;
            if (_scaler == null)
            {
                _scaler = Native.sws_alloc_context();
                if (_scaler == null)
                    throw new OutOfMemoryException();
                Vp8Encoder.Check(Native.av_opt_set_int(_scaler, "sws_flags", Native.SwsBicubic, 0), "The video's scaler can't be set up");
            }

            if (_scaled == null)
            {
                _scaled = Native.av_frame_alloc();
                if (_scaled == null)
                    throw new OutOfMemoryException();
                _scaled->width = width;
                _scaled->height = height;
                _scaled->format = Native.PixelFormatYuv420p;
                Vp8Encoder.Check(Native.av_frame_get_buffer(_scaled, 0), "A frame can't be allocated");
            }

            // The encoder may still hold the last frame; if it does, this one gets a buffer of its own, rather than writing
            // under it. (av_frame_make_writable would copy the old picture into it, only for the scaler to write over that.)
            if (Native.av_frame_is_writable(_scaled) == 0)
            {
                Native.av_frame_unref(_scaled);
                _scaled->width = width;
                _scaled->height = height;
                _scaled->format = Native.PixelFormatYuv420p;
                Vp8Encoder.Check(Native.av_frame_get_buffer(_scaled, 0), "A frame can't be allocated");
            }

            var colour = _colour!.Value;
            (_scaled->color_range, _scaled->colorspace, _scaled->color_primaries, _scaled->color_trc, _scaled->chroma_location) =
                (colour.Range, colour.Space, colour.Primaries, colour.Transfer, colour.ChromaLocation);

            int result = Native.sws_scale_frame(_scaler, _scaled, source);
            if (result < 0)
            {
                if (!_logged)
                {
                    _logged = true;
                    Logger.Warning(
                        "A {SourceWidth}×{SourceHeight} frame (pixel format {SourceFormat}) couldn't be converted to {Width}×{Height} (FFmpeg error {Error})",
                        source->width, source->height, source->format, width, height, result);
                }

                throw new InvalidDataException($"A frame can't be converted ({Vp8Encoder.Describe(result)}).");
            }

            // What the scaler copied over from the source: libvpx forces a keyframe wherever a frame says it's one, and the
            // encoder decides where its keyframes go.
            _scaled->pict_type = 0;
            _scaled->flags &= ~Native.FrameFlagKey;
            _scaled->width = width;
            _scaled->height = height;
            _scaled->format = Native.PixelFormatYuv420p;
        }
    }
}
