using System.Text;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Saving;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Tests.Validation;

/// <summary>
/// The library's quick fix for CHR066 (charter-spec C16 item 5): star power generated for a song's parts without any, into
/// its draft, alone or before its missing difficulties (CHR087).
/// </summary>
[TestClass]
public sealed class SongStarPowerGenerationTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;

    private DirectoryInfo _root = null!;
    private GeneratedChartStash _stash = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-starpower-");
        _stash = GeneratedChartStash.Create();
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        _stash.Dispose();
        _root.Delete(recursive: true);
    }

    [TestMethod]
    public async Task APartWithoutStarPowerIsNotedAndFailsNothing()
    {
        var entry = Folder(MidiChartWriter.Write(ExpertOnly()));

        var report = await SongChartLint.LintAsync(entry, await PairsOf(entry), null, null, TestContext.CancellationToken);

        var finding = report.Findings.Single(f => f.Finding.RuleId == ChrRules.Chr066).Finding;
        Assert.AreEqual(Severity.Info, finding.Severity);
        Assert.AreEqual(Guitar, finding.Location!.Part);
    }

    [TestMethod]
    public async Task StarPowerAloneIsGeneratedIntoTheDraftAndNothingIsWritten()
    {
        var entry = Folder(MidiChartWriter.Write(ExpertOnly()));
        var pairs = await PairsOf(entry);
        var before = Directory.GetFiles(entry.FullPath).ToDictionary(f => f, File.ReadAllBytes);

        var chart = await SongDifficultyGeneration.GenerateAsync(entry, pairs, null, ReductionDefaults.Shipped, new SongGenerationWork(false, [Guitar]), _stash, TestContext.CancellationToken);

        Assert.IsNotNull(chart);
        var generated = MidiChartLoader.Load(await File.ReadAllBytesAsync(chart.StagedPath, TestContext.CancellationToken), ChartReadOptions.From(pairs)).Chart;
        var guitar = generated.PartOf(Guitar)!;
        Assert.IsTrue(StarPowerGenerator.HasStarPower(guitar));
        Assert.IsTrue(guitar.Tracks.Where(t => t.Difficulty != ChartDifficulty.Expert).All(t => t.Notes.Count == 0), "Only star power was asked for.");
        foreach (var (file, bytes) in before)
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(file), $"{file} was written.");

        var draft = new MetadataDraft();
        chart.ApplyTo(draft);
        var drafted = await SongChartLint.LintAsync(entry, pairs, draft, null, TestContext.CancellationToken);
        Assert.DoesNotContain(f => f.Finding.RuleId == ChrRules.Chr066, drafted.Findings);
    }

    [TestMethod]
    public async Task StarPowerComesBeforeTheMissingDifficultiesSoNoPhraseIsEmptyOnOne()
    {
        var entry = Folder(MidiChartWriter.Write(ExpertOnly()));
        var pairs = await PairsOf(entry);
        var draft = new MetadataDraft();

        (await SongDifficultyGeneration.GenerateAsync(entry, pairs, null, ReductionDefaults.Shipped, new SongGenerationWork(true, [Guitar]), _stash, TestContext.CancellationToken))!.ApplyTo(draft);

        var drafted = await SongChartLint.LintAsync(entry, pairs, draft, null, TestContext.CancellationToken);
        Assert.DoesNotContain(f => f.Finding.RuleId is ChrRules.Chr066 or ChrRules.Chr087 or ChrRules.Chr065 or ChrRules.Chr060, drafted.Findings);
    }

    [TestMethod]
    public async Task PreparingLeavesTheChartReadAsItWas()
    {
        var entry = Folder(MidiChartWriter.Write(ExpertOnly()));
        var pairs = await PairsOf(entry);
        var read = await SongDifficultyGeneration.ReadAsync(entry, pairs, null, TestContext.CancellationToken);

        var generation = SongDifficultyGeneration.Prepare(read, pairs, ReductionDefaults.Shipped, new SongGenerationWork(true, [Guitar]));

        Assert.IsFalse(StarPowerGenerator.HasStarPower(read.Load.Chart.PartOf(Guitar)!));
        Assert.IsTrue(read.Load.Chart.PartOf(Guitar)![ChartDifficulty.Hard].Notes.Count == 0);
        Assert.IsNotEmpty(generation.StarPower![Guitar]);
        Assert.IsTrue(StarPowerGenerator.HasStarPower(generation.Drafted.PartOf(Guitar)!));
    }

    [TestMethod]
    public async Task APartThatHasStarPowerAlreadyIsLeftAsItIs()
    {
        var chart = ExpertOnly();
        new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 0, 1920)).Apply(chart);
        var entry = Folder(MidiChartWriter.Write(chart));

        var generated = await SongDifficultyGeneration.GenerateAsync(entry, await PairsOf(entry), null, ReductionDefaults.Shipped, new SongGenerationWork(false, [Guitar]), _stash, TestContext.CancellationToken);

        Assert.IsNull(generated);
    }

    // Lead guitar on Expert alone: quarter notes for 64 measures (128 s) at 120 BPM, up and down the frets.
    private static EditableChart ExpertOnly()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.Pro));
        new AddNotes(Guitar, ChartDifficulty.Expert, [.. Enumerable.Range(0, 256).Select(i => new EditableNote(i * 480L, 1, Math.Abs((i % 8) - 4)))]).Apply(chart);
        return chart;
    }

    private SongEntry Folder(byte[] notes)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, ChartFiles.MidiName), notes);
        File.WriteAllBytes(Path.Combine(folder, "song.ini"), Encoding.UTF8.GetBytes("[song]\nname = Song\ndiff_guitar = 3\n"));
        return new SongEntry(SongEntryKind.SongFolder, folder, Path.GetRelativePath(_root.FullName, folder), Path.GetFileName(folder));
    }

    private async Task<IReadOnlyList<KeyValuePair<string, string>>> PairsOf(SongEntry entry) =>
        (await SongMetadataCheck.ReadAsync(entry, TestContext.CancellationToken)).Pairs;
}
