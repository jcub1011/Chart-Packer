using ChartPacker.Core.Packing;

namespace ChartPacker.Core.Tests.Packing;

[TestClass]
public sealed class StagingPathTests
{
    [TestMethod]
    public void TheNameIsBesideTheTarget()
    {
        string target = Path.Combine(Path.GetTempPath(), "out", "Artist - Song");

        string staging = StagingPath.For(target);

        Assert.AreEqual(Path.GetDirectoryName(target), Path.GetDirectoryName(staging));
        StringAssert.StartsWith(Path.GetFileName(staging), ".Artist - Song.");
        StringAssert.EndsWith(staging, StagingPath.Suffix);
    }

    [TestMethod]
    public void ATrailingSeparatorIsIgnored()
    {
        string target = Path.Combine(Path.GetTempPath(), "out", "Song");

        string staging = StagingPath.For(target + Path.DirectorySeparatorChar);

        Assert.AreEqual(Path.GetDirectoryName(target), Path.GetDirectoryName(staging));
        StringAssert.StartsWith(Path.GetFileName(staging), ".Song.");
    }

    [TestMethod]
    public void EachNameIsNew()
    {
        string target = Path.Combine(Path.GetTempPath(), "Song");

        Assert.AreNotEqual(StagingPath.For(target), StagingPath.For(target));
    }

    [TestMethod]
    public void DeletingWhatIsntThereDoesNothing()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        StagingPath.TryDeleteFile(missing);
        StagingPath.TryDeleteDirectory(missing);
    }
}
