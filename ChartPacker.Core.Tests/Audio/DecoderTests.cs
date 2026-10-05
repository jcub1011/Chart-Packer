using ChartPacker.Audio.Decoding;

namespace ChartPacker.Core.Tests.Audio;

/// <summary>
/// <see cref="AudioDecoder"/> and the compressed formats, against the checked-in fixtures, on both backends:
/// the managed decoders and FFmpeg's. The FFmpeg rows are inconclusive where its libraries haven't been fetched.
/// </summary>
[TestClass]
public sealed class DecoderTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-decoder-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg", 44100, 2, AudioBackend.Managed)]
    [DataRow("chirp-stereo-48000.opus", 48000, 2, AudioBackend.Managed)]
    [DataRow("chirp-mono-48000.opus", 48000, 1, AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.mp3", 44100, 2, AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.ogg", 44100, 2, AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-48000.opus", 48000, 2, AudioBackend.Ffmpeg)]
    [DataRow("chirp-mono-48000.opus", 48000, 1, AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-44100.mp3", 44100, 2, AudioBackend.Ffmpeg)]
    public void DecodesTheWholeFileAtItsLength(string name, int sampleRate, int channels, AudioBackend backend)
    {
        using var source = TestAudio.OpenFixture(name, backend);

        Assert.AreEqual(sampleRate, source.SampleRate);
        Assert.AreEqual(channels, source.Channels);

        // Every fixture is exactly one second: Opus trims its pre-skip and end padding, MP3 its
        // encoder delay and padding (from the LAME tag).
        Assert.AreEqual(sampleRate, source.LengthFrames);
        Assert.HasCount(sampleRate * channels, TestAudio.ReadAll(source));
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Managed)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Managed)]
    [DataRow("chirp-mono-48000.opus", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Ffmpeg)]
    [DataRow("chirp-mono-48000.opus", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Ffmpeg)]
    public void DecodedAudioStartsOnTime(string name, AudioBackend backend)
    {
        // A sweep lines up with itself at only one offset, so a high correlation at zero means no
        // delay was left in (or trimmed twice): stems in different formats play in step.
        using var source = TestAudio.OpenFixture(name, backend);

        double correlation = TestAudio.CorrelationWithChirp(TestAudio.ReadAll(source), source.Channels, source.SampleRate);

        Assert.IsGreaterThan(0.99, correlation, $"Correlation with the source sweep was {correlation:F4}.");
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Managed)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Managed)]
    [DataRow("chirp-mono-48000.opus", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Ffmpeg)]
    [DataRow("chirp-mono-48000.opus", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Ffmpeg)]
    public void SeekingLandsOnTheExactFrame(string name, AudioBackend backend)
    {
        float[] whole;
        int channels;
        using (var source = TestAudio.OpenFixture(name, backend))
        {
            whole = TestAudio.ReadAll(source);
            channels = source.Channels;
        }

        foreach (int target in new[] { 0, 777, 5000, 12345, 30000, 40000 })
        {
            using var source = TestAudio.OpenFixture(name, backend);
            source.SeekToFrame(target);
            float[] read = TestAudio.Read(source, 4096);

            int expected = Math.Min(4096, (whole.Length / channels) - target);
            Assert.HasCount(expected * channels, read, $"Frames read after seeking to {target}.");
            for (int i = 0; i < read.Length; i++)
                Assert.AreEqual(whole[(target * channels) + i], read[i], 1e-3f, $"Sample {i} after seeking to {target}.");
        }
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Managed)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Ffmpeg)]
    public void SeekingBackAfterReadingStartsOver(string name, AudioBackend backend)
    {
        using var source = TestAudio.OpenFixture(name, backend);
        float[] first = TestAudio.Read(source, 2000);
        TestAudio.ReadAll(source);

        source.SeekToFrame(0);

        Assert.AreSequenceEqual(first, TestAudio.Read(source, 2000));
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Managed)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Ffmpeg)]
    public void SeekingPastTheEndReadsNothing(string name, AudioBackend backend)
    {
        using var source = TestAudio.OpenFixture(name, backend);

        source.SeekToFrame(source.LengthFrames + 1000);

        Assert.AreEqual(0, source.ReadFrames(new float[1024 * source.Channels]));
    }

    [TestMethod]
    [DataRow("song.ogg", AudioBackend.Managed)]
    [DataRow("song.opus", AudioBackend.Managed)]
    [DataRow("song.mp3", AudioBackend.Managed)]
    [DataRow("song.wav", AudioBackend.Managed)]
    [DataRow("song.ogg", AudioBackend.Ffmpeg)]
    [DataRow("song.opus", AudioBackend.Ffmpeg)]
    [DataRow("song.mp3", AudioBackend.Ffmpeg)]
    [DataRow("song.wav", AudioBackend.Ffmpeg)]
    public void DataThatIsntAudioIsRejectedAndTheStreamClosed(string name, AudioBackend backend)
    {
        // Rejected before a decoder is chosen, so FFmpeg needn't be there.
        var stream = new TrackingStream(new byte[4096]);

        Assert.ThrowsExactly<InvalidDataException>(() => AudioDecoder.Open(stream, name, backend));
        Assert.IsTrue(stream.IsDisposed);
    }

    [TestMethod]
    public void AFormatThatIsntAudioIsRejected()
    {
        var stream = new TrackingStream(File.ReadAllBytes(TestAudio.Fixture("chirp-stereo-44100.ogg")));

        Assert.ThrowsExactly<InvalidDataException>(() => AudioDecoder.Open(stream, "notes.chart"));
        Assert.IsTrue(stream.IsDisposed);
    }

    [TestMethod]
    [DataRow(AudioBackend.Managed)]
    [DataRow(AudioBackend.Ffmpeg)]
    public void AudioWithTheWrongExtensionIsRejected(AudioBackend backend)
    {
        // An Opus file named .ogg is still Ogg, but not Vorbis.
        var stream = new TrackingStream(File.ReadAllBytes(TestAudio.Fixture("chirp-stereo-48000.opus")));

        Assert.ThrowsExactly<InvalidDataException>(() => AudioDecoder.Open(stream, "guitar.ogg", backend));
    }

    [TestMethod]
    [DataRow(AudioBackend.Managed)]
    [DataRow(AudioBackend.Ffmpeg)]
    public void ATruncatedFilePlaysWhatItHas(AudioBackend backend)
    {
        TestAudio.RequireBackend(backend, "song.opus");
        byte[] bytes = File.ReadAllBytes(TestAudio.Fixture("chirp-stereo-48000.opus"));
        using var source = AudioDecoder.Open(new MemoryStream(bytes[..(bytes.Length / 2)]), "song.opus", backend);

        int frames = TestAudio.ReadAll(source).Length / 2;

        Assert.IsTrue(frames > 0 && frames < 48000, $"{frames} frames were decoded.");
    }

    [TestMethod]
    [DataRow(AudioBackend.Managed)]
    [DataRow(AudioBackend.Ffmpeg)]
    public void AnEmptyOpusPacketAddsNoAudio(AudioBackend backend)
    {
        TestAudio.RequireBackend(backend, "song.opus");
        byte[] original = File.ReadAllBytes(TestAudio.Fixture("chirp-stereo-48000.opus"));
        byte[] withEmpty = WithEmptyPacketAfterPage(original, 3);

        using var expected = AudioDecoder.Open(new MemoryStream(original), "song.opus", backend);
        using var actual = AudioDecoder.Open(new MemoryStream(withEmpty), "song.opus", backend);

        Assert.AreEqual(expected.LengthFrames, actual.LengthFrames);
        Assert.AreSequenceEqual(TestAudio.ReadAll(expected), TestAudio.ReadAll(actual));
    }

    [TestMethod]
    public void CheckAcceptsAudioAndRejectsOtherFiles()
    {
        AudioDecoder.Check(TestAudio.Fixture("chirp-stereo-44100.mp3"));

        string fake = Path.Combine(_root.FullName, "guitar.ogg");
        File.WriteAllText(fake, "not audio");
        Assert.ThrowsExactly<InvalidDataException>(() => AudioDecoder.Check(fake));
        File.Delete(fake); // Check closed it.
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg", 1e-3f, 100.0)]
    [DataRow("chirp-stereo-44100.mp3", 5e-3f, 80.0)]
    [DataRow("chirp-stereo-48000.opus", 0.1f, 45.0)]
    [DataRow("chirp-mono-48000.opus", 0.1f, 45.0)]
    public void TheBackendsAgree(string name, float maxDifference, double minSignalToNoise)
    {
        // Stems on either backend must line up, so the lengths are the same and so is the audio, within each pair
        // of decoders' rounding. FFmpeg's Opus decoder is a separate implementation from Concentus, a port of
        // libopus, and Opus is specified by the quality of what's decoded rather than its samples: the two differ
        // by about 60 dB, most in the last few milliseconds.
        float[] managed, ffmpeg;
        using (var source = TestAudio.OpenFixture(name, AudioBackend.Ffmpeg))
        {
            Assert.AreEqual(source.SampleRate, source.LengthFrames);
            ffmpeg = TestAudio.ReadAll(source);
        }

        using (var source = TestAudio.OpenFixture(name, AudioBackend.Managed))
            managed = TestAudio.ReadAll(source);

        Assert.HasCount(managed.Length, ffmpeg);
        double signal = 0, noise = 0;
        for (int i = 0; i < managed.Length; i++)
        {
            float difference = Math.Abs(managed[i] - ffmpeg[i]);
            Assert.IsLessThanOrEqualTo(maxDifference, difference, $"Sample {i} differs by {difference}.");
            signal += (double)managed[i] * managed[i];
            noise += (double)difference * difference;
        }

        double signalToNoise = noise == 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / noise);
        Assert.IsGreaterThanOrEqualTo(minSignalToNoise, signalToNoise, $"The backends differ by {signalToNoise:F1} dB.");
    }

    [TestMethod]
    public void AutoDecodesVorbisWithFfmpegWhenItCan()
    {
        TestAudio.RequireBackend(AudioBackend.Ffmpeg, "song.ogg");

        using var source = AudioDecoder.Open(File.OpenRead(TestAudio.Fixture("chirp-stereo-44100.ogg")), "song.ogg");

        Assert.IsInstanceOfType<OggVorbisSource>(source);
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg")]
    [DataRow("chirp-stereo-48000.opus")]
    [DataRow("chirp-stereo-44100.mp3")]
    public void AutoFallsBackToTheManagedDecodersWithoutFfmpeg(string name)
    {
        // As with libraries built for video alone, or none at all.
        using var expected = TestAudio.OpenFixture(name, AudioBackend.Managed);
        using var actual = AudioDecoder.Open(File.OpenRead(TestAudio.Fixture(name)), name, AudioBackend.Auto, _ => false);

        Assert.AreEqual(expected.GetType(), actual.GetType());
        Assert.AreSequenceEqual(TestAudio.ReadAll(expected), TestAudio.ReadAll(actual));
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg")]
    [DataRow("chirp-stereo-48000.opus")]
    [DataRow("chirp-stereo-44100.mp3")]
    public void TheFfmpegBackendThrowsWithoutFfmpegAndClosesTheStream(string name)
    {
        var stream = new TrackingStream(File.ReadAllBytes(TestAudio.Fixture(name)));

        Assert.ThrowsExactly<InvalidOperationException>(() => AudioDecoder.Open(stream, name, AudioBackend.Ffmpeg, _ => false));
        Assert.IsTrue(stream.IsDisposed);
    }

    [TestMethod]
    [DataRow(AudioBackend.Managed)]
    [DataRow(AudioBackend.Ffmpeg)]
    public void VorbisOverManyPagesDecodesAndSeeksAsOnOnePage(AudioBackend backend)
    {
        // The fixture is one audio page, so its seeks all start from the beginning. Four packets a page gives a
        // dozen pages, whose granule positions a seek then starts from. Building it needs FFmpeg to count samples.
        TestAudio.RequireBackend(AudioBackend.Ffmpeg, "song.ogg");
        byte[] paged = TestOgg.RepaginateVorbis(File.ReadAllBytes(TestAudio.Fixture("chirp-stereo-44100.ogg")), packetsPerPage: 4);

        float[] whole;
        using (var original = TestAudio.OpenFixture("chirp-stereo-44100.ogg", AudioBackend.Managed))
            whole = TestAudio.ReadAll(original);

        using (var source = AudioDecoder.Open(new MemoryStream(paged), "song.ogg", backend))
        {
            Assert.AreEqual(44100, source.LengthFrames);
            float[] read = TestAudio.ReadAll(source);
            Assert.HasCount(whole.Length, read);
            for (int i = 0; i < read.Length; i++)
                Assert.AreEqual(whole[i], read[i], 1e-3f, $"Sample {i}.");
        }

        foreach (int target in new[] { 0, 777, 5000, 9000, 12345, 20000, 30000, 40000, 44000 })
        {
            using var source = AudioDecoder.Open(new MemoryStream(paged), "song.ogg", backend);
            source.SeekToFrame(target);
            float[] read = TestAudio.Read(source, 4096);

            int expected = Math.Min(4096, 44100 - target);
            Assert.HasCount(expected * 2, read, $"Frames read after seeking to {target}.");
            for (int i = 0; i < read.Length; i++)
                Assert.AreEqual(whole[(target * 2) + i], read[i], 1e-3f, $"Sample {i} after seeking to {target}.");
        }
    }

    /// <summary>
    /// <paramref name="ogg"/> with a zero-length packet added at the end of the first page, from
    /// <paramref name="fromPage"/> on, that ends a packet. The CRC isn't redone: the reader doesn't check it.
    /// </summary>
    private static byte[] WithEmptyPacketAfterPage(byte[] ogg, int fromPage)
    {
        int offset = 0;
        for (int page = 0; offset < ogg.Length; page++)
        {
            int segments = ogg[offset + 26];
            int bodyLength = 0;
            for (int i = 0; i < segments; i++)
                bodyLength += ogg[offset + 27 + i];
            int tableEnd = offset + 27 + segments;

            // A last lacing value under 255 ends a packet on this page, so an empty one can follow it.
            if (page >= fromPage && segments is > 0 and < 255 && ogg[tableEnd - 1] < 255)
            {
                byte[] result = [.. ogg[..tableEnd], 0, .. ogg[tableEnd..]];
                result[offset + 26]++;
                return result;
            }

            offset = tableEnd + bodyLength;
        }

        throw new InvalidOperationException("No page to add an empty packet to.");
    }

    /// <summary>A memory stream that records being disposed.</summary>
    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
