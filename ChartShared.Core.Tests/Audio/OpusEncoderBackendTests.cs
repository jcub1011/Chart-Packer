using ChartShared.Audio.Decoding;
using ChartShared.Audio.Encoding;
using ChartShared.Ffmpeg;

namespace ChartShared.Core.Tests.Audio;

/// <summary>
/// The two Opus encoders behind <see cref="OpusTranscoder"/>: libopus through FFmpeg (<see cref="FfmpegOpusEncoder"/>, skipped as
/// inconclusive when the libraries lack it) and managed Concentus, which stands in when it's missing. Each must give a stream
/// <see cref="AudioDecoder"/> reads back at exactly the length that went in.
/// </summary>
[TestClass]
public sealed class OpusEncoderBackendTests
{
    public TestContext TestContext { get; set; } = null!;

    private static void RequireNative()
    {
        if (!FfmpegLibrary.IsAvailable)
            Assert.Inconclusive(FfmpegLibrary.UnavailableReason ?? "");
        if (!FfmpegOpusEncoder.IsAvailable)
            Assert.Inconclusive("This build of FFmpeg has no libopus encoder; see build/ffmpeg/README.md.");
    }

    private byte[] Encode(byte[] wav, int bitrateKbps, OpusEncoderBackend backend)
    {
        using var source = AudioDecoder.Open(new MemoryStream(wav), "a.wav");
        var output = new MemoryStream();
        OpusTranscoder.Transcode(source, output, bitrateKbps, "a.wav", null, TestContext.CancellationToken, null, backend);
        return output.ToArray();
    }

    private static byte[] Chirp(int frames, int channels) =>
        TestAudio.Wav(frames, 48000, channels, (n, c) => (float)TestAudio.Chirp((double)n / 48000, rising: c == 0));

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void NativeEncodesAStreamThatDecodesToTheSameLength(int channels)
    {
        RequireNative();

        // Not a whole number of frames, so the last one is padded and trimmed by the final granule.
        byte[] opus = Encode(Chirp(48000 + 123, channels), 128, OpusEncoderBackend.Native);

        using var decoded = AudioDecoder.Open(new MemoryStream(opus), "a.opus");
        Assert.AreEqual(48000, decoded.SampleRate);
        Assert.AreEqual(channels, decoded.Channels);
        Assert.AreEqual(48000 + 123, decoded.LengthFrames);
        Assert.IsGreaterThan(0.9, TestAudio.CorrelationWithChirp(TestAudio.ReadAll(decoded), channels, 48000));
    }

    [TestMethod]
    public void ManagedEncodesAStreamThatDecodesToTheSameLength()
    {
        byte[] opus = Encode(Chirp(48000 + 123, 2), 128, OpusEncoderBackend.Managed);

        using var decoded = AudioDecoder.Open(new MemoryStream(opus), "a.opus");
        Assert.AreEqual(48000 + 123, decoded.LengthFrames);
        Assert.IsGreaterThan(0.9, TestAudio.CorrelationWithChirp(TestAudio.ReadAll(decoded), 2, 48000));
    }

    [TestMethod]
    public void TheEncodersAreAboutAsLargeAsEachOther()
    {
        RequireNative();
        byte[] wav = Chirp(48000 * 4, 2);

        long native = Encode(wav, 96, OpusEncoderBackend.Native).Length;
        long managed = Encode(wav, 96, OpusEncoderBackend.Managed).Length;

        double ratio = (double)native / managed;
        Assert.IsTrue(ratio is > 0.7 and < 1.4, $"Native {native} bytes, managed {managed} bytes.");
    }

    [TestMethod]
    public void AutoUsesTheNativeEncoderWhenThereIsOne()
    {
        using var encoder = OpusPacketEncoders.Create(2, 128000);
        Assert.AreEqual(OpusPacketEncoders.NativeAvailable, encoder.IsNative);

        using var managed = OpusPacketEncoders.Create(2, 128000, OpusEncoderBackend.Managed);
        Assert.IsFalse(managed.IsNative);
    }

    [TestMethod]
    public void NativeIsRefusedWhenItIsNotThere()
    {
        if (OpusPacketEncoders.NativeAvailable)
            Assert.Inconclusive("The native encoder is available, so it can't be refused.");

        Assert.ThrowsExactly<InvalidOperationException>(() => OpusPacketEncoders.Create(2, 128000, OpusEncoderBackend.Native));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void TheNativeEncoderGivesOnePacketPerFrameAndALookahead(int channels)
    {
        RequireNative();
        using var encoder = new FfmpegOpusEncoder(channels, 64000 * channels / 2);
        Assert.AreEqual(channels, encoder.Channels);
        Assert.IsGreaterThan(0, encoder.Lookahead);

        var frame = new float[FfmpegOpusEncoder.FrameSize * channels];
        var packet = new byte[4000];
        for (int i = 0; i < 20; i++)
        {
            for (int n = 0; n < FfmpegOpusEncoder.FrameSize; n++)
            {
                float sample = 0.5f * MathF.Sin(2 * MathF.PI * 440 * ((i * FfmpegOpusEncoder.FrameSize) + n) / 48000);
                for (int c = 0; c < channels; c++)
                    frame[(n * channels) + c] = sample;
            }

            int length = encoder.EncodeFrame(frame, packet);
            Assert.IsInRange(1, packet.Length, length);
        }
    }

    [TestMethod]
    public void TheNativeEncoderAcceptsTheBitrateExtremes()
    {
        RequireNative();
        foreach (int bitrate in new[] { OpusBitrates.MinimumBps, OpusBitrates.MaximumBps })
        {
            using var encoder = new FfmpegOpusEncoder(2, bitrate);
            Assert.IsGreaterThan(0, encoder.EncodeFrame(new float[FfmpegOpusEncoder.FrameSize * 2], new byte[4000]));
        }
    }

    [TestMethod]
    public void TheNativeEncoderRefusesTheWrongShapes()
    {
        RequireNative();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FfmpegOpusEncoder(3, 128000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FfmpegOpusEncoder(2, 0));

        using var encoder = new FfmpegOpusEncoder(2, 128000);
        Assert.ThrowsExactly<ArgumentException>(() => encoder.EncodeFrame(new float[10], new byte[4000]));
        Assert.ThrowsExactly<ArgumentException>(() => encoder.EncodeFrame(new float[FfmpegOpusEncoder.FrameSize * 2], new byte[1]));

        encoder.Dispose();
        encoder.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => encoder.EncodeFrame(new float[FfmpegOpusEncoder.FrameSize * 2], new byte[4000]));
    }
}
