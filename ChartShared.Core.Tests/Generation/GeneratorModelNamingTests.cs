using ChartShared.Core.Generation;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class GeneratorModelNamingTests
{
    [TestMethod]
    public void FileNameIsNameThenExtension()
    {
        Assert.AreEqual("MyModel.json", GeneratorModelNaming.FileNameFor(new GeneratorModelId(GeneratorType.Difficulty, "MyModel")));
        Assert.AreEqual("MyPack.neuralpack", GeneratorModelNaming.FileNameFor(new GeneratorModelId(GeneratorType.NeuralDifficulty, "MyPack")));
    }

    [TestMethod]
    public void RelativePathPutsTheFileInItsTypeFolder()
    {
        Assert.AreEqual(
            Path.Combine("Difficulty", "MyModel.json"),
            GeneratorModelNaming.RelativePathFor(new GeneratorModelId(GeneratorType.Difficulty, "MyModel")));
        Assert.AreEqual(
            Path.Combine("NeuralDifficulty", "MyPack.neuralpack"),
            GeneratorModelNaming.RelativePathFor(new GeneratorModelId(GeneratorType.NeuralDifficulty, "MyPack")));
    }

    [TestMethod]
    public void PlaceholderKindUsesJson()
    {
        Assert.AreEqual("json", GeneratorModelNaming.GetExtension(GeneratorType.Difficulty));
    }

    [TestMethod]
    public void NamesRoundTripThroughStoredPaths()
    {
        var id = new GeneratorModelId(GeneratorType.Difficulty, "My Model 1");

        Assert.IsTrue(GeneratorModelNaming.TryParseStoredPath(
            Path.Combine("root", GeneratorModelNaming.RelativePathFor(id)), out var parsed));
        Assert.AreEqual(id, parsed);
    }

    [TestMethod]
    public void ParseReadsAStoredPathWithItsStorageRoot()
    {
        string path = Path.Combine("some", "folder", "Difficulty", "Drums.json");

        Assert.IsTrue(GeneratorModelNaming.TryParseStoredPath(path, out var id));
        Assert.AreEqual(new GeneratorModelId(GeneratorType.Difficulty, "Drums"), id);
    }

    [TestMethod]
    public void ParseRefusesFlatFilesWrongFoldersAndOtherExtensions()
    {
        Assert.IsFalse(GeneratorModelNaming.TryParseStoredPath("DifficultyModel.onnx", out _));
        Assert.IsFalse(GeneratorModelNaming.TryParseStoredPath(Path.Combine("folder", "DifficultyDrums.json"), out _));
        Assert.IsFalse(GeneratorModelNaming.TryParseStoredPath(Path.Combine("Difficulty", "Drums.neuralpack"), out _));
        Assert.IsFalse(GeneratorModelNaming.TryParseStoredPath(Path.Combine("NeuralDifficulty", "Pack.json"), out _));
        Assert.IsFalse(GeneratorModelNaming.TryParseStoredPath(Path.Combine("Difficulty", ".json"), out _));
        Assert.IsFalse(GeneratorModelNaming.TryParseStoredPath("", out _));
    }

    [TestMethod]
    public void DefaultTypeFollowsTheExtension()
    {
        Assert.AreEqual(GeneratorType.Difficulty, GeneratorModelNaming.DefaultTypeForExtension(".json"));
        Assert.AreEqual(GeneratorType.NeuralDifficulty, GeneratorModelNaming.DefaultTypeForExtension("neuralpack"));
        Assert.IsNull(GeneratorModelNaming.DefaultTypeForExtension(".onnx"));
        Assert.IsNull(GeneratorModelNaming.DefaultTypeForExtension(null));
    }

    [TestMethod]
    public void SuggestNameSanitizesTheStem()
    {
        Assert.AreEqual("neural-pack", GeneratorModelNaming.SuggestName(Path.Combine("dl", "neural-pack.neuralpack")));
        Assert.AreEqual("Imported", GeneratorModelNaming.SuggestName(Path.Combine("dl", "DifficultyImported.json")));
        Assert.AreEqual("", GeneratorModelNaming.SuggestName("  "));
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
