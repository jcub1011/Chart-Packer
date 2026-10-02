using ChartPacker.Core.Trash;

namespace ChartPacker.Core.Tests.Trash;

[TestClass]
public sealed class FreedesktopTrashTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 34, 56);

    private DirectoryInfo _root = null!;
    private string _home = null!;
    private string _drive = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-trash-");
        _home = _root.CreateSubdirectory("home").FullName;
        _drive = _root.CreateSubdirectory("drive").FullName;
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AnItemOnTheHomeMountGoesToTheHomeTrashWithItsInfo()
    {
        string song = Write(Path.Combine(_home, "My Songs", "Song ü.sng"));

        var result = Trash().MoveToTrash(song, isDirectory: false);

        Assert.AreEqual(TrashStatus.Trashed, result.Status);
        Assert.IsFalse(File.Exists(song));
        string trash = Path.Combine(_home, "Trash");
        Assert.IsTrue(File.Exists(Path.Combine(trash, "files", "Song ü.sng")));
        string info = File.ReadAllText(Path.Combine(trash, "info", "Song ü.sng.trashinfo"));
        Assert.AreEqual($"[Trash Info]\nPath={FreedesktopTrash.Escape(song)}\nDeletionDate=2026-09-30T12:34:56\n", info);
        Assert.Contains("Song%20%C3%BC.sng", info);
    }

    [TestMethod]
    public void ASecondItemWithTheSameNameGetsANumber()
    {
        var trash = Trash();
        trash.MoveToTrash(Write(Path.Combine(_home, "a", "Song.sng")), isDirectory: false);

        var result = trash.MoveToTrash(Write(Path.Combine(_home, "b", "Song.sng")), isDirectory: false);

        Assert.AreEqual(TrashStatus.Trashed, result.Status);
        Assert.IsTrue(File.Exists(Path.Combine(_home, "Trash", "files", "Song.sng.2")));
        Assert.IsTrue(File.Exists(Path.Combine(_home, "Trash", "info", "Song.sng.2.trashinfo")));
    }

    [TestMethod]
    public void AFolderOnAnotherMountGoesToThatMountsOwnTrash()
    {
        string folder = Path.Combine(_drive, "Songs", "Song");
        Directory.CreateDirectory(folder);
        Write(Path.Combine(folder, "notes.chart"));

        var result = Trash().MoveToTrash(folder, isDirectory: true);

        Assert.AreEqual(TrashStatus.Trashed, result.Status);
        string trash = Path.Combine(_drive, ".Trash-1000");
        Assert.IsTrue(File.Exists(Path.Combine(trash, "files", "Song", "notes.chart")));
        Assert.Contains("Path=Songs/Song\n", File.ReadAllText(Path.Combine(trash, "info", "Song.trashinfo")));
    }

    [TestMethod]
    public void AnItemReachedThroughALinkGoesToTheTrashOfTheMountItIsReallyOn()
    {
        // As if "home/link" were a symbolic link to "drive/real".
        string link = Path.Combine(_home, "link");
        string real = Path.Combine(_drive, "real");
        string Resolve(string path) =>
            path == link || path.StartsWith(link + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? real + path[link.Length..] : path;
        string song = Write(Path.Combine(link, "Song.sng"));

        var result = new FreedesktopTrash(Path.Combine(_home, "Trash"), MountOf, "1000", () => Now, Resolve).MoveToTrash(song, isDirectory: false);

        Assert.AreEqual(TrashStatus.Trashed, result.Status);
        string trash = Path.Combine(_drive, ".Trash-1000");
        Assert.IsTrue(File.Exists(Path.Combine(trash, "files", "Song.sng")));
        Assert.Contains("Path=real/Song.sng\n", File.ReadAllText(Path.Combine(trash, "info", "Song.sng.trashinfo")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_home, "Trash")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void LinksAreFollowedUpToThePartThatDoesNotExistYet()
    {
        string real = Directory.CreateDirectory(Path.Combine(_drive, "real")).FullName;
        string link = Path.Combine(_home, "link");
        Directory.CreateSymbolicLink(link, real);

        Assert.AreEqual(FreedesktopTrash.ResolveLinks(real), FreedesktopTrash.ResolveLinks(link));
        Assert.AreEqual(Path.Combine(FreedesktopTrash.ResolveLinks(real), "Trash", "files"), FreedesktopTrash.ResolveLinks(Path.Combine(link, "Trash", "files")));
    }

    [TestMethod]
    public void AnItemOnNoKnownMountFailsAndStays()
    {
        string song = Write(Path.Combine(_root.FullName, "elsewhere", "Song.sng"));

        var result = Trash().MoveToTrash(song, isDirectory: false);

        Assert.AreEqual(TrashStatus.Failed, result.Status);
        Assert.IsTrue(File.Exists(song));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void AFailedMoveRemovesItsInfo()
    {
        // Only Windows refuses to move a file that's open.
        string song = Write(Path.Combine(_home, "Song.sng"));
        using var locked = new FileStream(song, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = Trash().MoveToTrash(song, isDirectory: false);

        Assert.AreEqual(TrashStatus.Failed, result.Status);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(_home, "Trash", "info")));
    }

    [TestMethod]
    public void EscapingKeepsSlashesAndUnreservedCharacters() =>
        Assert.AreEqual("/a%20b/c-d_e.f~g%25", FreedesktopTrash.Escape("/a b/c-d_e.f~g%"));

    private FreedesktopTrash Trash() =>
        new(Path.Combine(_home, "Trash"), MountOf, "1000", () => Now);

    private string? MountOf(string path)
    {
        foreach (string mount in new[] { _home, _drive })
        {
            if (path == mount || path.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return mount;
        }

        return null;
    }

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "data");
        return path;
    }
}

[TestClass]
public sealed class MountTableTests
{
    private const string MountInfo =
        "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "30 22 0:26 / /home rw,relatime shared:5 - ext4 /dev/sda2 rw\n" +
        "41 22 8:17 / /media/me/My\\040Drive rw,nosuid - exfat /dev/sdb1 rw\n";

    [TestMethod]
    public void TheLongestMountPointWins()
    {
        var mounts = MountTable.Parse(MountInfo);

        Assert.AreEqual("/home", mounts.MountPointOf("/home/me/Songs/a.sng"));
        Assert.AreEqual("/", mounts.MountPointOf("/homework/a.sng"));
        Assert.AreEqual("/media/me/My Drive", mounts.MountPointOf("/media/me/My Drive/Songs"));
        Assert.AreEqual("/home", mounts.MountPointOf("/home"));
    }
}
