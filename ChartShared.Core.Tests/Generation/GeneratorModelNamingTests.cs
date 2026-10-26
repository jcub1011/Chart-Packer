using ChartShared.Core.Generation;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class GeneratorModelNamingTests
{
    [TestMethod]
    public void FileNameIsTypeThenNameThenExtension()
    {
        Assert.AreEqual("DifficultyMyModel.json", GeneratorModelNaming.FileNameFor(new GeneratorModelId(GeneratorType.Difficulty, "MyModel")));
    }

    [TestMethod]
    public void PlaceholderKindUsesJson()
    {
        Assert.AreEqual("json", GeneratorModelNaming.GetExtension(GeneratorType.Difficulty));
    }

    [TestMethod]
    public void NamesRoundTripThroughFileNames()
    {
        var id = new GeneratorModelId(GeneratorType.Difficulty, "My Model 1");

        Assert.IsTrue(GeneratorModelNaming.TryParseFileName(GeneratorModelNaming.FileNameFor(id), out var parsed));
        Assert.AreEqual(id, parsed);
    }

    [TestMethod]
    public void ParseReadsAFileNameWithItsFolder()
    {
        string path = Path.Combine("some", "folder", "DifficultyDrums.json");

        Assert.IsTrue(GeneratorModelNaming.TryParseFileName(path, out var id));
        Assert.AreEqual(new GeneratorModelId(GeneratorType.Difficulty, "Drums"), id);
    }

    [TestMethod]
    public void ParseRefusesOtherExtensionsAndBlankNames()
    {
        Assert.IsFalse(GeneratorModelNaming.TryParseFileName("DifficultyModel.onnx", out _));
        Assert.IsFalse(GeneratorModelNaming.TryParseFileName("Difficulty.json", out _));
        Assert.IsFalse(GeneratorModelNaming.TryParseFileName("Random.json", out _));
        Assert.IsFalse(GeneratorModelNaming.TryParseFileName("", out _));
    }

    [TestMethod]
    public void SanitizeDropsWhatAFileCannotHold()
    {
        Assert.AreEqual("ab", GeneratorModelNaming.SanitizeName(" a/b "));
        Assert.AreEqual("", GeneratorModelNaming.SanitizeName("  "));
    }

    [TestMethod]
    public void ValidateRefusesBlankNames()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorModelNaming.ValidateName("  "));
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorModelNaming.ValidateName("...".Replace(".", "/")));
    }
}
