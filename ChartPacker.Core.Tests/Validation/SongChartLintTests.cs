using System.Buffers.Binary;
using System.Text;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Validation;

/// <summary>Chart lint in Chart Packer's library (charter-spec C16 item 5): what it finds, what it caches, and that it never blocks packing.</summary>
[TestClass]
public sealed class SongChartLintTests
{
    private DirectoryInfo _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-chartlint-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task AFoldersChartIsLintedWithItsSongIni()
    {
        var entry = Folder(OpenInAChord(), "[song]\nname = Song\n");

        var report = await SongChartLint.LintAsync(entry, await PairsOf(entry), null, null, TestContext.CancellationToken);

        Assert.AreEqual(SongChartLintStatus.Linted, report.Status);
        var open = report.Findings.Single(f => f.Finding.RuleId == ChrRules.Chr022);
        Assert.AreEqual(Severity.Error, open.Finding.Severity);
        Assert.IsNotNull(open.Place);
        Assert.AreEqual("diff_guitar", report.Findings.Single(f => f.Finding.RuleId == ChrRules.Chr083).Finding.Key);
        Assert.IsTrue(report.HasProblems);
    }

    [TestMethod]
    public async Task SongLengthIsCheckedAgainstTheAudio()
    {
        var entry = Folder(OpenInAChord(), "[song]\nname = Song\nsong_length = 9000\n", ("song.wav", Wav(2000)));

        var report = await SongChartLint.LintAsync(entry, await PairsOf(entry), null, null, TestContext.CancellationToken);

        Assert.AreEqual("song_length", report.Findings.Single(f => f.Finding.RuleId == ChrRules.Chr084).Finding.Key);
    }

    [TestMethod]
    public async Task ADotChartSongIsConvertedAndLinted()
    {
        const string chart = "[Song]\n{\n  Resolution = 192\n}\n[SyncTrack]\n{\n  0 = TS 4\n  0 = B 120000\n}\n[ExpertSingle]\n{\n  0 = N 0 0\n  192 = N 1 0\n}\n";
        string folder = CreateFolder(("notes.chart", Encoding.UTF8.GetBytes(chart)), ("song.ini", Encoding.UTF8.GetBytes("[song]\nname = Song\n")));
        var entry = Entry(folder);

        var report = await SongChartLint.LintAsync(entry, await PairsOf(entry), null, null, TestContext.CancellationToken);

        Assert.AreEqual(SongChartLintStatus.Linted, report.Status);
        Assert.IsTrue(report.Findings.Any(f => f.Finding.RuleId == ChrRules.Chr083));
    }

    [TestMethod]
    public async Task ADotSngSongIsLinted()
    {
        var builder = new SngBuilder().AddMetadata("name", "Song");
        builder.AddFile("notes.mid", Masked(builder, MidiChartWriter.Write(OpenInAChord())));
        string path = Path.Combine(_root.FullName, "Song.sng");
        await File.WriteAllBytesAsync(path, builder.Build(), TestContext.CancellationToken);
        var entry = new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song");

        var report = await SongChartLint.LintAsync(entry, [KeyValuePair.Create("name", "Song")], null, null, TestContext.CancellationToken);

        Assert.IsTrue(report.Findings.Any(f => f.Finding.RuleId == ChrRules.Chr022));
    }

    [TestMethod]
    public async Task ASongWithoutAChartIsntLinted()
    {
        var entry = Entry(CreateFolder(("song.ini", Encoding.UTF8.GetBytes("[song]\nname = Song\n"))));

        var report = await SongChartLint.LintAsync(entry, [], null, null, TestContext.CancellationToken);

        Assert.AreEqual(SongChartLintStatus.NoChart, report.Status);
        Assert.IsEmpty(report.Findings);
    }

    [TestMethod]
    public async Task AnUnchangedSongIsAnsweredFromTheCacheAndAChangeLintsItAgain()
    {
        var entry = Folder(OpenInAChord(), "[song]\nname = Song\n", ("song.wav", Wav(1000)));
        var pairs = await PairsOf(entry);
        var cache = new SongChartLintCache();

        var first = await SongChartLint.LintAsync(entry, pairs, null, cache, TestContext.CancellationToken);
        var second = await SongChartLint.LintAsync(entry, pairs, null, cache, TestContext.CancellationToken);
        Assert.AreSame(first, second);
        Assert.AreEqual(1, cache.Hits);

        // Other song.ini values lint again, without measuring the audio again.
        await SongChartLint.LintAsync(entry, [.. pairs, KeyValuePair.Create("diff_guitar", "3")], null, cache, TestContext.CancellationToken);
        Assert.AreEqual(1, cache.Hits);
        Assert.AreEqual(1, cache.AudioMeasures);

        // A chart written since lints again.
        File.SetLastWriteTimeUtc(Path.Combine(entry.FullPath, "notes.mid"), DateTime.UtcNow.AddMinutes(1));
        Assert.AreNotSame(first, await SongChartLint.LintAsync(entry, pairs, null, cache, TestContext.CancellationToken));
        Assert.AreEqual(1, cache.Hits);

        // Audio written since is measured again.
        await File.WriteAllBytesAsync(Path.Combine(entry.FullPath, "song.wav"), Wav(1500), TestContext.CancellationToken);
        await SongChartLint.LintAsync(entry, pairs, null, cache, TestContext.CancellationToken);
        Assert.AreEqual(2, cache.AudioMeasures);
    }

    [TestMethod]
    public async Task ChartLintNeverBlocksPacking()
    {
        // charter-spec C16 item 5: a song with a chart Error still plans and packs as it did.
        var entry = Folder(OpenInAChord(), "[song]\nname = Song\nartist = Artist\n", ("song.wav", Wav(1000)));
        var report = await SongChartLint.LintAsync(entry, await PairsOf(entry), null, null, TestContext.CancellationToken);
        Assert.IsTrue(report.Findings.Any(f => f.Finding.Severity == Severity.Error));

        var plan = await PackPlanner.PlanAsync(entry.FullPath, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(plan.Findings.Any(f => ChrRules.IsChartRule(f.RuleId)));
        Assert.IsFalse(plan.HasErrors, string.Join("; ", plan.Findings.Select(f => f.Message)));
    }

    [TestMethod]
    public void EveryRuleIdHasACategory()
    {
        foreach (string id in ChrRules.All)
            Assert.IsNotNull(ChrRules.CategoryOf(id), id);
        Assert.AreEqual(ChrCategory.Timing, ChrRules.CategoryOf(ChrRules.Chr004));
        Assert.AreEqual(ChrCategory.Frets, ChrRules.CategoryOf(ChrRules.Chr022));
        Assert.AreEqual(ChrCategory.Drums, ChrRules.CategoryOf(ChrRules.Chr045));
        Assert.AreEqual(ChrCategory.Phrases, ChrRules.CategoryOf(ChrRules.Chr060));
        Assert.AreEqual(ChrCategory.Song, ChrRules.CategoryOf(ChrRules.Chr085));
        Assert.AreEqual(ChrCategory.Load, ChrRules.CategoryOf(ChrRules.Chr086));
        Assert.AreEqual(ChrCategory.Vocals, ChrRules.CategoryOf(ChrRules.Chr090));
        Assert.AreEqual(ChrCategory.Vocals, ChrRules.CategoryOf(ChrRules.Chr095));
        Assert.IsNull(ChrRules.CategoryOf("SNG021"));
        Assert.IsNull(ChrRules.CategoryOf("CHRX01"));
    }

    // A guitar chart with an open note in a chord with a fret at tick 960 (CHR022), and no sections.
    private static EditableChart OpenInAChord()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(0, 1, 0), new(480, 1, 1), new(960, 1, ChartNote.OpenLane), new(960, 1, 2)]).Apply(chart);
        return chart;
    }

    private SongEntry Folder(EditableChart chart, string songIni, params (string Name, byte[] Data)[] more) =>
        Entry(CreateFolder([("notes.mid", MidiChartWriter.Write(chart)), ("song.ini", Encoding.UTF8.GetBytes(songIni)), .. more]));

    private async Task<IReadOnlyList<KeyValuePair<string, string>>> PairsOf(SongEntry entry) =>
        (await SongMetadataCheck.ReadAsync(entry, TestContext.CancellationToken)).Pairs;

    private string CreateFolder(params (string Name, byte[] Data)[] files)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        foreach (var (name, data) in files)
            File.WriteAllBytes(Path.Combine(folder, name), data);
        return folder;
    }

    private SongEntry Entry(string folder) => new(SongEntryKind.SongFolder, folder, Path.GetRelativePath(_root.FullName, folder), Path.GetFileName(folder));

    private static byte[] Masked(SngBuilder builder, byte[] data)
    {
        byte[] copy = [.. data];
        SngMask.Apply(copy, builder.Mask);
        return copy;
    }

    /// <summary>A silent 16-bit mono PCM WAV at 8 kHz lasting <paramref name="ms"/> milliseconds.</summary>
    private static byte[] Wav(int ms)
    {
        const int Rate = 8000;
        int dataLength = Rate * ms / 1000 * 2;
        byte[] wav = new byte[44 + dataLength];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], Rate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], Rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        return wav;
    }
}
