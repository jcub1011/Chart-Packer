using ChartShared.Core.Ini;
using ChartShared.Core.Scanning;
using ChartShared.Core.Tests.Sng;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Tests.Validation;

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
        await File.WriteAllTextAsync(Path.Combine(folder, "Song.INI"), "[song]\nname = A\ndiff_guitar = hard\nname = B\n", TestContext.CancellationToken);

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"), TestContext.CancellationToken);

        Assert.IsNull(report.ReadError);
        Assert.AreEqual(SongMetadataStatus.Ok, report.Status);
        Assert.IsTrue(report.CanEdit);
        Assert.AreSequenceEqual([P("name", "B"), P("diff_guitar", "hard")], [.. report.Pairs]);
        Assert.AreSequenceEqual(["name"], [.. report.DuplicateKeys]);
        Assert.AreEqual(Path.Combine(folder, "Song.INI"), report.SongIniPath);
    }

    [TestMethod]
    public async Task AFolderWithoutSongIniHasNoMetadata()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"), TestContext.CancellationToken);

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
        await File.WriteAllTextAsync(Path.Combine(folder, "song.ini"), "[other]\nname = A\n", TestContext.CancellationToken);

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"), TestContext.CancellationToken);

        Assert.AreEqual(SongMetadataStatus.NoSongSection, report.Status);
        Assert.IsTrue(report.CanEdit);
        Assert.IsEmpty(report.Pairs);
    }

    [TestMethod]
    public async Task ASongIniTooLargeToReadCantBeEdited()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, "song.ini"), new byte[SongIni.MaxFileSize + 1], TestContext.CancellationToken);

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"), TestContext.CancellationToken);

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
        await File.WriteAllBytesAsync(path, builder.Build(), TestContext.CancellationToken);

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "song.sng", "song"), TestContext.CancellationToken);

        Assert.AreEqual(SongMetadataStatus.SngHasErrors, report.Status);
        Assert.IsFalse(report.CanEdit);
        Assert.IsNull(report.ReadError);
        Assert.AreSequenceEqual([P("pro_drums", "1")], [.. report.Pairs]);
    }

    [TestMethod]
    public async Task ReadsAnSngsPairsKeepingDuplicates()
    {
        string path = Path.Combine(_root.FullName, "song.sng");
        byte[] bytes = new SngBuilder().AddMetadata("name", "A").AddMetadata("Name", "B").AddMetadata("pro_drums", "true").Build();
        await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken);

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "song.sng", "song"), TestContext.CancellationToken);

        Assert.IsNull(report.ReadError);
        Assert.AreSequenceEqual([P("name", "A"), P("Name", "B"), P("pro_drums", "true")], [.. report.Pairs]);
        Assert.IsNull(report.SongIniPath);
    }

    [TestMethod]
    public async Task AMissingSongIsReportedNotThrown()
    {
        string folder = Path.Combine(_root.FullName, "Gone");

        var report = await SongMetadataCheck.ReadAsync(new SongEntry(SongEntryKind.SongFolder, folder, "Gone", "Gone"), TestContext.CancellationToken);

        Assert.IsNotNull(report.ReadError);
        Assert.AreEqual(SongMetadataStatus.Unreadable, report.Status);
        Assert.IsFalse(report.CanEdit);
    }

    private static KeyValuePair<string, string> P(string key, string value) => new(key, value);

    public TestContext TestContext { get; set; }
}
