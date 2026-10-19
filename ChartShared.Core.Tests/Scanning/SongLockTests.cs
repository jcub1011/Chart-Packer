using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Scanning;

/// <summary>
/// <see cref="SongLock"/>, with two locks in one process standing in for two processes (charter-spec §12): each opens
/// the lock file for itself, so the OS refuses the second as it would another process.
/// </summary>
[TestClass]
public sealed class SongLockTests
{
    private string _root = "";
    private string _folderSong = "";
    private string _sngSong = "";

    [TestInitialize]
    public void CreateFolders()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-locks-").FullName;
        _folderSong = Path.Combine(_root, "Artist - Title");
        _sngSong = Path.Combine(_root, "Artist - Other.sng");
        Directory.CreateDirectory(_folderSong);
        File.WriteAllBytes(_sngSong, [1]);
    }

    [TestCleanup]
    public void DeleteFolders() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ASongHeldIsRefusedUntilItsLetGo(bool sng)
    {
        string song = sng ? _sngSong : _folderSong;
        Assert.IsNull(SongLock.HolderOf(song), "Nothing has held it yet.");

        using (var held = SongLock.TryAcquire(song, SongLockHolder.ChartBuilder))
        {
            Assert.IsNotNull(held);
            Assert.IsTrue(held.Holds);
            Assert.AreEqual(song, held.SongPath);
            Assert.IsNull(SongLock.TryAcquire(song, SongLockHolder.ChartPacker));
            Assert.IsNotNull(SongLock.HolderOf(song));
        }

        Assert.IsNull(SongLock.HolderOf(song));
        using var again = SongLock.TryAcquire(song, SongLockHolder.ChartPacker);
        Assert.IsNotNull(again);
        Assert.IsTrue(again.Holds);
    }

    [TestMethod]
    public void TheLockFileIsBesideTheSong()
    {
        Assert.AreEqual(_sngSong + ".lock", SongLock.LockPathOf(_sngSong));
        Assert.AreEqual(_folderSong + ".lock", SongLock.LockPathOf(_folderSong + Path.DirectorySeparatorChar));

        using (SongLock.TryAcquire(_folderSong, SongLockHolder.ChartBuilder))
        {
            Assert.IsTrue(File.Exists(_folderSong + ".lock"));
            Assert.IsEmpty(Directory.GetFileSystemEntries(_folderSong), "Nothing is put in the song folder.");
        }
    }

    [TestMethod]
    [DataRow(SongLockHolder.ChartBuilder)]
    [DataRow(SongLockHolder.ChartPacker)]
    public void TheHolderIsTold(SongLockHolder holder)
    {
        Assert.IsNull(SongLock.HolderOf(_sngSong));

        using (SongLock.TryAcquire(_sngSong, holder))
        {
            // Only Windows lets a held lock file be read.
            SongLockHolder expected = OperatingSystem.IsWindows() ? holder : SongLockHolder.Unknown;
            Assert.AreEqual(expected, SongLock.HolderOf(_sngSong));
        }

        Assert.IsNull(SongLock.HolderOf(_sngSong));
    }

    [TestMethod]
    public void TheLockFileIsGoneOnceItsLetGo()
    {
        using (SongLock.TryAcquire(_sngSong, SongLockHolder.ChartBuilder))
        {
        }

        Assert.IsFalse(File.Exists(SongLock.LockPathOf(_sngSong)));
    }

    [TestMethod]
    public void ALockFileLeftBehindMeansNothing()
    {
        // As a crash before the file could be deleted leaves it.
        string leftOver = SongLock.LockPathOf(_sngSong);
        File.WriteAllText(leftOver, nameof(SongLockHolder.ChartBuilder));

        Assert.IsNull(SongLock.HolderOf(_sngSong));
        using (var held = SongLock.TryAcquire(_sngSong, SongLockHolder.ChartPacker))
        {
            Assert.IsNotNull(held);
            Assert.IsTrue(held.Holds);
        }

        Assert.IsFalse(File.Exists(leftOver));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void AHiddenLockFileLeftBehindMeansNothing()
    {
        // Windows hides a lock file, and a hidden file can't be overwritten as a plain one can.
        string leftOver = SongLock.LockPathOf(_sngSong);
        File.WriteAllText(leftOver, nameof(SongLockHolder.ChartBuilder));
        File.SetAttributes(leftOver, FileAttributes.Hidden);

        using (var held = SongLock.TryAcquire(_sngSong, SongLockHolder.ChartPacker))
            Assert.IsNotNull(held);

        Assert.IsFalse(File.Exists(leftOver));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ASongInAFolderNotMadeYetIsHeld(bool sng)
    {
        // As a song about to be written for the first time is: its folder is made, so the lock can be.
        string song = Path.Combine(_root, "New", sng ? "Song.sng" : "Song");

        using (var songLock = SongLock.TryAcquire(song, SongLockHolder.ChartBuilder))
        {
            Assert.IsNotNull(songLock);
            Assert.IsTrue(songLock.Holds);
            Assert.IsNull(SongLock.TryAcquire(song, SongLockHolder.ChartPacker));
        }

        Assert.IsTrue(Directory.Exists(Path.Combine(_root, "New")));
    }

    [TestMethod]
    public void ASongWhereNothingCanBeWrittenIsntHeld()
    {
        // Nothing can write a song under a file, so there's nothing to keep out.
        string song = Path.Combine(_sngSong, "Under a file", "Song.sng");

        using var songLock = SongLock.TryAcquire(song, SongLockHolder.ChartBuilder);

        Assert.IsNotNull(songLock);
        Assert.IsFalse(songLock.Holds);
        Assert.IsNull(SongLock.HolderOf(song));
    }

    [TestMethod]
    public void AnEmptyLockFileLeftBehindMeansNothing()
    {
        // As a holder that crashed before it wrote its name leaves it.
        string leftOver = SongLock.LockPathOf(_sngSong);
        File.WriteAllBytes(leftOver, []);

        Assert.IsNull(SongLock.HolderOf(_sngSong));
        using var held = SongLock.TryAcquire(_sngSong, SongLockHolder.ChartPacker);
        Assert.IsNotNull(held);
        Assert.IsTrue(held.Holds);
    }

    [TestMethod]
    public void OtherSongsAreNotHeld()
    {
        using var held = SongLock.TryAcquire(_folderSong, SongLockHolder.ChartBuilder);

        Assert.IsNull(SongLock.HolderOf(_folderSong + " (2)"));
        using var other = SongLock.TryAcquire(_folderSong + ".sng", SongLockHolder.ChartBuilder);
        Assert.IsNotNull(other);
    }

    [TestMethod]
    public void TheSamePathWrittenAnotherWayIsTheSameSong()
    {
        using var held = SongLock.TryAcquire(_folderSong, SongLockHolder.ChartBuilder);

        Assert.IsNotNull(SongLock.HolderOf(_folderSong + Path.DirectorySeparatorChar));
        Assert.IsNotNull(SongLock.HolderOf(Path.Combine(_folderSong, "..", Path.GetFileName(_folderSong))));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.IsNotNull(SongLock.HolderOf(_folderSong.ToUpperInvariant()), "The file system ignores case.");
    }

    [TestMethod]
    public void EveryWayOfWritingAPathHasTheSameKey()
    {
        string key = SongLock.KeyOf(_folderSong);

        Assert.AreEqual(32, key.Length);
        Assert.AreEqual(key, SongLock.KeyOf(_folderSong + Path.DirectorySeparatorChar));
        Assert.AreEqual(key, SongLock.KeyOf(Path.Combine(_folderSong, "..", Path.GetFileName(_folderSong))));
        Assert.AreNotEqual(key, SongLock.KeyOf(_folderSong + " (2)"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void KeysAreWhatTheyWere()
    {
        // Recovery files are named by the key, so a changed key would lose them.
        Assert.AreEqual("5787f6f1e1a7a879ae9d2c901709188b", SongLock.KeyOf(@"C:\Songs\Artist - Title"));
    }
}
