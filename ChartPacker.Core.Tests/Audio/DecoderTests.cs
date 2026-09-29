using ChartPacker.Audio.Decoding;

namespace ChartPacker.Core.Tests.Audio;

/// <summary><see cref="AudioDecoder"/> and the compressed formats, against the checked-in fixtures.</summary>
[TestClass]
public sealed class DecoderTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-decoder-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg", 44100, 2)]
    [DataRow("chirp-stereo-48000.opus", 48000, 2)]
    [DataRow("chirp-mono-48000.opus", 48000, 1)]
    [DataRow("chirp-stereo-44100.mp3", 44100, 2)]
    public void DecodesTheWholeFileAtItsLength(string name, int sampleRate, int channels)
    {
        using var source = TestAudio.OpenFixture(name);

        Assert.AreEqual(sampleRate, source.SampleRate);
        Assert.AreEqual(channels, source.Channels);

        // Every fixture is exactly one second: Opus trims its pre-skip and end padding, MP3 its
        // encoder delay and padding (from the LAME tag).
        Assert.AreEqual(sampleRate, source.LengthFrames);
        Assert.AreEqual(sampleRate * channels, TestAudio.ReadAll(source).Length);
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg")]
    [DataRow("chirp-stereo-48000.opus")]
    [DataRow("chirp-mono-48000.opus")]
    [DataRow("chirp-stereo-44100.mp3")]
    public void DecodedAudioStartsOnTime(string name)
    {
        // A sweep lines up with itself at only one offset, so a high correlation at zero means no
        // delay was left in (or trimmed twice): stems in different formats play in step.
        using var source = TestAudio.OpenFixture(name);

        double correlation = TestAudio.CorrelationWithChirp(TestAudio.ReadAll(source), source.Channels, source.SampleRate);

        Assert.IsTrue(correlation > 0.99, $"Correlation with the source sweep was {correlation:F4}.");
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg")]
    [DataRow("chirp-stereo-48000.opus")]
    [DataRow("chirp-mono-48000.opus")]
    [DataRow("chirp-stereo-44100.mp3")]
    public void SeekingLandsOnTheExactFrame(string name)
    {
        float[] whole;
        int channels;
        using (var source = TestAudio.OpenFixture(name))
        {
            whole = TestAudio.ReadAll(source);
            channels = source.Channels;
        }

        foreach (int target in new[] { 0, 777, 5000, 12345, 30000, 40000 })
        {
            using var source = TestAudio.OpenFixture(name);
            source.SeekToFrame(target);
            float[] read = TestAudio.Read(source, 4096);

            int expected = Math.Min(4096, (whole.Length / channels) - target);
            Assert.AreEqual(expected * channels, read.Length, $"Frames read after seeking to {target}.");
            for (int i = 0; i < read.Length; i++)
                Assert.AreEqual(whole[(target * channels) + i], read[i], 1e-3f, $"Sample {i} after seeking to {target}.");
        }
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg")]
    [DataRow("chirp-stereo-48000.opus")]
    [DataRow("chirp-stereo-44100.mp3")]
    public void SeekingBackAfterReadingStartsOver(string name)
    {
        using var source = TestAudio.OpenFixture(name);
        float[] first = TestAudio.Read(source, 2000);
        TestAudio.ReadAll(source);

        source.SeekToFrame(0);

        CollectionAssert.AreEqual(first, TestAudio.Read(source, 2000));
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg")]
    [DataRow("chirp-stereo-48000.opus")]
    [DataRow("chirp-stereo-44100.mp3")]
    public void SeekingPastTheEndReadsNothing(string name)
    {
        using var source = TestAudio.OpenFixture(name);

        source.SeekToFrame(source.LengthFrames + 1000);

        Assert.AreEqual(0, source.ReadFrames(new float[1024 * source.Channels]));
    }

    [TestMethod]
    [DataRow("song.ogg")]
    [DataRow("song.opus")]
    [DataRow("song.mp3")]
    [DataRow("song.wav")]
    public void DataThatIsntAudioIsRejectedAndTheStreamClosed(string name)
    {
        var stream = new TrackingStream(new byte[4096]);

        Assert.ThrowsExactly<InvalidDataException>(() => AudioDecoder.Open(stream, name));
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
    public void AudioWithTheWrongExtensionIsRejected()
    {
        // An Opus file named .ogg is still Ogg, but not Vorbis.
        var stream = new TrackingStream(File.ReadAllBytes(TestAudio.Fixture("chirp-stereo-48000.opus")));

        Assert.ThrowsExactly<InvalidDataException>(() => AudioDecoder.Open(stream, "guitar.ogg"));
    }

    [TestMethod]
    public void ATruncatedFilePlaysWhatItHas()
    {
        byte[] bytes = File.ReadAllBytes(TestAudio.Fixture("chirp-stereo-48000.opus"));
        using var source = AudioDecoder.Open(new MemoryStream(bytes[..(bytes.Length / 2)]), "song.opus");

        int frames = TestAudio.ReadAll(source).Length / 2;

        Assert.IsTrue(frames > 0 && frames < 48000, $"{frames} frames were decoded.");
    }

    [TestMethod]
    public void AnEmptyOpusPacketAddsNoAudio()
    {
        byte[] original = File.ReadAllBytes(TestAudio.Fixture("chirp-stereo-48000.opus"));
        byte[] withEmpty = WithEmptyPacketAfterPage(original, 3);

        using var expected = AudioDecoder.Open(new MemoryStream(original), "song.opus");
        using var actual = AudioDecoder.Open(new MemoryStream(withEmpty), "song.opus");

        Assert.AreEqual(expected.LengthFrames, actual.LengthFrames);
        CollectionAssert.AreEqual(TestAudio.ReadAll(expected), TestAudio.ReadAll(actual));
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
