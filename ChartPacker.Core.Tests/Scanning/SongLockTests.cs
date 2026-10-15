using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests.Scanning;

/// <summary>
/// <see cref="SongLock"/>, with two locks in one process standing in for two processes (charter-spec §12): each opens
/// the lock file for itself, so the OS refuses the second as it would another process.
/// </summary>
[TestClass]
public sealed class SongLockTests
{
    private string _locks = "";
    private string _song = "";

    [TestInitialize]
    public void CreateFolders()
    {
        string root = Directory.CreateTempSubdirectory("chartpacker-locks-").FullName;
        _locks = Path.Combine(root, "locks");
        _song = Path.Combine(root, "Songs", "Artist - Title");
    }

    [TestCleanup]
    public void DeleteFolders() => Directory.Delete(Path.GetDirectoryName(_locks)!, recursive: true);

    [TestMethod]
    public void ASongHeldIsRefusedUntilItsLetGo()
    {
        Assert.IsFalse(SongLock.IsHeld(_song, _locks), "Nothing has held it yet.");

        using (var held = SongLock.TryAcquire(_song, _locks))
        {
            Assert.IsNotNull(held);
            Assert.AreEqual(_song, held.SongPath);
            Assert.IsNull(SongLock.TryAcquire(_song, _locks));
            Assert.IsTrue(SongLock.IsHeld(_song, _locks));
        }

        Assert.IsFalse(SongLock.IsHeld(_song, _locks));
        using var again = SongLock.TryAcquire(_song, _locks);
        Assert.IsNotNull(again);
    }

    [TestMethod]
    public void OtherSongsAreNotHeld()
    {
        using var held = SongLock.TryAcquire(_song, _locks);

        Assert.IsFalse(SongLock.IsHeld(_song + " (2)", _locks));
        using var other = SongLock.TryAcquire(_song + ".sng", _locks);
        Assert.IsNotNull(other);
    }

    [TestMethod]
    public void TheSamePathWrittenAnotherWayIsTheSameSong()
    {
        using var held = SongLock.TryAcquire(_song, _locks);

        Assert.IsTrue(SongLock.IsHeld(_song + Path.DirectorySeparatorChar, _locks));
        Assert.IsTrue(SongLock.IsHeld(Path.Combine(_song, "..", Path.GetFileName(_song)), _locks));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.IsTrue(SongLock.IsHeld(_song.ToUpperInvariant(), _locks), "The file system ignores case.");
    }

    [TestMethod]
    public void TheLockFileIsNamedForTheSongAndHoldsNothing()
    {
        using (SongLock.TryAcquire(_song, _locks))
        {
        }

        string file = Assert.ContainsSingle(Directory.GetFiles(_locks));
        Assert.AreEqual(SongLock.FileNameOf(_song), Path.GetFileName(file));
        Assert.AreEqual(32, Path.GetFileName(file).Length);
        Assert.AreEqual(0, new FileInfo(file).Length);
    }
}
