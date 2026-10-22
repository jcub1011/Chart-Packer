using ChartShared.Core.Updating;

namespace ChartShared.Core.Tests.Updating;

[TestClass]
public sealed class SemanticVersionTests
{
    [TestMethod]
    [DataRow("v1.2.0", 1, 2, 0, null)]
    [DataRow("1.2.0", 1, 2, 0, null)]
    [DataRow("V0.5.0", 0, 5, 0, null)]
    [DataRow("1.2", 1, 2, 0, null)]
    [DataRow("1", 1, 0, 0, null)]
    [DataRow("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [DataRow("v1.3.0-beta.1", 1, 3, 0, "beta.1")]
    [DataRow("1.2.3+build-5", 1, 2, 3, null)]
    [DataRow("1.2.3-rc.1+build.7", 1, 2, 3, "rc.1")]
    public void ParsesVersions(string text, int major, int minor, int patch, string? prerelease)
    {
        var version = SemanticVersion.TryParse(text);
        Assert.IsNotNull(version);
        Assert.AreEqual(major, version.Major);
        Assert.AreEqual(minor, version.Minor);
        Assert.AreEqual(patch, version.Patch);
        Assert.AreEqual(prerelease, version.Prerelease);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("v")]
    [DataRow("1.2.3.4")]
    [DataRow("1.02.3")]
    [DataRow("a.b.c")]
    [DataRow("1.2-")]
    [DataRow("1. 2.3")]
    [DataRow("1.+2.3")]
    [DataRow("1.2.3-a..b")]
    [DataRow("1.2.3-beta.")]
    public void RejectsNonVersions(string? text)
    {
        Assert.IsNull(SemanticVersion.TryParse(text));
    }

    [TestMethod]
    public void NewerNumericsOutrankOlder()
    {
        Assert.IsTrue(SemanticVersion.TryParse("v1.2.1")! > SemanticVersion.TryParse("v1.2.0"));
        Assert.IsTrue(SemanticVersion.TryParse("v1.10.0")! > SemanticVersion.TryParse("v1.2.0"));
        Assert.IsFalse(SemanticVersion.TryParse("v1.2.0")! > SemanticVersion.TryParse("v1.2.0"));
    }

    [TestMethod]
    public void StableOutranksItsPrereleases()
    {
        var stable = SemanticVersion.TryParse("1.2.0")!;
        var pre = SemanticVersion.TryParse("1.2.0-beta.1")!;
        Assert.IsTrue(stable > pre);
        Assert.IsTrue(pre < stable);
    }

    [TestMethod]
    [DataRow("1.0.0-beta.2", "1.0.0-beta.10")]
    [DataRow("1.0.0-alpha", "1.0.0-alpha.1")]
    [DataRow("1.0.0-1", "1.0.0-alpha")]
    [DataRow("1.0.0-alpha.beta", "1.0.0-beta")]
    [DataRow("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [DataRow("1.0.0-rc.1", "1.0.0")]
    public void PrereleasesFollowSemVerPrecedence(string lower, string higher)
    {
        Assert.IsTrue(SemanticVersion.TryParse(higher)! > SemanticVersion.TryParse(lower));
        Assert.IsTrue(SemanticVersion.TryParse(lower)! < SemanticVersion.TryParse(higher));
    }
}
