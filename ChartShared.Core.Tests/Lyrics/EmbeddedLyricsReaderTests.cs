using ATL;
using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class EmbeddedLyricsReaderTests
{
    private static LyricsInfo Synced(params (int Start, string Text)[] phrases)
    {
        var info = new LyricsInfo { ContentType = LyricsInfo.LyricsType.LYRICS, Format = LyricsInfo.LyricsFormat.SYNCHRONIZED };
        foreach (var (start, text) in phrases)
            info.SynchronizedLyrics.Add(new LyricsInfo.LyricsPhrase(start, text));
        return info;
    }

    [TestMethod]
    public void TimedPhrasesAreLines()
    {
        var document = EmbeddedLyricsReader.Of([Synced((1000, "Hello world"), (3500, "Again"))]);

        Assert.IsNotNull(document);
        Assert.HasCount(2, document.Lines);
        Assert.AreEqual(TimeSpan.FromSeconds(1), document.Lines[0].Start);
        Assert.AreSequenceEqual(["Hello", "world"], [.. document.Lines[0].Words.Select(static w => w.Text)]);
    }

    [TestMethod]
    public void EntriesWithLineFeedsAreSyllables()
    {
        var document = EmbeddedLyricsReader.Of([Synced((0, "Hel"), (400, "lo \n"), (1000, "big"), (1500, " world"))]);

        Assert.IsNotNull(document);
        Assert.HasCount(2, document.Lines);
        Assert.AreSequenceEqual(["Hel-", "lo"], [.. document.Lines[0].Words.Select(static w => w.Text)]);
        Assert.AreSequenceEqual(["big", "world"], [.. document.Lines[1].Words.Select(static w => w.Text)]);
    }

    [TestMethod]
    public void UnsyncedTextThatIsLrcIsReadAsLrc()
    {
        var info = new LyricsInfo { ContentType = LyricsInfo.LyricsType.LYRICS, UnsynchronizedLyrics = "[00:05.00]Hi there\n" };

        var document = EmbeddedLyricsReader.Of([info]);

        Assert.IsNotNull(document);
        Assert.AreEqual(TimeSpan.FromSeconds(5), document.Lines[0].Start);
    }

    [TestMethod]
    public void PlainUnsyncedTextHasNoTimesToPlaceItBy()
    {
        var info = new LyricsInfo { ContentType = LyricsInfo.LyricsType.LYRICS, UnsynchronizedLyrics = "just words\nno times\n" };

        Assert.IsNull(EmbeddedLyricsReader.Of([info]));
    }

    [TestMethod]
    public void AFileOfNoAudioIsRefused()
    {
        var root = Directory.CreateTempSubdirectory("chartpacker-embedded-");
        try
        {
            string path = Path.Combine(root.FullName, "broken.mp3");
            File.WriteAllBytes(path, [1, 2, 3, 4]);

            Assert.ThrowsExactly<ChartShared.Core.Charts.ChartFormatException>(() => EmbeddedLyricsReader.Read(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
