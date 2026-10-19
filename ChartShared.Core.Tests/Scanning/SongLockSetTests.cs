using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Scanning;

/// <summary>
/// <see cref="SongLockSet"/>, with a <see cref="SongLock"/> taken directly standing in for another process (charter-spec
/// §12).
/// </summary>
[TestClass]
public sealed class SongLockSetTests
{
    private string _root = "";
    private string _song = "";

    [TestInitialize]
    public void CreateSong()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-lockset-").FullName;
        _song = Path.Combine(_root, "Artist - Title.sng");
        File.WriteAllBytes(_song, [1]);
    }

    [TestCleanup]
    public void DeleteSong() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void ASongIsHeldUntilItsLastHoldIsLetGo()
    {
        var set = new SongLockSet(SongLockHolder.ChartPacker);

        IDisposable? first = set.TryHold(_song);
        IDisposable? second = set.TryHold(_song);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);

        first.Dispose();
        first.Dispose();
        Assert.IsTrue(set.Holds(_song), "Letting go of one hold twice counts once.");
        Assert.IsNotNull(SongLock.HolderOf(_song));

        second.Dispose();
        Assert.IsFalse(set.Holds(_song));
        Assert.IsNull(SongLock.HolderOf(_song));
    }

    [TestMethod]
    public void ASongAnotherProcessHoldsIsRefused()
    {
        var set = new SongLockSet(SongLockHolder.ChartPacker);
        using (SongLock.TryAcquire(_song, SongLockHolder.ChartBuilder))
        {
            Assert.IsNull(set.TryHold(_song));
            Assert.IsFalse(set.Holds(_song));
            Assert.IsNotNull(set.HolderElsewhere(_song));
            if (OperatingSystem.IsWindows())
                Assert.AreEqual(SongLockHolder.ChartBuilder, set.HolderElsewhere(_song));
        }

        Assert.IsNull(set.HolderElsewhere(_song));
        using IDisposable? hold = set.TryHold(_song);
        Assert.IsNotNull(hold);
    }

    [TestMethod]
    public void ItsOwnHoldIsntElsewhere()
    {
        var set = new SongLockSet(SongLockHolder.ChartPacker);
        using IDisposable? hold = set.TryHold(_song);

        Assert.IsNull(set.HolderElsewhere(_song));
        Assert.IsNotNull(new SongLockSet(SongLockHolder.ChartPacker).HolderElsewhere(_song), "Another set is another process.");
    }

    [TestMethod]
    public void ThePathWrittenAnotherWayIsTheSameHold()
    {
        var set = new SongLockSet(SongLockHolder.ChartPacker);
        using IDisposable? hold = set.TryHold(_song);

        string other = Path.Combine(_root, "..", Path.GetFileName(_root), Path.GetFileName(_song));
        using IDisposable? again = set.TryHold(other);
        Assert.IsNotNull(again);
        Assert.IsTrue(set.Holds(other));
    }

    [TestMethod]
    public void HoldsTakenAtOnceShareOneLock()
    {
        var set = new SongLockSet(SongLockHolder.ChartPacker);

        var holds = Enumerable.Range(0, 8).AsParallel().Select(_ => set.TryHold(_song)).ToList();

        Assert.IsTrue(holds.All(h => h is not null), "None takes another's taking for another process's.");
        foreach (var hold in holds.Skip(1))
            hold!.Dispose();
        Assert.IsTrue(set.Holds(_song));
        holds[0]!.Dispose();
        Assert.IsNull(SongLock.HolderOf(_song));
    }

    [TestMethod]
    public void HoldersAreToldForManySongsAtOnce()
    {
        string other = Directory.CreateDirectory(Path.Combine(_root, "Other")).FullName;
        string folderSong = Directory.CreateDirectory(Path.Combine(other, "Artist - Folder")).FullName;
        string free = Path.Combine(other, "Free.sng");
        string missing = Path.Combine(_root, "Missing", "Song.sng");
        File.WriteAllBytes(free, [1]);

        var set = new SongLockSet(SongLockHolder.ChartPacker);
        using IDisposable? own = set.TryHold(free);
        using (SongLock.TryAcquire(_song, SongLockHolder.ChartBuilder))
        using (SongLock.TryAcquire(folderSong, SongLockHolder.ChartBuilder))
        {
            string[] songs = [_song, folderSong, free, missing];

            var holders = set.HoldersElsewhere(songs);

            CollectionAssert.AreEqual(songs.Select(set.HolderElsewhere).ToList(), holders.ToList());
            Assert.IsNotNull(holders[0]);
            Assert.IsNotNull(holders[1]);
            Assert.IsNull(holders[2], "Its own hold isn't elsewhere.");
            Assert.IsNull(holders[3]);
        }

        Assert.IsTrue(set.HoldersElsewhere([_song, folderSong]).All(h => h is null));
    }
}
