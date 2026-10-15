using System.Buffers.Binary;
using System.Text;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Tests.Sng;

namespace ChartShared.Core.Tests;

/// <summary>The <c>song_length</c> fallback in <see cref="SongSummaryReader"/>, measured by <see cref="AudioLength"/>.</summary>
[TestClass]
public sealed class AudioLengthTests
{
    private const int SampleRate = 8000;

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-audio-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    [DataRow("guitar.ogg", true)]
    [DataRow("Song.MP3", true)]
    [DataRow("vocals.opus", true)]
    [DataRow("drums_1.wav", true)]
    [DataRow("stems/guitar.ogg", false)]
    [DataRow("notes.mid", false)]
    [DataRow("album.png", false)]
    public void RecognisesAudioFiles(string name, bool expected) =>
        Assert.AreEqual(expected, AudioLength.IsAudioFile(name));

    [TestMethod]
    public async Task FolderWithoutSongLengthUsesTheLongestAudioFile()
    {
        string folder = CreateFolder(
            ("song.ini", Encoding.UTF8.GetBytes("[song]\nname = Song\n")),
            ("guitar.wav", Wav(1000)),
            ("song.wav", Wav(2500)),
            ("preview.wav", Wav(500)));

        var summary = await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken);

        Assert.AreEqual(2500L, summary.FallbackLengthMs);
    }

    [TestMethod]
    public async Task FolderWithoutSongIniStillMeasuresAudio()
    {
        string folder = CreateFolder(("guitar.wav", Wav(1500)));

        Assert.AreEqual(1500L, (await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken)).FallbackLengthMs);
    }

    [TestMethod]
    public async Task SongLengthInMetadataSkipsMeasuring()
    {
        string folder = CreateFolder(
            ("song.ini", Encoding.UTF8.GetBytes("[song]\nsong_length = 60000\n")),
            ("guitar.wav", Wav(1000)));

        Assert.IsNull((await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken)).FallbackLengthMs);
    }

    [TestMethod]
    public async Task ZeroSongLengthCountsAsUnset()
    {
        string folder = CreateFolder(
            ("song.ini", Encoding.UTF8.GetBytes("[song]\nsong_length = 0\n")),
            ("guitar.wav", Wav(1000)));

        Assert.AreEqual(1000L, (await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken)).FallbackLengthMs);
    }

    [TestMethod]
    public async Task AudioInSubfoldersAndUnreadableAudioAreIgnored()
    {
        string folder = CreateFolder(
            ("guitar.wav", Wav(1000)),
            ("bass.ogg", [1, 2, 3, 4]),
            ("old/song.wav", Wav(9000)));

        Assert.AreEqual(1000L, (await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken)).FallbackLengthMs);
    }

    [TestMethod]
    public async Task FolderWithNoReadableAudioHasNoFallback()
    {
        string folder = CreateFolder(("notes.chart", [1]), ("song.ogg", [0, 0, 0]));

        Assert.IsNull((await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken)).FallbackLengthMs);
    }

    [TestMethod]
    public async Task SngWithoutSongLengthUsesTheLongestStoredAudioFile()
    {
        var builder = new SngBuilder().AddMetadata("name", "Song");
        builder.AddFile("guitar.wav", Masked(builder, Wav(1200))).AddFile("song.wav", Masked(builder, Wav(3400)));

        Assert.AreEqual(3400L, (await SongSummaryReader.ReadAsync(WriteSng(builder), TestContext.CancellationToken)).FallbackLengthMs);
    }

    [TestMethod]
    public async Task SngWithSongLengthSkipsMeasuring()
    {
        var builder = new SngBuilder().AddMetadata("song_length", "5000");
        builder.AddFile("song.wav", Masked(builder, Wav(3400)));

        Assert.IsNull((await SongSummaryReader.ReadAsync(WriteSng(builder), TestContext.CancellationToken)).FallbackLengthMs);
    }

    [TestMethod]
    public async Task SngAudioPastTheEndIsSkipped()
    {
        var builder = new SngBuilder();
        builder.AddFile("guitar.wav", Masked(builder, Wav(800))).AddFile("song.wav", Masked(builder, Wav(3400)));
        builder.LengthOverrides[1] = 10_000_000;

        Assert.AreEqual(800L, (await SongSummaryReader.ReadAsync(WriteSng(builder), TestContext.CancellationToken)).FallbackLengthMs);
    }

    /// <summary>A silent 16-bit mono PCM WAV lasting <paramref name="ms"/> milliseconds.</summary>
    private static byte[] Wav(int ms)
    {
        int dataLength = SampleRate * ms / 1000 * 2;
        byte[] wav = new byte[44 + dataLength];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        return wav;
    }

    private static byte[] Masked(SngBuilder builder, byte[] data)
    {
        byte[] copy = [.. data];
        SngMask.Apply(copy, builder.Mask);
        return copy;
    }

    private SongEntry WriteSng(SngBuilder builder)
    {
        string path = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(path, builder.Build());
        return new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song");
    }

    private string CreateFolder(params (string Relative, byte[] Data)[] files)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        foreach (var (relative, data) in files)
        {
            string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        return folder;
    }

    private SongEntry Folder(string path) =>
        new(SongEntryKind.SongFolder, path, Path.GetRelativePath(_root.FullName, path), Path.GetFileName(path));

    public TestContext TestContext { get; set; }
}
