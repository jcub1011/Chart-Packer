using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class MetadataDifficultyTests
{
    [TestMethod]
    [DataRow("diff_guitar", "-1")]
    [DataRow("diff_drums_real", "-5")]
    [DataRow("DIFF_Bass", "-1")]
    public void NegativeDifficultyIsUnset(string key, string value) =>
        Assert.IsTrue(MetadataDifficulty.IsUnset(key, value));

    [TestMethod]
    [DataRow("diff_guitar", "0")]
    [DataRow("diff_guitar", "6")]
    [DataRow("diff_guitar", "hard")]
    [DataRow("diff_guitar", "")]
    [DataRow("delay", "-1")]
    [DataRow("song_length", "-1")]
    [DataRow("difficulty", "-1")]
    public void OtherValuesAreSet(string key, string value) =>
        Assert.IsFalse(MetadataDifficulty.IsUnset(key, value));

    [TestMethod]
    [DataRow("0", 0)]
    [DataRow("3", 3)]
    [DataRow("12", 12)]
    [DataRow("-1", null)]
    [DataRow("-5", null)]
    [DataRow("hard", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void TierIsANonNegativeInteger(string? value, int? tier) =>
        Assert.AreEqual(tier, MetadataDifficulty.TierOf(value));

    [TestMethod]
    public void UnsetWritesMinusOneOverAStoredDifficulty()
    {
        var item = Item(("diff_guitar", "4"));

        MetadataDifficulty.Unset([item], "diff_guitar");

        Assert.AreEqual("-1", Value(item, "diff_guitar"));
    }

    [TestMethod]
    public void UnsetAddsNothingWhereNoDifficultyIsStored()
    {
        var missing = Item(("name", "A"));
        var unset = Item(("diff_guitar", "-1"));
        missing.Draft.Set("diff_guitar", "3");
        unset.Draft.Set("diff_guitar", "5");

        MetadataDifficulty.Unset([missing, unset], "diff_guitar");

        Assert.IsTrue(missing.Draft.IsEmpty);
        Assert.IsTrue(unset.Draft.IsEmpty);
    }

    [TestMethod]
    public void UnsetInEveryItemCountsMissingAndNegativeValues()
    {
        Assert.IsTrue(MetadataDifficulty.IsUnsetIn([Item(("diff_bass", "-1")), Item(("name", "A"))], "diff_bass"));
        Assert.IsFalse(MetadataDifficulty.IsUnsetIn([Item(("diff_bass", "-1")), Item(("diff_bass", "2"))], "diff_bass"));
        Assert.IsFalse(MetadataDifficulty.IsUnsetIn([Item(("diff_bass", "hard"))], "diff_bass"));
    }

    [TestMethod]
    public void UnsetInEveryItemTakesTheDraft()
    {
        var item = Item(("diff_bass", "3"));
        item.Draft.Remove("diff_bass");
        Assert.IsTrue(MetadataDifficulty.IsUnsetIn([item], "diff_bass"));

        var added = Item(("name", "A"));
        added.Draft.Set("diff_bass", "1");
        Assert.IsFalse(MetadataDifficulty.IsUnsetIn([added], "diff_bass"));
    }

    private static MetadataItem Item(params (string Key, string Value)[] pairs) =>
        new([.. pairs.Select(p => KeyValuePair.Create(p.Key, p.Value))], new MetadataDraft());

    private static string? Value(MetadataItem item, string key) =>
        item.Draft.ApplyTo(item.Original).FirstOrDefault(p => p.Key == key).Value;
}
