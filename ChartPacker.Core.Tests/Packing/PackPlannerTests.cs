using ChartPacker.Core.Packing;
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

        var plan = await PackPlanner.PlanAsync(_root.FullName);

        Assert.IsFalse(plan.HasErrors);
        CollectionAssert.AreEquivalent(new[] { "notes.chart", "song.ogg" }, plan.Included.Select(f => f.StoredName).ToArray());
        CollectionAssert.Contains(plan.Excluded.ToArray(), "desktop.ini");
        CollectionAssert.Contains(plan.Excluded.ToArray(), "song.ini");
        var junk = plan.Findings.Single(f => f.RuleId == SngRules.Sng072);
        Assert.AreEqual(Severity.Info, junk.Severity);
        StringAssert.Contains(junk.Message, "desktop.ini");
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("name", "Test") }, plan.Metadata.ToArray());
    }

    [TestMethod]
    public async Task SubfoldersAreSkippedByDefault()
    {
        Write("song.ini", "[song]\nname = A\n");
        Write("stems/a.ogg", "1");
        Write("stems/b.ogg", "2");

        var plan = await PackPlanner.PlanAsync(_root.FullName);

        Assert.IsEmpty(plan.Included);
        Assert.AreEqual(1, plan.Findings.Count(f => f.RuleId == SngRules.Sng073));
    }

    [TestMethod]
    public async Task SubfoldersCanBeIncluded()
    {
        Write("song.ini", "[song]\nname = A\n");
        Write("stems/a.ogg", "1");

        var plan = await PackPlanner.PlanAsync(_root.FullName, new PackOptions(IncludeSubfolders: true));

        Assert.AreEqual("stems/a.ogg", plan.Included.Single().StoredName);
    }

    [TestMethod]
    public async Task MacOsFolderAndNestedSngAreExcluded()
    {
        Write("song.ini", "[song]\nname = A\n");
        Write("__MACOSX/._song.ogg", "1");
        Write("old.sng", "2");

        var plan = await PackPlanner.PlanAsync(_root.FullName, new PackOptions(IncludeSubfolders: true));

        Assert.IsEmpty(plan.Included);
        Assert.AreEqual(2, plan.Findings.Count(f => f.RuleId == SngRules.Sng072));
    }

    [TestMethod]
    public async Task SemicolonInValueBlocksPacking()
    {
        Write("song.ini", "[song]\nloading_phrase = Hello; world\n");

        var plan = await PackPlanner.PlanAsync(_root.FullName);

        Assert.IsTrue(plan.HasErrors);
        Assert.AreEqual(SngRules.Sng021, plan.Findings.Single(f => f.Severity == Severity.Error).RuleId);
    }

    [TestMethod]
    public async Task EmptyValuesAreDropped()
    {
        Write("song.ini", "[song]\nname = A\nalbum =\n");

        var plan = await PackPlanner.PlanAsync(_root.FullName);

        Assert.IsFalse(plan.HasErrors);
        Assert.AreEqual("name", plan.Metadata.Single().Key);
        Assert.AreEqual(Severity.Info, plan.Findings.Single(f => f.RuleId == SngRules.Sng061).Severity);
    }

    [TestMethod]
    public async Task MissingSongSectionIsAWarning()
    {
        Write("song.ini", "[other]\nname = A\n");

        var plan = await PackPlanner.PlanAsync(_root.FullName);

        Assert.IsFalse(plan.HasErrors);
        Assert.IsEmpty(plan.Metadata);
        Assert.AreEqual(Severity.Warning, plan.Findings.Single(f => f.RuleId == SngRules.Sng071).Severity);
    }

    [TestMethod]
    public async Task MissingSongIniIsAWarning()
    {
        Write("notes.chart", "x");

        var plan = await PackPlanner.PlanAsync(_root.FullName);

        Assert.AreEqual(Severity.Warning, plan.Findings.Single(f => f.RuleId == SngRules.Sng070).Severity);
    }

    [TestMethod]
    public async Task UnsafeFilenameIsAnError()
    {
        Write("song.ini", "[song]\nname = A\n");
        Write("notes..chart", "x");

        var plan = await PackPlanner.PlanAsync(_root.FullName);

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

        var plan = await PackPlanner.PlanAsync(_root.FullName);

        Assert.AreEqual(SngRules.Sng032, plan.Findings.Single(f => f.Severity == Severity.Error).RuleId);
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
