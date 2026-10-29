using System.Reflection;
using ChartShared.Core.Help;

namespace ChartShared.Core.Tests.Help;

[TestClass]
public sealed class HelpPagesTests
{
    private static string[] Pages() =>
    [
        .. typeof(HelpPages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!),
    ];

    [TestMethod]
    public void APageIsOpenedAtTheGivenTag()
    {
        Assert.AreEqual(
            "https://github.com/jcub1011/Chart-Packer/blob/v1.0.0/docs/help/Merge-Compare.md",
            HelpPages.UrlFor(HelpPages.MergeCompare, "v1.0.0"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void NoPageIsTheFrontPage(string? page)
    {
        Assert.AreEqual(HelpPages.UrlFor(HelpPages.Home, "main"), HelpPages.UrlFor(page, "main"));
    }

    [TestMethod]
    public void ABranchKeepsItsSlashInTheAddress()
    {
        Assert.AreEqual(
            "https://github.com/jcub1011/Chart-Packer/blob/release/1.0.0/docs/help/Merge.md",
            HelpPages.UrlFor(HelpPages.Merge, "release/1.0.0"));
    }

    [TestMethod]
    public void ANameIsEscapedSoItCantChangeTheAddress()
    {
        Assert.AreEqual(
            "https://github.com/jcub1011/Chart-Packer/blob/a%3Fb/docs/help/c%3Fd.md",
            HelpPages.UrlFor("c?d", "a?b"));
    }

    [TestMethod]
    [DataRow("1.0.0", "v1.0.0")]
    [DataRow("1.2.0-beta.1", "v1.2.0-beta.1")]
    [DataRow("1.0.0+3f9c2ab", "v1.0.0")]
    [DataRow("1.2.0-rc.1+3f9c2ab", "v1.2.0-rc.1")]
    public void AReleaseIsAtItsVersionsTagWithoutTheBuildsCommit(string version, string tag)
    {
        Assert.AreEqual(tag, HelpPages.TagFor(version));
    }

    [TestMethod]
    [DataRow("1.0.0", "release/1.0.0")]
    [DataRow("1.2.0-beta.1", "release/1.2.0")]
    [DataRow("1.0.0+3f9c2ab", "release/1.0.0")]
    [DataRow("1.2.0-rc.1+3f9c2ab", "release/1.2.0")]
    public void AVersionIsTestedOnItsReleaseBranch(string version, string branch)
    {
        Assert.AreEqual(branch, HelpPages.BranchFor(version));
    }

    [TestMethod]
    [DataRow(true, null, "v1.0.0")]
    [DataRow(true, false, "v1.0.0")]
    [DataRow(null, null, "v1.0.0")]
    [DataRow(null, false, "v1.0.0")]
    [DataRow(false, true, "release/1.0.0")]
    [DataRow(false, null, "release/1.0.0")]
    [DataRow(false, false, "main")]
    public void ATagIsUsedOnceItExistsAndTheBranchBeforeThatAndMainLast(bool? tagExists, bool? branchExists, string expected)
    {
        Assert.AreEqual(expected, HelpPages.RefFor("1.0.0+3f9c2ab", tagExists, branchExists));
    }

    [TestMethod]
    public void MainIsOnlyOpenedWhenNeitherTheTagNorTheBranchExists()
    {
        foreach (bool? tag in new bool?[] { true, false, null })
        {
            foreach (bool? branch in new bool?[] { true, false, null })
            {
                bool neither = tag == false && branch == false;
                Assert.AreEqual(neither, HelpPages.RefFor("1.0.0", tag, branch) == "main", $"tag {tag}, branch {branch}");
            }
        }
    }

    [TestMethod]
    public void EveryPageHasItsOwnNameThatNeedsNoEscaping()
    {
        var pages = Pages();

        Assert.IsGreaterThan(20, pages.Length);
        Assert.AreEqual(pages.Length, pages.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string page in pages)
            Assert.AreEqual(page, Uri.EscapeDataString(page), $"{page} would be escaped in its address.");
    }

    [TestMethod]
    public void EveryPageHasItsFileInTheRepository()
    {
        string folder = Path.Combine(RepositoryRoot(), "docs", "help");

        foreach (string page in Pages())
            Assert.IsTrue(File.Exists(Path.Combine(folder, page + ".md")), $"docs/help/{page}.md is missing.");
    }

    // Up from the test's folder to the one holding the solution.
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Chart-Packer.slnx")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("The repository's root wasn't found above " + AppContext.BaseDirectory);
    }
}
