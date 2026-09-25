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
}
