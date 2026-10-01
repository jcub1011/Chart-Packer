using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class SongBackgroundsTests
{
    [TestMethod]
    [DataRow(@"C:\pics\Sky.PNG", BackgroundKind.Image)]
    [DataRow("a.jpeg", BackgroundKind.Image)]
    [DataRow("clip.WebM", BackgroundKind.Video)]
    [DataRow("clip.vp8", BackgroundKind.Video)]
    [DataRow("clip.mpeg", BackgroundKind.Video)]
    public void KindOfGoesByExtension(string path, BackgroundKind kind) =>
        Assert.AreEqual(kind, SongBackgrounds.KindOf(path));

    [TestMethod]
    [DataRow("song.ogg")]
    [DataRow("clip.mkv")]
    [DataRow("noextension")]
    public void KindOfOtherFilesIsNull(string path) => Assert.IsNull(SongBackgrounds.KindOf(path));

    [TestMethod]
    public void StoredNameUsesTheKindsStemAndTheLowercasedExtension()
    {
        Assert.AreEqual("background.jpg", SongBackgrounds.StoredNameFor(BackgroundKind.Image, @"C:\pics\Sky.JPG"));
        Assert.AreEqual("video.webm", SongBackgrounds.StoredNameFor(BackgroundKind.Video, "Clip.WEBM"));
        Assert.IsNull(SongBackgrounds.StoredNameFor(BackgroundKind.Image, "clip.mp4"));
        Assert.IsNull(SongBackgrounds.StoredNameFor(BackgroundKind.Video, "sky.png"));
    }

    [TestMethod]
    public void NamesAreMatchedIgnoringCaseAndOnlyAtTheTop()
    {
        Assert.AreEqual(BackgroundKind.Image, SongBackgrounds.KindOfName("Background.JPG"));
        Assert.AreEqual(BackgroundKind.Video, SongBackgrounds.KindOfName("VIDEO.mp4"));
        Assert.IsNull(SongBackgrounds.KindOfName("extras/video.mp4"));
        Assert.IsNull(SongBackgrounds.KindOfName("album.png"));
        Assert.IsNull(SongBackgrounds.KindOfName("background.mp4"));
        Assert.IsNull(SongBackgrounds.KindOfName("video.png"));
        Assert.IsNull(SongBackgrounds.KindOfName("highway.png"));
        Assert.IsTrue(SongBackgrounds.IsName("video.ogv", BackgroundKind.Video));
        Assert.IsFalse(SongBackgrounds.IsName("video.ogv", BackgroundKind.Image));
    }

    [TestMethod]
    public void PickPrefersTheEarlierExtension()
    {
        string[] names = ["video.webm", "Background.jpeg", "VIDEO.mp4", "background.jpg", "notes.chart"];

        Assert.AreEqual("VIDEO.mp4", SongBackgrounds.Pick(names, n => n, BackgroundKind.Video));
        Assert.AreEqual("background.jpg", SongBackgrounds.Pick(names, n => n, BackgroundKind.Image));
        Assert.IsNull(SongBackgrounds.Pick(["notes.chart"], n => n, BackgroundKind.Image));
    }

    [TestMethod]
    public void EveryBackgroundNameIsRegistered()
    {
        foreach (var kind in SongBackgrounds.Kinds)
        {
            foreach (string extension in SongBackgrounds.ExtensionsOf(kind))
                Assert.IsTrue(FilenameRules.IsRegistered(SongBackgrounds.StemOf(kind) + extension));
        }
    }
}
