using ChartShared.Core.Launching;

namespace ChartShared.Core.Tests.Launching;

[TestClass]
public sealed class RecentJumpListTests
{
    private static readonly string Root = Path.GetFullPath("Songs");

    [TestMethod]
    public void TheLatestIsFirstAndEachSongIsListedOnce()
    {
        var entries = RecentJumpList.Build([Song("A"), Song("B"), Song("A", "A again")], _ => true);

        Assert.AreSequenceEqual(new[] { "A", "B" }, [.. entries.Select(e => e.Title)]);
    }

    [TestMethod]
    public void SongsThatAreGoneAreLeftOut()
    {
        var entries = RecentJumpList.Build([Song("A"), Song("B")], path => path.EndsWith("B", StringComparison.Ordinal));

        Assert.AreSequenceEqual(new[] { "B" }, [.. entries.Select(e => e.Title)]);
    }

    [TestMethod]
    public void NoMoreThanTheMostAreListed()
    {
        var songs = Enumerable.Range(0, RecentSongs.Max + 5).Select(i => Song($"Song {i}"));

        Assert.HasCount(RecentSongs.Max, RecentJumpList.Build(songs, _ => true));
    }

    [TestMethod]
    public void TheEntryShowsTheFullPathAndOpensTheSongWithItQuoted()
    {
        var entry = RecentJumpList.Build([Song("My Song")], _ => true).Single();

        Assert.AreEqual(Path.Combine(Root, "My Song"), entry.Path);
        Assert.AreEqual("\"" + Path.Combine(Root, "My Song") + "\"", entry.Arguments);
    }

    [TestMethod]
    public void ArgumentsReadBackAsTheSongPath()
    {
        var entry = RecentJumpList.Build([Song("My Song")], _ => true).Single();

        var args = BuilderArgs.Parse([entry.Path]);

        Assert.AreEqual(entry.Path, args.SongPath);
    }

    [TestMethod]
    public void ASongWithNoTitleIsNamedForItsFolder()
    {
        var entry = RecentJumpList.Build([Song("Folder Name", "")], _ => true).Single();

        Assert.AreEqual("Folder Name", entry.Title);
    }

    [TestMethod]
    public void SongsTheUserRemovedAreLeftOut()
    {
        string removed = RecentJumpList.Quote(Path.Combine(Root, "A"));

        var entries = RecentJumpList.Build([Song("A"), Song("B")], _ => true, [removed.ToUpperInvariant()]);

        Assert.AreSequenceEqual(new[] { "B" }, [.. entries.Select(e => e.Title)]);
    }

    [TestMethod]
    public void BlankAndUnusablePathsAreLeftOut()
    {
        var entries = RecentJumpList.Build(
            [new RecentSong { Path = "", Title = "Blank" }, new RecentSong { Path = "bad\0path", Title = "Bad" }, Song("A")],
            _ => true);

        Assert.AreSequenceEqual(new[] { "A" }, [.. entries.Select(e => e.Title)]);
    }

    [TestMethod]
    public void TrailingBackslashesAreDoubledSoTheyDontEscapeTheQuote()
    {
        Assert.AreEqual("\"C:\\\\\"", RecentJumpList.Quote("C:\\"));
        Assert.AreEqual("\"C:\\a b\"", RecentJumpList.Quote("C:\\a b"));
    }

    private static RecentSong Song(string name, string? title = null) =>
        new() { Path = Path.Combine(Root, name), Title = title ?? name };
}
