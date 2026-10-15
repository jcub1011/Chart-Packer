using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Scanning;

[TestClass]
public sealed class ScanPositionTests
{
    [TestMethod]
    public void ComparesRankByRank()
    {
        var a = ScanPosition.Root.Child(0).Child(5);
        var b = ScanPosition.Root.Child(1).Child(0);

        Assert.IsLessThan(0, a.CompareTo(b));
        Assert.IsGreaterThan(0, b.CompareTo(a));
    }

    [TestMethod]
    public void PrefixSortsFirst()
    {
        var folder = ScanPosition.Root.Child(2);
        var inside = folder.Child(0);

        Assert.IsLessThan(0, ScanPosition.Root.CompareTo(folder));
        Assert.IsLessThan(0, folder.CompareTo(inside));
    }

    [TestMethod]
    public void EverythingInAFolderSortsBeforeItsNextSibling()
    {
        var deep = ScanPosition.Root.Child(0).Child(9).Child(9);
        var sibling = ScanPosition.Root.Child(1);

        Assert.IsLessThan(0, deep.CompareTo(sibling));
    }

    [TestMethod]
    public void EqualPathsAreEqual()
    {
        var a = ScanPosition.Root.Child(3).Child(1);
        var b = ScanPosition.Root.Child(3).Child(1);

        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.AreEqual(0, a.CompareTo(b));
        Assert.AreNotEqual(a, ScanPosition.Root.Child(3));
    }

    [TestMethod]
    public void NegativeRankThrows() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScanPosition.Root.Child(-1));
}
