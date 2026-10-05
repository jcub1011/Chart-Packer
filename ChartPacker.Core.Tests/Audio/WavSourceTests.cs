using System.Buffers.Binary;
using ChartPacker.Audio.Decoding;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class WavSourceTests
{
    [TestMethod]
    [DataRow(8, 1, false, 1 / 64f)]
    [DataRow(16, 1, false, 2 / 32767f)]
    [DataRow(24, 1, false, 2 / 8388607f)]
    [DataRow(32, 1, false, 1e-6f)]
    [DataRow(32, 3, false, 0f)]
    [DataRow(64, 3, false, 0f)]
    [DataRow(16, 1, true, 2 / 32767f)]
    [DataRow(32, 3, true, 0f)]
    public void DecodesEachSampleFormat(int bits, int format, bool extensible, float tolerance)
    {
        float[] samples = [0f, 0.5f, -0.5f, 0.25f, -1f, 0.75f];
        using var source = new WavSource(new MemoryStream(TestAudio.Wav(samples, 8000, 2, bits, format, extensible)));

        Assert.AreEqual(8000, source.SampleRate);
        Assert.AreEqual(2, source.Channels);
        Assert.AreEqual(3, source.LengthFrames);
        float[] read = TestAudio.ReadAll(source);
        Assert.HasCount(samples.Length, read);
        for (int i = 0; i < samples.Length; i++)
            Assert.AreEqual(samples[i], read[i], tolerance + 1e-6f, $"Sample {i}.");
    }

    [TestMethod]
    public void SeeksExactly()
    {
        using var source = new WavSource(new MemoryStream(TestAudio.Wav(1000, 8000, 1, (n, _) => n / 1000f)));

        source.SeekToFrame(600);
        float[] read = TestAudio.Read(source, 3);

        Assert.HasCount(3, read);
        Assert.AreEqual(0.6f, read[0], 1e-4f);
        Assert.AreEqual(0.602f, read[2], 1e-4f);
    }

    [TestMethod]
    public void ReadsStopAtTheEndOfTheData()
    {
        using var source = new WavSource(new MemoryStream(TestAudio.Wav(10, 8000, 2, (_, _) => 0.1f)));

        Assert.AreEqual(10, source.ReadFrames(new float[100]));
        Assert.AreEqual(0, source.ReadFrames(new float[100]));
    }

    [TestMethod]
    public void OtherChunksBeforeTheDataAreSkipped()
    {
        byte[] plain = TestAudio.Wav(4, 8000, 1, (n, _) => n / 4f);

        // A LIST chunk with an odd length (padded to even) between fmt and data.
        byte[] list = [.. "LIST"u8, 3, 0, 0, 0, 1, 2, 3, 0];
        byte[] wav = [.. plain[..36], .. list, .. plain[36..]];
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);

        using var source = new WavSource(new MemoryStream(wav));

        Assert.AreEqual(4, source.LengthFrames);
        Assert.AreEqual(0.5f, TestAudio.ReadAll(source)[2], 1e-4f);
    }

    [TestMethod]
    public void ADataChunkLongerThanTheFileIsCutToTheFile()
    {
        byte[] wav = TestAudio.Wav(100, 8000, 1, (_, _) => 0.1f);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), int.MaxValue);

        using var source = new WavSource(new MemoryStream(wav));

        Assert.AreEqual(100, source.LengthFrames);
    }

    [TestMethod]
    public void UnsupportedAndBrokenFilesAreRejected()
    {
        byte[] adpcm = TestAudio.Wav(10, 8000, 1, (_, _) => 0f);
        BinaryPrimitives.WriteUInt16LittleEndian(adpcm.AsSpan(20), 2);
        byte[] noData = TestAudio.Wav(10, 8000, 1, (_, _) => 0f)[..36];
        byte[] notRiff = new byte[64];

        foreach (byte[] bytes in new[] { adpcm, noData, notRiff })
            Assert.ThrowsExactly<InvalidDataException>(() => new WavSource(new MemoryStream(bytes)));
    }
}
