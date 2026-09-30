using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Charts;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class DraftWriterTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-draftwriter-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task SngFilesAreAddedRemovedAndReplaced()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("notes.chart", [1, 2, 3]), ("song.ogg", [4]), ("video.mp4", [5]));
        var draft = new MetadataDraft();
        draft.RemoveFile("VIDEO.mp4");
        draft.AddFile(new AddedFile("guitar.ogg", WriteSource([7, 7]), 2));
        draft.AddFile(new AddedFile("song.ogg", WriteSource([9, 9, 9]), 3));

        var findings = await DraftWriter.SaveSngAsync(sng, draft);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        Assert.IsEmpty(after.Findings);
        CollectionAssert.AreEqual(new[] { "notes.chart", "guitar.ogg", "song.ogg" }, after.Files.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, Extract(sng, after, "notes.chart"));
        CollectionAssert.AreEqual(new byte[] { 7, 7 }, Extract(sng, after, "guitar.ogg"));
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, Extract(sng, after, "song.ogg"));
        CollectionAssert.AreEqual(new[] { ("name", "A") }, after.Metadata.Select(p => (p.Key, p.Value)).ToArray());
    }

    [TestMethod]
    public async Task SngArtAndFileChangesAreSavedTogether()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("album.jpg", [1]), ("notes.chart", [2]));
        string art = Path.Combine(_root.FullName, "cover.png");
        File.WriteAllBytes(art, [8, 8]);
        var draft = new MetadataDraft();
        draft.Set("artist", "Band");
        draft.SetAlbumArt(art);
        draft.RemoveFile("notes.chart");
        draft.AddFile(new AddedFile("notes.mid", WriteSource([6]), 1));

        var findings = await DraftWriter.SaveSngAsync(sng, draft);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        CollectionAssert.AreEquivalent(new[] { "album.png", "notes.mid" }, after.Files.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new byte[] { 8, 8 }, Extract(sng, after, "album.png"));
        CollectionAssert.AreEqual(new[] { ("name", "A"), ("artist", "Band") }, after.Metadata.Select(p => (p.Key, p.Value)).ToArray());
    }

    [TestMethod]
    public async Task SngMetadataOnlyDraftKeepsEveryFile()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("album.jpg", [1]), ("notes.chart", [2]));
        var draft = new MetadataDraft();
        draft.Set("name", "B");

        await DraftWriter.SaveSngAsync(sng, draft);

        var after = SngReader.Read(sng);
        CollectionAssert.AreEqual(new[] { "album.jpg", "notes.chart" }, after.Files.Select(f => f.Name).ToArray());
        Assert.AreEqual("B", after.Metadata.Single().Value);
    }

    [TestMethod]
    public async Task SngBackgroundImageChangeReplacesEveryVariantAndKeepsTheVideo()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("background.jpg", [1]), ("background.png", [2]), ("video.mp4", [3]), ("notes.chart", [4]));
        string image = Path.Combine(_root.FullName, "Sky.JPEG");
        File.WriteAllBytes(image, [8, 8]);
        var draft = new MetadataDraft();
        draft.SetBackground(BackgroundKind.Image, image);

        var findings = await DraftWriter.SaveSngAsync(sng, draft);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        CollectionAssert.AreEquivalent(new[] { "background.jpeg", "video.mp4", "notes.chart" }, after.Files.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new byte[] { 8, 8 }, Extract(sng, after, "background.jpeg"));
        CollectionAssert.AreEqual(new byte[] { 3 }, Extract(sng, after, "video.mp4"));
    }

    [TestMethod]
    public async Task SngArtBackgroundAndFileChangesAreSavedTogether()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("album.jpg", [1]), ("background.png", [2]), ("video.webm", [3]), ("notes.chart", [4]));
        string art = WriteNamedSource("cover.png", [5]);
        string video = WriteNamedSource("clip.mp4", [6, 6]);
        var draft = new MetadataDraft();
        draft.SetAlbumArt(art);
        draft.SetBackground(BackgroundKind.Video, video);
        draft.RemoveBackground(BackgroundKind.Image);
        draft.AddFile(new AddedFile("guitar.ogg", WriteSource([7]), 1));

        var findings = await DraftWriter.SaveSngAsync(sng, draft);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        CollectionAssert.AreEquivalent(new[] { "album.png", "video.mp4", "notes.chart", "guitar.ogg" }, after.Files.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new byte[] { 6, 6 }, Extract(sng, after, "video.mp4"));
    }

    [TestMethod]
    public async Task SngMetadataOnlyDraftKeepsTheBackgrounds()
    {
        string sng = await CreateSngAsync([P("name", "A")], ("background.png", [1]), ("video.mp4", [2]));
        var draft = new MetadataDraft();
        draft.Set("name", "B");

        await DraftWriter.SaveSngAsync(sng, draft);

        CollectionAssert.AreEqual(new[] { "background.png", "video.mp4" }, SngReader.Read(sng).Files.Select(f => f.Name).ToArray());
    }

    [TestMethod]
    public async Task FolderBackgroundChangesTouchOnlyTheirKind()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "background.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "Video.MP4"), [2]);
        File.WriteAllBytes(Path.Combine(folder, "video.avi"), [3]);
        File.WriteAllBytes(Path.Combine(folder, "notes.chart"), [4]);
        string sub = Directory.CreateDirectory(Path.Combine(folder, "extras")).FullName;
        File.WriteAllBytes(Path.Combine(sub, "video.mp4"), [5]);
        string image = WriteNamedSource("sky.png", [9]);
        var draft = new MetadataDraft();
        draft.SetBackground(BackgroundKind.Image, image);
        draft.RemoveBackground(BackgroundKind.Video);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        CollectionAssert.AreEquivalent(new[] { "background.png", "notes.chart" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEqual(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(folder, "background.png")));
        Assert.IsTrue(File.Exists(Path.Combine(sub, "video.mp4")));
    }

    [TestMethod]
    public async Task FolderBackgroundCanBeReplacedByItsOwnFile()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        string own = Path.Combine(folder, "video.webm");
        File.WriteAllBytes(own, [1, 2]);
        File.WriteAllBytes(Path.Combine(folder, "video.mp4"), [3]);
        var draft = new MetadataDraft();
        draft.SetBackground(BackgroundKind.Video, own);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        CollectionAssert.AreEquivalent(new[] { "video.webm" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, File.ReadAllBytes(own));
    }

    [TestMethod]
    public async Task FolderFileChangesAreApplied()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "notes.chart"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "video.mp4"), [2]);
        string songIni = Path.Combine(folder, SongIni.FileName);
        File.WriteAllText(songIni, "[song]\nname = A\n");
        var draft = new MetadataDraft();
        draft.Set("name", "B");
        draft.RemoveFile("video.mp4");
        draft.AddFile(new AddedFile("guitar.ogg", WriteSource([7]), 1));

        await DraftWriter.SaveFolderAsync(folder, songIni, draft);

        CollectionAssert.AreEquivalent(
            new[] { "guitar.ogg", "notes.chart", "song.ini" },
            Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        StringAssert.Contains(File.ReadAllText(songIni), "name = B");
    }

    [TestMethod]
    public async Task FolderChartIsReplacedByTheOtherFormat()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "Notes.Chart"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "song.ogg"), [2]);
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("notes.mid", WriteSource([6, 6]), 2), ["Notes.Chart"]);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        CollectionAssert.AreEquivalent(new[] { "notes.mid", "song.ogg" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEqual(new byte[] { 6, 6 }, File.ReadAllBytes(Path.Combine(folder, "notes.mid")));
    }

    [TestMethod]
    public async Task FolderFileOnlyDraftDoesNotCreateASongIni()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "notes.chart"), [1]);
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("guitar.ogg", WriteSource([7]), 1));

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        CollectionAssert.AreEquivalent(new[] { "guitar.ogg", "notes.chart" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task FolderChartOffsetsRewriteTheStoredChart()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        string chart = Path.Combine(folder, "notes.chart");
        File.WriteAllText(chart, ChartReaderTests.Chart);
        var draft = new MetadataDraft();
        draft.SetChartOffset(ExpertGuitar, 500);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        CollectionAssert.AreEquivalent(new[] { "notes.chart" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(192, ChartReaderTests.Notes(ChartReader.Read(File.ReadAllBytes(chart), "notes.chart"), ChartInstrument.Guitar)[0].Tick);
        Assert.IsEmpty(Directory.GetFiles(Path.GetTempPath(), "chartpacker-*.chart").Where(f => File.GetCreationTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1)));
    }

    [TestMethod]
    public async Task FolderChartOffsetsApplyToAnAddedChart()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "notes.chart"), [1]);
        string source = WriteSource(ChartReaderTests.SampleMidi());
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("notes.mid", source, 1), ["notes.chart"]);
        draft.SetChartOffset(ExpertGuitar, 250);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        CollectionAssert.AreEquivalent(new[] { "notes.mid" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(240, ChartReaderTests.Notes(ChartReader.Read(File.ReadAllBytes(Path.Combine(folder, "notes.mid")), "notes.mid"), ChartInstrument.Guitar)[0].Tick);
        CollectionAssert.AreEqual(ChartReaderTests.SampleMidi(), File.ReadAllBytes(source));
    }

    [TestMethod]
    public async Task FolderChartOffsetThatCantApplyWritesNothing()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllText(Path.Combine(folder, "notes.chart"), ChartReaderTests.Chart);
        string songIni = Path.Combine(folder, SongIni.FileName);
        File.WriteAllText(songIni, "[song]\nname = A\n");
        var draft = new MetadataDraft();
        draft.Set("name", "B");
        draft.SetChartOffset(ExpertGuitar, -500);

        await Assert.ThrowsExactlyAsync<ChartFormatException>(() => DraftWriter.SaveFolderAsync(folder, songIni, draft));

        Assert.AreEqual("[song]\nname = A\n", File.ReadAllText(songIni));
        Assert.AreEqual(ChartReaderTests.Chart, File.ReadAllText(Path.Combine(folder, "notes.chart")));
    }

    [TestMethod]
    public async Task FolderChartOffsetsRefuseAnAddedChartOverTheLimit()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        string source = Path.Combine(_root.FullName, "huge.mid");
        using (var stream = File.Create(source))
            stream.SetLength(ChartReader.MaxBytes + 1);
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("notes.mid", source, ChartReader.MaxBytes + 1));
        draft.SetChartOffset(ExpertGuitar, 250);

        await Assert.ThrowsExactlyAsync<ChartFormatException>(() => DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft));

        Assert.IsEmpty(Directory.GetFiles(folder));
    }

    [TestMethod]
    public async Task SngChartOffsetsRewriteTheStoredChart()
    {
        byte[] midi = ChartReaderTests.SampleMidi();
        string sng = await CreateSngAsync([P("name", "A")], ("notes.mid", midi), ("song.ogg", [4]));
        var draft = new MetadataDraft();
        draft.SetChartOffset(ExpertGuitar, 250);

        var findings = await DraftWriter.SaveSngAsync(sng, draft);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        CollectionAssert.AreEquivalent(new[] { "notes.mid", "song.ogg" }, after.Files.Select(f => f.Name).ToArray());
        var chart = ChartReader.Read(Extract(sng, after, "notes.mid"), "notes.mid");
        Assert.AreEqual(240, ChartReaderTests.Notes(chart, ChartInstrument.Guitar)[0].Tick);
        CollectionAssert.AreEqual(new byte[] { 4 }, Extract(sng, after, "song.ogg"));
    }

    [TestMethod]
    public async Task FolderVocalsAreTakenFromAnotherChart()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        string chart = Path.Combine(folder, "notes.mid");
        File.WriteAllBytes(chart, ChartReaderTests.SampleMidi());
        string source = WriteNamedSource("notes.mid", VocalsMidi("hey"));
        var draft = new MetadataDraft();
        draft.SetVocals(source);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        byte[] saved = File.ReadAllBytes(chart);
        CollectionAssert.AreEqual(new[] { "notes.mid" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        Assert.AreEqual("hey", VocalReader.Read(saved, "notes.mid").Main!.Lyrics.Single().Text);
        CollectionAssert.AreEqual(
            ChartReaderTests.Notes(ChartReader.Read(ChartReaderTests.SampleMidi(), "notes.mid"), ChartInstrument.Guitar),
            ChartReaderTests.Notes(ChartReader.Read(saved, "notes.mid"), ChartInstrument.Guitar));
    }

    [TestMethod]
    public async Task FolderVocalsSourceBesideASongIniIsPlacedByItsDelay()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        string chart = Path.Combine(folder, "notes.mid");
        File.WriteAllBytes(chart, ChartReaderTests.SampleMidi());
        string source = WriteNamedSource("notes.mid", VocalsMidi("hey", tick: 960));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(source)!, "song.ini"), "[song]\ndelay = 500\n");
        var draft = new MetadataDraft();
        draft.SetVocals(source);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        // Half a second later, at 120 BPM and 480 ticks a beat.
        Assert.AreEqual(1440, VocalReader.Read(File.ReadAllBytes(chart), "notes.mid").Main!.Lyrics.Single().Tick);
    }

    [TestMethod]
    public async Task FolderVocalsOffsetMovesTheStoredVocals()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        string chart = Path.Combine(folder, "notes.mid");
        File.WriteAllBytes(chart, VocalsMidi("hey", tick: 960));
        var draft = new MetadataDraft();
        draft.SetVocalsOffset(-500);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        Assert.AreEqual(480, VocalReader.Read(File.ReadAllBytes(chart), "notes.mid").Main!.Lyrics.Single().Tick);
    }

    [TestMethod]
    public async Task FolderVocalsOffsetMovesVocalsTakenFromAnotherChart()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        string chart = Path.Combine(folder, "notes.mid");
        File.WriteAllBytes(chart, ChartReaderTests.SampleMidi());
        var draft = new MetadataDraft();
        draft.SetVocals(WriteNamedSource("notes.mid", VocalsMidi("hey", tick: 960)));
        draft.SetVocalsOffset(250);

        await DraftWriter.SaveFolderAsync(folder, Path.Combine(folder, SongIni.FileName), draft);

        Assert.AreEqual(1200, VocalReader.Read(File.ReadAllBytes(chart), "notes.mid").Main!.Lyrics.Single().Tick);
    }

    [TestMethod]
    public async Task SngVocalsAreRemovedAndOffsetsAppliedTogether()
    {
        byte[] midi = new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART GUITAR").Note(0, 96)
            .Track("PART VOCALS").Note(0, 60).Meta(0, 0x05, [0x68, 0x69])
            .Build();
        string sng = await CreateSngAsync([P("name", "A")], ("notes.mid", midi), ("song.ogg", [4]));
        var draft = new MetadataDraft();
        draft.RemoveVocals();
        draft.SetChartOffset(ExpertGuitar, 250);

        var findings = await DraftWriter.SaveSngAsync(sng, draft);

        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        var after = SngReader.Read(sng);
        byte[] saved = Extract(sng, after, "notes.mid");
        Assert.IsEmpty(VocalReader.Read(saved, "notes.mid").Parts);
        Assert.AreEqual(240, ChartReaderTests.Notes(ChartReader.Read(saved, "notes.mid"), ChartInstrument.Guitar)[0].Tick);
    }

    [TestMethod]
    public async Task FolderVocalsFromAMissingSourceWriteNothing()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        string chart = Path.Combine(folder, "notes.mid");
        File.WriteAllBytes(chart, ChartReaderTests.SampleMidi());
        string songIni = Path.Combine(folder, SongIni.FileName);
        File.WriteAllText(songIni, "[song]\nname = A\n");
        var draft = new MetadataDraft();
        draft.Set("name", "B");
        draft.SetVocals(Path.Combine(_root.FullName, "gone", "notes.mid"));

        await Assert.ThrowsAsync<IOException>(() => DraftWriter.SaveFolderAsync(folder, songIni, draft));

        Assert.AreEqual("[song]\nname = A\n", File.ReadAllText(songIni));
        CollectionAssert.AreEqual(ChartReaderTests.SampleMidi(), File.ReadAllBytes(chart));
    }

    private static byte[] VocalsMidi(string lyric, long tick = 0) => new MidiBuilder()
        .Track("tempo").Tempo(0, 120)
        .Track("PART VOCALS").Note(tick, 60).Meta(tick, 0x05, System.Text.Encoding.Latin1.GetBytes(lyric))
        .Build();

    private static readonly ChartTrackKey ExpertGuitar = new(ChartInstrument.Guitar, ChartDifficulty.Expert);

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);

    private string WriteSource(byte[] data)
    {
        string path = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, data);
        return path;
    }

    private string WriteNamedSource(string name, byte[] data)
    {
        string path = Path.Combine(Directory.CreateDirectory(Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"))).FullName, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private async Task<string> CreateSngAsync(IReadOnlyList<KeyValuePair<string, string>> metadata, params (string Name, byte[] Data)[] files)
    {
        var sources = files.Select(f => new SngSourceFile(f.Name, WriteSource(f.Data), f.Data.Length)).ToList();
        string sng = Path.Combine(_root.FullName, "song.sng");
        await SngWriter.WriteAsync(sng, SngMask.Generate(), metadata, sources, overwrite: false);
        return sng;
    }

    private static byte[] Extract(string sng, SngReadResult read, string name)
    {
        using var handle = File.OpenHandle(sng);
        using var output = new MemoryStream();
        SngFileData.CopyUnmasked(handle, read.Files.Single(f => f.Name == name), read.Header!.Mask.Span, output);
        return output.ToArray();
    }
}
