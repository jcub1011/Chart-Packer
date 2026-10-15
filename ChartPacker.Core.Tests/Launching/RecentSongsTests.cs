using ChartPacker.Core.Launching;

namespace ChartPacker.Core.Tests.Launching;

[TestClass]
public sealed class RecentSongsTests
{
    private static readonly string Root = Path.GetFullPath("Songs");

    [TestMethod]
    public void TheLatestIsFirstAndEachSongIsListedOnce()
    {
        var list = RecentSongs.Add(null, Song("A"));
        list = RecentSongs.Add(list, Song("B"));
        list = RecentSongs.Add(list, Song("A", "A again"));

        Assert.AreSequenceEqual(new[] { "A again", "B" }, [.. list.Select(s => s.Title)]);
    }

    [TestMethod]
    public void NoMoreThanTheMostAreKept()
    {
        List<RecentSong> list = [];
        for (int i = 0; i < RecentSongs.Max + 5; i++)
            list = RecentSongs.Add(list, Song($"Song {i}"));

        Assert.HasCount(RecentSongs.Max, list);
        Assert.AreEqual($"Song {RecentSongs.Max + 4}", list[0].Title);
    }

    [TestMethod]
    public void ASongCanBeTakenOffTheList()
    {
        var list = RecentSongs.Add(RecentSongs.Add(null, Song("A")), Song("B"));

        list = RecentSongs.Remove(list, Path.Combine(Root, "A") + Path.DirectorySeparatorChar);

        Assert.AreEqual("B", Assert.ContainsSingle(list).Title);
    }

    [TestMethod]
    public void EntriesWithNoPathAreDropped()
    {
        var list = RecentSongs.Add([new RecentSong { Path = "", Title = "Broken" }], Song("A"));

        Assert.AreEqual("A", Assert.ContainsSingle(list).Title);
    }

    private static RecentSong Song(string folder, string? title = null) =>
        new() { Path = Path.Combine(Root, folder), Title = title ?? folder, OpenedAt = DateTimeOffset.UnixEpoch };
}
