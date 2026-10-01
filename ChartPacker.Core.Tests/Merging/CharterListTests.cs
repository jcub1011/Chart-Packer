using ChartPacker.Core.Merging;

namespace ChartPacker.Core.Tests.Merging;

[TestClass]
public sealed class CharterListTests
{
    [TestMethod]
    public void NamesAreJoinedInOrder() =>
        Assert.AreEqual("Alice, Bob", CharterList.Merge(["Alice", "Bob"]));

    [TestMethod]
    public void ValuesAreSplitAtEverySeparator() =>
        Assert.AreEqual("Alice, Bob, Carol, Dave, Erin", CharterList.Merge(["Alice & Bob", "Carol / Dave + Erin"]));

    [TestMethod]
    public void NamesAreTheSameIgnoringCaseAndTagsAndTheFirstIsKept() =>
        Assert.AreEqual("<color=#FF0000>Alice</color>, Bob", CharterList.Merge(["<color=#FF0000>Alice</color>", "alice, Bob", "BOB"]));

    [TestMethod]
    public void TagsSpanningASeparatorKeepTheNameWhole() =>
        Assert.AreEqual("<b>Alice & Bob</b>, Carol", CharterList.Merge(["<b>Alice & Bob</b>", "Carol"]));

    [TestMethod]
    public void BlankAndMissingValuesAreSkipped() =>
        Assert.AreEqual("Alice", CharterList.Merge([null, "  ", "Alice", ", ,"]));

    [TestMethod]
    public void TheListNeverHasASemicolon() =>
        Assert.DoesNotContain(";", CharterList.Merge(["A", "B", "C"]));
}
