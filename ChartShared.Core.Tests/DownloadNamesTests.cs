using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests;

[TestClass]
public sealed class DownloadNamesTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-downloadnames-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    [DataRow("Artist", "Title", "Artist - Title")]
    [DataRow(null, "Title", "Title")]
    [DataRow("  ", " Title ", "Title")]
    [DataRow("Artist", null, "Song Folder")]
    [DataRow(null, "", "Song Folder")]
    [DataRow("AC/DC", "Back: In Black?", "AC_DC - Back_ In Black_")]
    public void SongPrefixNamesTheSong(string? artist, string? title, string expected)
    {
        Assert.AreEqual(expected, DownloadNames.SongPrefix(artist, title, "Song Folder"));
    }

    [TestMethod]
    [DataRow("Artist - Title", "album.png", "Artist - Title - album.png")]
    [DataRow("Artist - Title", "extras/readme.txt", "Artist - Title - readme.txt")]
    [DataRow("", "notes.mid", "notes.mid")]
    public void FileNameJoinsThePrefixAndTheFile(string prefix, string storedName, string expected)
    {
        Assert.AreEqual(expected, DownloadNames.FileName(prefix, storedName));
    }

    [TestMethod]
    [DataRow("a<b>c|d", "a_b_c_d")]
    [DataRow("tab\there", "tab_here")]
    [DataRow("trailing. . ", "trailing")]
    [DataRow("CON", "CON_")]
    [DataRow("con.txt", "con_.txt")]
    [DataRow("Console.txt", "Console.txt")]
    [DataRow("...", "_")]
    [DataRow("", "_")]
    public void SafeNameIsAValidFileName(string name, string expected)
    {
        Assert.AreEqual(expected, DownloadNames.SafeName(name));
    }

    [TestMethod]
    public void UniquePathNumbersAFileWhoseNameIsTaken()
    {
        string folder = _root.FullName;
        Assert.AreEqual(Path.Combine(folder, "album.png"), DownloadNames.UniquePath(folder, "album.png"));

        File.WriteAllBytes(Path.Combine(folder, "album.png"), [1]);
        Directory.CreateDirectory(Path.Combine(folder, "album (2).png"));

        Assert.AreEqual(Path.Combine(folder, "album (3).png"), DownloadNames.UniquePath(folder, "album.png"));
    }

    [TestMethod]
    public void UniquePathNumbersAFolderWithoutTakingItsDotForAnExtension()
    {
        string folder = _root.FullName;
        Directory.CreateDirectory(Path.Combine(folder, "Mr. Artist - Song"));
        File.WriteAllBytes(Path.Combine(folder, "Mr. Artist - Song (2)"), [1]);

        Assert.AreEqual(Path.Combine(folder, "Mr. Artist - Song (3)"), DownloadNames.UniquePath(folder, "Mr. Artist - Song", isFolder: true));
    }
}
