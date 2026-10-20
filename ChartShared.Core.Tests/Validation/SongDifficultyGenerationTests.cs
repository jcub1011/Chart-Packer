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
/// The library's quick fixes for chart findings (charter-spec C16 item 5): a song's missing difficulties (CHR087) generated
/// into its draft, the draft's chart linted, and an unrated part's suggested rating (CHR083).
/// </summary>
[TestClass]
public sealed class SongDifficultyGenerationTests
{
    private DirectoryInfo _root = null!;
    private GeneratedChartStash _stash = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-generate-");
        _stash = GeneratedChartStash.Create();
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        _stash.Dispose();
        _root.Delete(recursive: true);
    }

    [TestMethod]
    public async Task AFoldersMissingDifficultiesAreGeneratedIntoItsDraftAndNothingIsWritten()
    {
        var entry = Folder(("notes.mid", MidiChartWriter.Write(ExpertOnly())));
        var pairs = await PairsOf(entry);
        var before = Directory.GetFiles(entry.FullPath).ToDictionary(f => f, File.ReadAllBytes);

        var chart = await SongDifficultyGeneration.GenerateAsync(entry, pairs, null, ReductionDefaults.Shipped, _stash, TestContext.CancellationToken);

        Assert.IsNotNull(chart);
        Assert.IsNull(chart.RemoveChart);
        Assert.StartsWith(_stash.Folder, chart.StagedPath);
        var generated = MidiChartLoader.Load(await File.ReadAllBytesAsync(chart.StagedPath, TestContext.CancellationToken), ChartReadOptions.From(pairs)).Chart;
        Assert.IsTrue(generated.PartOf(ChartInstrument.Guitar)!.Tracks.All(t => t.Notes.Count > 0));
        foreach (var (file, bytes) in before)
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(file), $"{file} was written.");

        var draft = new MetadataDraft();
        chart.ApplyTo(draft);
        Assert.IsTrue(draft.IsFileAdded(ChartFiles.MidiName));
    }

    [TestMethod]
    public async Task TheDraftsChartIsLintedAndCachedApartFromTheSongs()
    {
        var entry = Folder(("notes.mid", MidiChartWriter.Write(ExpertOnly())));
        var pairs = await PairsOf(entry);
        var cache = new SongChartLintCache();
        var draft = new MetadataDraft();
        (await SongDifficultyGeneration.GenerateAsync(entry, pairs, null, ReductionDefaults.Shipped, _stash, TestContext.CancellationToken))!.ApplyTo(draft);

        var stored = await SongChartLint.LintAsync(entry, pairs, null, cache, TestContext.CancellationToken);
        var drafted = await SongChartLint.LintAsync(entry, pairs, draft, cache, TestContext.CancellationToken);
        var storedAgain = await SongChartLint.LintAsync(entry, pairs, null, cache, TestContext.CancellationToken);

        Assert.HasCount(3, stored.Findings.Where(f => f.Finding.RuleId == ChrRules.Chr087).ToList());
        Assert.DoesNotContain(f => f.Finding.RuleId == ChrRules.Chr087, drafted.Findings);
        Assert.HasCount(3, storedAgain.Findings.Where(f => f.Finding.RuleId == ChrRules.Chr087).ToList());
    }

    [TestMethod]
    public async Task ANotesChartSongIsGivenANotesMidInItsPlace()
    {
        const string chart = "[Song]\n{\n  Resolution = 192\n}\n[SyncTrack]\n{\n  0 = TS 4\n  0 = B 120000\n}\n[ExpertSingle]\n{\n  0 = N 0 0\n  192 = N 1 0\n  384 = N 2 0\n  576 = N 3 0\n  768 = N 4 0\n  960 = N 0 0\n}\n";
        var entry = Folder(("notes.chart", Encoding.UTF8.GetBytes(chart)));
        var pairs = await PairsOf(entry);

        var generated = await SongDifficultyGeneration.GenerateAsync(entry, pairs, null, ReductionDefaults.Shipped, _stash, TestContext.CancellationToken);

        Assert.IsNotNull(generated);
        Assert.AreEqual(ChartFiles.ChartName, generated.RemoveChart);
        var draft = new MetadataDraft();
        generated.ApplyTo(draft);
        Assert.IsTrue(draft.IsFileRemoved(ChartFiles.ChartName));
        var played = DraftedChart.Of([ChartFiles.ChartName, "song.ini"], draft);
        Assert.AreEqual(ChartFiles.MidiName, played?.StoredName);
        Assert.AreEqual(generated.StagedPath, played?.StagedPath);
    }

    [TestMethod]
    public async Task ThePreviewShowsEachDraftAgainstItsSource()
    {
        var entry = Folder(("notes.mid", MidiChartWriter.Write(ExpertOnly())));
        var pairs = await PairsOf(entry);
        var read = await SongDifficultyGeneration.ReadAsync(entry, pairs, null, TestContext.CancellationToken);

        var generation = SongDifficultyGeneration.Prepare(read, pairs, ReductionDefaults.Shipped);
        var diffs = generation.Diff(ChartInstrument.Guitar);

        Assert.AreSequenceEqual(
            [(ChartDifficulty.Expert, ChartDifficulty.Hard), (ChartDifficulty.Hard, ChartDifficulty.Medium), (ChartDifficulty.Medium, ChartDifficulty.Easy)],
            [.. diffs.Select(d => (d.Source, d.Target))]);
        Assert.IsTrue(diffs.All(d => d.DraftTrack.Notes.Count > 0));
    }

    [TestMethod]
    public async Task ASongWhoseDraftChangesItsChartOtherwiseIsRefused()
    {
        var entry = Folder(("notes.mid", MidiChartWriter.Write(ExpertOnly())));
        var draft = new MetadataDraft();
        draft.SetChartOffset(new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Expert), 10);

        Assert.IsFalse(SongDifficultyGeneration.CanGenerate(draft));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SongDifficultyGeneration.GenerateAsync(entry, [], draft, ReductionDefaults.Shipped, _stash, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ASongMissingNothingGeneratesNothing()
    {
        var full = ExpertOnly();
        foreach (var difficulty in new[] { ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy })
            new AddNotes(ChartInstrument.Guitar, difficulty, [new EditableNote(0, 1, 0)]).Apply(full);
        var entry = Folder(("notes.mid", MidiChartWriter.Write(full)));

        var generated = await SongDifficultyGeneration.GenerateAsync(entry, await PairsOf(entry), null, ReductionDefaults.Shipped, _stash, TestContext.CancellationToken);

        Assert.IsNull(generated);
        Assert.IsEmpty(Directory.GetFiles(_stash.Folder, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task AnUnratedPartIsGivenTheEstimatorsRating()
    {
        var entry = Folder(("notes.mid", MidiChartWriter.Write(ExpertOnly())), songIni: "[song]\nname = Song\n");

        var report = await SongChartLint.LintAsync(entry, await PairsOf(entry), null, null, TestContext.CancellationToken);

        Assert.Contains(f => f.Finding.RuleId == ChrRules.Chr083 && f.Finding.Key == "diff_guitar", report.Findings);
        Assert.IsTrue(report.SuggestedRatings.TryGetValue("diff_guitar", out int tier));
        Assert.IsTrue(tier is >= 0 and <= 9, $"Tier {tier}.");
    }

    // Lead guitar on Expert alone: a note a beat for 32 beats, up and down the frets.
    private static EditableChart ExpertOnly()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [.. Enumerable.Range(0, 32).Select(i => new EditableNote(i * 480L, 1, Math.Abs((i % 8) - 4)))]).Apply(chart);
        return chart;
    }

    private SongEntry Folder((string Name, byte[] Data) chart, string songIni = "[song]\nname = Song\ndiff_guitar = 3\n")
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, chart.Name), chart.Data);
        File.WriteAllBytes(Path.Combine(folder, "song.ini"), Encoding.UTF8.GetBytes(songIni));
        return new SongEntry(SongEntryKind.SongFolder, folder, Path.GetRelativePath(_root.FullName, folder), Path.GetFileName(folder));
    }

    private async Task<IReadOnlyList<KeyValuePair<string, string>>> PairsOf(SongEntry entry) =>
        (await SongMetadataCheck.ReadAsync(entry, TestContext.CancellationToken)).Pairs;
}
