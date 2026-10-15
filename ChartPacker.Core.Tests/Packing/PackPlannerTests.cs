using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Tests.Charts;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Packing;

[TestClass]
public sealed class PackPlannerTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-plan-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task RegisteredNamesAreLowercasedAndJunkIsExcluded()
    {
        Write("song.ini", "[song]\nname = Test\n");
        Write("notes.chart", "x");
        Write("Song.OGG", "audio");
        Write("desktop.ini", "junk");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(plan.HasErrors);
        Assert.AreSequenceEqual(["notes.chart", "song.ogg"], [.. plan.Included.Select(f => f.StoredName)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        CollectionAssert.Contains(plan.Excluded.ToArray(), "desktop.ini");
        CollectionAssert.Contains(plan.Excluded.ToArray(), "song.ini");
        var junk = plan.Findings.Single(f => f.RuleId == SngRules.Sng072);
        Assert.AreEqual(Severity.Info, junk.Severity);
        Assert.Contains("desktop.ini", junk.Message);
        Assert.AreSequenceEqual([KeyValuePair.Create("name", "Test")], [.. plan.Metadata]);
    }

    [TestMethod]
    public async Task SubfoldersAreSkippedByDefault()
    {
        Write("song.ini", "[song]\nname = A\n");
        Write("stems/a.ogg", "1");
        Write("stems/b.ogg", "2");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.IsEmpty(plan.Included);
        Assert.ContainsSingle(f => f.RuleId == SngRules.Sng073, plan.Findings);
    }

    [TestMethod]
    public async Task SubfoldersCanBeIncluded()
    {
        Write("song.ini", "[song]\nname = A\n");
        Write("stems/a.ogg", "1");

        var plan = await PackPlanner.PlanAsync(_root.FullName, new PackOptions(IncludeSubfolders: true), TestContext.CancellationToken);

        Assert.AreEqual("stems/a.ogg", plan.Included.Single().StoredName);
    }

    [TestMethod]
    public async Task MacOsFolderAndNestedSngAreExcluded()
    {
        Write("song.ini", "[song]\nname = A\n");
        Write("__MACOSX/._song.ogg", "1");
        Write("old.sng", "2");

        var plan = await PackPlanner.PlanAsync(_root.FullName, new PackOptions(IncludeSubfolders: true), TestContext.CancellationToken);

        Assert.IsEmpty(plan.Included);
        Assert.AreEqual(2, plan.Findings.Count(f => f.RuleId == SngRules.Sng072));
    }

    [TestMethod]
    public async Task SemicolonInValueBlocksPacking()
    {
        Write("song.ini", "[song]\nloading_phrase = Hello; world\n");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(plan.HasErrors);
        Assert.AreEqual(SngRules.Sng021, plan.Findings.Single(f => f.Severity == Severity.Error).RuleId);
    }

    [TestMethod]
    public async Task EmptyValuesAreDropped()
    {
        Write("song.ini", "[song]\nname = A\nalbum =\n");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(plan.HasErrors);
        Assert.AreEqual("name", plan.Metadata.Single().Key);
        Assert.AreEqual(Severity.Info, plan.Findings.Single(f => f.RuleId == SngRules.Sng061).Severity);
    }

    [TestMethod]
    public async Task KeysOfPartsTheChartHasNoNotesInAreDropped()
    {
        // The drums track is there but empty, as a template leaves it; diff_band rates the whole chart.
        Write("song.ini", "[song]\nname = A\ndiff_guitar = 3\ndiff_drums = -1\npro_drums = True\ndiff_band = 2\ndiff_vocals = -1\n");
        WriteBytes("notes.mid", new MidiBuilder().Track("Song").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Track("PART DRUMS").Build());

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(plan.HasErrors);
        Assert.AreSequenceEqual(["name", "diff_guitar", "diff_band"], [.. plan.Metadata.Select(p => p.Key)]);
        var dropped = plan.Findings.Where(f => f.RuleId == SngRules.Sng062).ToList();
        Assert.HasCount(3, dropped);
        Assert.IsTrue(dropped.All(f => f.Severity == Severity.Info));
    }

    [TestMethod]
    public async Task AChartKeepsTheKeysOfPartsItsReaderDoesntRead()
    {
        // Vocals aren't read from a .chart, so it isn't known not to have them.
        Write("song.ini", "[song]\nname = A\ndiff_drums = -1\ndiff_vocals = 2\n");
        Write("notes.chart", "[Song]\n{\n  Resolution = 192\n}\n[SyncTrack]\n{\n  0 = TS 4\n  0 = B 120000\n}\n[ExpertSingle]\n{\n  0 = N 0 0\n}\n");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual(["name", "diff_vocals"], [.. plan.Metadata.Select(p => p.Key)]);
    }

    [TestMethod]
    public async Task WithoutAReadableChartEveryKeyIsKept()
    {
        Write("song.ini", "[song]\nname = A\ndiff_drums = -1\n");
        WriteBytes("notes.mid", [1, 2, 3]);

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual(["name", "diff_drums"], [.. plan.Metadata.Select(p => p.Key)]);
        Assert.DoesNotContain(f => f.RuleId == SngRules.Sng062, plan.Findings);
    }

    [TestMethod]
    public async Task MissingSongSectionIsAWarning()
    {
        Write("song.ini", "[other]\nname = A\n");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(plan.HasErrors);
        Assert.IsEmpty(plan.Metadata);
        Assert.AreEqual(Severity.Warning, plan.Findings.Single(f => f.RuleId == SngRules.Sng071).Severity);
    }

    [TestMethod]
    public async Task MissingSongIniIsAWarning()
    {
        Write("notes.chart", "x");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(Severity.Warning, plan.Findings.Single(f => f.RuleId == SngRules.Sng070).Severity);
    }

    [TestMethod]
    public async Task UnsafeFilenameIsAnError()
    {
        Write("song.ini", "[song]\nname = A\n");
        Write("notes..chart", "x");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(SngRules.Sng030, plan.Findings.Single(f => f.Severity == Severity.Error).RuleId);
        Assert.IsEmpty(plan.Included);
    }

    [TestMethod]
    public async Task NamesThatCollideAfterLowercasingAreAnError()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Inconclusive("The file system is case-insensitive, so the two files can't both exist.");

        Write("song.ini", "[song]\nname = A\n");
        Write("Song.ogg", "1");
        Write("song.ogg", "2");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(SngRules.Sng032, plan.Findings.Single(f => f.Severity == Severity.Error).RuleId);
    }

    [TestMethod]
    public async Task PlanFromAListingMatchesPlanFromTheFolder()
    {
        Write("song.ini", "[song]\nname = Test\n");
        Write("notes.chart", "x");
        Write("Song.OGG", "audio");
        Write("stems/extra.ogg", "more");
        Write("desktop.ini", "junk");
        var options = new PackOptions(IncludeSubfolders: true);

        var fromFolder = await PackPlanner.PlanAsync(_root.FullName, options, TestContext.CancellationToken);
        var fromListing = await PackPlanner.PlanAsync(_root.FullName, SongFolderListing.List(_root.FullName, TestContext.CancellationToken), options, TestContext.CancellationToken);

        Assert.AreSequenceEqual([.. fromFolder.Included], [.. fromListing.Included]);
        Assert.AreSequenceEqual([.. fromFolder.Excluded], [.. fromListing.Excluded]);
        Assert.AreSequenceEqual([.. fromFolder.Metadata], [.. fromListing.Metadata]);
    }

    [TestMethod]
    public async Task ASongWithoutAudioIsWarnedOf()
    {
        Write("song.ini", "[song]\nname = Test\n");
        Write("notes.mid", "x");
        Write("stems/guitar.ogg", "not at the top");
        Write("backing.ogg", "not a stem");

        var plan = await PackPlanner.PlanAsync(_root.FullName, new PackOptions(IncludeSubfolders: true), TestContext.CancellationToken);

        Assert.IsFalse(plan.HasErrors);
        Assert.AreEqual(Severity.Warning, plan.Findings.Single(f => f.RuleId == SngRules.Sng052).Severity);
    }

    [TestMethod]
    [DataRow("song.ogg")]
    [DataRow("Drums_2.WAV")]
    [DataRow("preview.mp3")]
    public async Task AnyStemIsAudio(string name)
    {
        Write("song.ini", "[song]\nname = Test\n");
        Write(name, "audio");

        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);

        Assert.DoesNotContain(f => f.RuleId == SngRules.Sng052, plan.Findings);
    }

    private void WriteBytes(string relative, byte[] content) => File.WriteAllBytes(Path.Combine(_root.FullName, relative), content);

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public TestContext TestContext { get; set; }
}
