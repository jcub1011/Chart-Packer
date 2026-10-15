using ChartPacker.Core.Scanning;
using ChartPacker.Core.Tests.Audio;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class AudioFileInfoTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-audioinfo-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void TagsLengthAndFormatAreRead()
    {
        string path = Path.Combine(_root.FullName, "tagged.wav");
        File.WriteAllBytes(path, TestAudio.Wav(2 * 8000, 8000, 1, (_, _) => 0));
        var track = new ATL.Track(path)
        {
            Title = "Title",
            Artist = "Artist",
            Album = "Album",
            Genre = "Rock",
            Year = 1999,
        };
        Assert.IsTrue(track.Save());

        var info = AudioFileInfo.Read(path);

        Assert.IsNotNull(info);
        Assert.AreEqual("Title", info.Title);
        Assert.AreEqual("Artist", info.Artist);
        Assert.AreEqual("Album", info.Album);
        Assert.AreEqual("Rock", info.Genre);
        Assert.AreEqual("1999", info.Year);
        Assert.AreEqual(2000, info.DurationMs);
        Assert.AreEqual(8000, info.SampleRate);
        Assert.IsNotNull(info.Format);
    }

    [TestMethod]
    public void AnUntaggedFileHasNoTags()
    {
        string path = Path.Combine(_root.FullName, "plain.wav");
        File.WriteAllBytes(path, TestAudio.Wav(8000, 8000, 1, (_, _) => 0));

        var info = AudioFileInfo.Read(path);

        Assert.IsNotNull(info);
        Assert.IsNull(info.Title);
        Assert.IsNull(info.Artist);
        Assert.IsNull(info.Year);
        Assert.AreEqual(1000, info.DurationMs);
    }

    [TestMethod]
    public void AFileThatIsntAudioHasNoLength()
    {
        string path = Path.Combine(_root.FullName, "fake.ogg");
        File.WriteAllText(path, "not audio at all");

        Assert.IsNull(AudioFileInfo.Read(path)?.DurationMs);
    }
}
