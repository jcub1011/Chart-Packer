using ChartPacker.Core.Ini;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Tests.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Validation;

[TestClass]
public sealed class SongMetadataCheckTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-check-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task ReadsAFoldersSongIniWithItsRepeatedKeys()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "Song.INI"), "[song]\nname = A\ndiff_guitar = hard\nname = B\n");

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"));

        Assert.IsNull(report.ReadError);
        Assert.AreEqual(SongMetadataStatus.Ok, report.Status);
        Assert.IsTrue(report.CanEdit);
        CollectionAssert.AreEqual(new[] { P("name", "B"), P("diff_guitar", "hard") }, report.Pairs.ToArray());
        CollectionAssert.AreEqual(new[] { "name" }, report.DuplicateKeys.ToArray());
        Assert.AreEqual(Path.Combine(folder, "Song.INI"), report.SongIniPath);
    }

    [TestMethod]
    public async Task AFolderWithoutSongIniHasNoMetadata()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"));

        Assert.IsNull(report.ReadError);
        Assert.AreEqual(SongMetadataStatus.NoSongIni, report.Status);
        Assert.IsTrue(report.CanEdit);
        Assert.IsEmpty(report.Pairs);
        Assert.AreEqual(Path.Combine(folder, "song.ini"), report.SongIniPath);
    }

    [TestMethod]
    public async Task ASongIniWithoutASongSectionHasNoMetadataButCanBeEdited()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "song.ini"), "[other]\nname = A\n");

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"));

        Assert.AreEqual(SongMetadataStatus.NoSongSection, report.Status);
        Assert.IsTrue(report.CanEdit);
        Assert.IsEmpty(report.Pairs);
    }

    [TestMethod]
    public async Task ASongIniTooLargeToReadCantBeEdited()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, "song.ini"), new byte[SongIni.MaxFileSize + 1]);

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"));

        Assert.AreEqual(SongMetadataStatus.SongIniTooLarge, report.Status);
        Assert.IsFalse(report.CanEdit);
        Assert.IsNotNull(report.ReadError);
    }

    [TestMethod]
    public async Task AnSngWithStructuralErrorsIsReadButCantBeEdited()
    {
        string path = Path.Combine(_root.FullName, "song.sng");
        var builder = new SngBuilder().AddMetadata("pro_drums", "1");
        builder.FileDataLen = 5;
        builder.Trailing = [0, 0];
        await File.WriteAllBytesAsync(path, builder.Build());

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "song.sng", "song"));

        Assert.AreEqual(SongMetadataStatus.SngHasErrors, report.Status);
        Assert.IsFalse(report.CanEdit);
        Assert.IsNull(report.ReadError);
        CollectionAssert.AreEqual(new[] { P("pro_drums", "1") }, report.Pairs.ToArray());
    }

    [TestMethod]
    public async Task ReadsAnSngsPairsKeepingDuplicates()
    {
        string path = Path.Combine(_root.FullName, "song.sng");
        byte[] bytes = new SngBuilder().AddMetadata("name", "A").AddMetadata("Name", "B").AddMetadata("pro_drums", "true").Build();
        await File.WriteAllBytesAsync(path, bytes);

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "song.sng", "song"));

        Assert.IsNull(report.ReadError);
        CollectionAssert.AreEqual(new[] { P("name", "A"), P("Name", "B"), P("pro_drums", "true") }, report.Pairs.ToArray());
        Assert.IsNull(report.SongIniPath);
    }

    [TestMethod]
    public async Task AMissingSongIsReportedNotThrown()
    {
        string folder = Path.Combine(_root.FullName, "Gone");

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Gone", "Gone"));

        Assert.IsNotNull(report.ReadError);
        Assert.AreEqual(SongMetadataStatus.Unreadable, report.Status);
        Assert.IsFalse(report.CanEdit);
    }

    private static KeyValuePair<string, string> P(string key, string value) => new(key, value);
}
