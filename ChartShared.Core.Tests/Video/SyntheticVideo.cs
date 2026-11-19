using ChartShared.Ffmpeg.Interop;
using ChartShared.Video;

namespace ChartShared.Core.Tests.Video;

/// <summary>
/// Writes test videos of any size and length: VP8 in WebM, of moving stripes with deterministic noise, so every frame
/// differs and a long video's bytes per frame stay steady. Made with the same encoder and writer the re-encoder uses,
/// so the tests that need one need the libvpx encoder too.
/// </summary>
internal static unsafe class SyntheticVideo
{
    /// <summary>Writes a video of <paramref name="frames"/> frames at <paramref name="width"/>×<paramref name="height"/>, <paramref name="fps"/> frames per second.</summary>
    /// <param name="noise">How far the deterministic noise goes; 0 leaves the stripes clean, which compress to almost nothing.</param>
    /// <param name="skipEvery">When positive, every this-many-th frame is left out of the timeline, so the video has gaps.</param>
    public static void Write(string path, int width, int height, int frames, int fps = 30, int cq = 30, int skipEvery = 0, int noise = 24)
    {
        var rate = new AVRational { num = fps, den = 1 };
        using var encoder = new Vp8Encoder(
            new Vp8Encoder.Setup(width, height, rate, new AVRational { num = 1, den = 1 }, new Vp8Encoder.FrameColour(0, 0, 0, 0, 0)),
            new VideoReencodeSettings(Core.Optimization.VideoTarget.Uhd, cq));
        using var writer = new WebmWriter(path, encoder);

        AVFrame* frame = Native.av_frame_alloc();
        try
        {
            frame->width = width;
            frame->height = height;
            frame->format = Native.PixelFormatYuv420p;
            if (Native.av_frame_get_buffer(frame, 0) < 0)
                throw new InvalidOperationException("A frame can't be allocated.");

            var random = new Random(12345);
            long pts = 0;
            for (int i = 0; i < frames; i++)
            {
                if (Native.av_frame_make_writable(frame) < 0)
                    throw new InvalidOperationException("A frame can't be made writable.");

                Fill(frame, width, height, i, random, noise);
                pts += skipEvery > 0 && i > 0 && i % skipEvery == 0 ? 2 : 1;
                frame->pts = pts;
                encoder.Encode(frame, packet => writer.Write(packet));
            }

            encoder.Encode(null, packet => writer.Write(packet));
            writer.Finish();
        }
        finally
        {
            Native.av_frame_free(&frame);
        }
    }

    private static void Fill(AVFrame* frame, int width, int height, int index, Random random, int noise)
    {
        byte* luma = (byte*)frame->data[0];
        int lumaStride = frame->linesize[0];
        for (int y = 0; y < height; y++)
        {
            byte* row = luma + ((long)y * lumaStride);
            for (int x = 0; x < width; x++)
                row[x] = (byte)(((x + y + (index * 3)) & 0x7F) + (noise > 0 ? random.Next(0, noise) : 0));
        }

        for (int plane = 1; plane <= 2; plane++)
        {
            byte* chroma = (byte*)frame->data[plane];
            int stride = frame->linesize[plane];
            for (int y = 0; y < (height + 1) / 2; y++)
            {
                byte* row = chroma + ((long)y * stride);
                for (int x = 0; x < (width + 1) / 2; x++)
                    row[x] = (byte)(112 + (plane * 8) + ((x + index) & 0xF));
            }
        }
    }
}
