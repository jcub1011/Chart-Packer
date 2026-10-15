using ChartPacker.Core.Charting;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class NewChartFolderTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-newfolder-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AFreeNameInAnExistingFolderIsFine() =>
        Assert.IsNull(NewChartFolder.ProblemOf(_root.FullName, "Artist - Song"));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void NoParentIsAProblem(string? parent) =>
        Assert.AreEqual(NewChartFolderProblemKind.NoParent, NewChartFolder.ProblemOf(parent, "Song")?.Kind);

    [TestMethod]
    public void AMissingParentIsAProblem() =>
        Assert.AreEqual(NewChartFolderProblemKind.ParentMissing, NewChartFolder.ProblemOf(Path.Combine(_root.FullName, "nowhere"), "Song")?.Kind);

    [TestMethod]
    public void ATakenNameIsAProblemWhetherAFolderOrAFile()
    {
        // AC C1.3: Create is unavailable while the target exists.
        Directory.CreateDirectory(Path.Combine(_root.FullName, "Folder"));
        File.WriteAllText(Path.Combine(_root.FullName, "File"), "x");

        Assert.AreEqual(NewChartFolderProblemKind.Exists, NewChartFolder.ProblemOf(_root.FullName, "Folder")?.Kind);
        Assert.AreEqual(NewChartFolderProblemKind.Exists, NewChartFolder.ProblemOf(_root.FullName, "File")?.Kind);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow("Song?")]
    [DataRow("Song.")]
    [DataRow("Song ")]
    [DataRow("CON")]
    [DataRow("..")]
    public void ANameBreakingTheFilenameRulesIsAProblemWithAReason(string name)
    {
        var problem = NewChartFolder.ProblemOf(_root.FullName, name);

        Assert.AreEqual(NewChartFolderProblemKind.InvalidName, problem?.Kind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(problem!.Reason));
    }

    [TestMethod]
    [DataRow("Artist", "Song", "Artist - Song")]
    [DataRow(" Artist ", "", "Artist")]
    [DataRow("", "Song", "Song")]
    [DataRow("", "", "")]
    [DataRow("AC/DC", "What?", "AC_DC - What_")]
    [DataRow("Band", "Song...", "Band - Song")]
    [DataRow("Band", "A..B", "Band - A.B")]
    public void TheFolderNameFollowsTheArtistAndNameWithWhatAPathCanNotHoldReplaced(string artist, string name, string expected)
    {
        string folder = NewChartFolder.NameFor(artist, name);

        Assert.AreEqual(expected, folder);
        if (folder.Length > 0)
            Assert.IsNull(NewChartFolder.ProblemOf(_root.FullName, folder));
    }
}
