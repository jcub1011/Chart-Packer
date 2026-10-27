using System.Text.Json.Serialization;
using ChartShared.Core.Generation;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Generation;

internal sealed record TestWeights(int Version, double[] Values);

[JsonSerializable(typeof(TestWeights))]
internal sealed partial class TestWeightsJsonContext : JsonSerializerContext
{
}

[TestClass]
public sealed class GeneratorModelStoreTests
{
    private DirectoryInfo _root = null!;
    private string _settings = null!;
    private string _folder = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-generator-models-");
        _settings = Path.Combine(_root.FullName, "generator-model-settings.json");
        _folder = Path.Combine(_root.FullName, "models");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private GeneratorModelStore Store(string? customFolder = null)
    {
        var folders = new GeneratorModelFolderFile(_settings);
        if (customFolder is not null)
            folders.SaveFolder(customFolder);
        return new GeneratorModelStore(folders, _folder);
    }

    private static byte[] Bytes(int size)
    {
        var random = new Random(154);
        var bytes = new byte[size];
        random.NextBytes(bytes);
        return bytes;
    }

    [TestMethod]
    public async Task AModelSavedIsListedAndReadBack()
    {
        var store = Store();
        var id = new GeneratorModelId(GeneratorType.Difficulty, "First");
        byte[] bytes = Bytes(1024 * 1024 + 7);

        await using (var source = new MemoryStream(bytes, writable: false))
            await store.SaveAsync(id, source, overwrite: false);

        var listed = store.List();
        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual(id, listed[0].Id);
        Assert.AreEqual(bytes.Length, listed[0].Size);
        Assert.AreEqual(Path.Combine(_folder, "Difficulty", "First.json"), listed[0].Path);

        await using var read = store.OpenRead(id);
        await using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        CollectionAssert.AreEqual(bytes, copy.ToArray());
    }

    [TestMethod]
    public async Task ASaveWithoutOverwriteKeepsTheStoredModel()
    {
        var store = Store();
        var id = new GeneratorModelId(GeneratorType.Difficulty, "First");
        await using (var first = new MemoryStream(Bytes(16), writable: false))
            await store.SaveAsync(id, first, overwrite: false);

        await using (var second = new MemoryStream(Bytes(32), writable: false))
            await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(id, second, overwrite: false));

        await using var read = store.OpenRead(id);
        Assert.AreEqual(16, read.Length);
    }

    [TestMethod]
    public async Task MissingFolderListsNothingAndReadsThrow()
    {
        var store = Store();

        Assert.AreEqual(0, store.List().Count);
        Assert.IsFalse(store.HasModels());
        Assert.ThrowsExactly<FileNotFoundException>(() => store.OpenRead(new GeneratorModelId(GeneratorType.Difficulty, "Missing")));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task TempFilesAreNotModels()
    {
        var store = Store();
        Directory.CreateDirectory(Path.Combine(_folder, "Difficulty"));
        await File.WriteAllTextAsync(Path.Combine(_folder, "Difficulty", "A.json.abc.tmp"), "half");

        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public async Task FlatFilesAreNotModels()
    {
        var store = Store();
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(Path.Combine(_folder, "DifficultyFirst.json"), Bytes(16));

        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public async Task ImportExportDeleteRoundTrip()
    {
        var store = Store();
        string source = Path.Combine(_root.FullName, "source.bin");
        await File.WriteAllBytesAsync(source, Bytes(1024));
        var id = new GeneratorModelId(GeneratorType.Difficulty, "Imported");

        await store.ImportAsync(source, id, overwrite: false);

        string dest = Path.Combine(_root.FullName, "exports", "out.json");
        await store.ExportAsync(id, dest, overwrite: false);
        Assert.IsTrue(File.Exists(dest));

        Assert.IsTrue(store.Delete(id));
        Assert.IsFalse(store.Delete(id));
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public async Task ImportOfAMissingFileThrows()
    {
        var store = Store();

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            store.ImportAsync(Path.Combine(_root.FullName, "nope.bin"), new GeneratorModelId(GeneratorType.Difficulty, "Nope"), overwrite: false));
    }

    [TestMethod]
    public async Task RenameKeepsTheKindAndMovesTheFile()
    {
        var store = Store();
        var id = new GeneratorModelId(GeneratorType.Difficulty, "Old");
        await using (var source = new MemoryStream(Bytes(64), writable: false))
            await store.SaveAsync(id, source, overwrite: false);

        var renamed = store.Rename(id, "New", overwrite: false);

        Assert.AreEqual(new GeneratorModelId(GeneratorType.Difficulty, "New"), renamed);
        Assert.IsFalse(File.Exists(Path.Combine(_folder, "Difficulty", "Old.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_folder, "Difficulty", "New.json")));
    }

    [TestMethod]
    public async Task RenameOntoAnotherModelNeedsOverwrite()
    {
        var store = Store();
        var first = new GeneratorModelId(GeneratorType.Difficulty, "First");
        var second = new GeneratorModelId(GeneratorType.Difficulty, "Second");
        await using (var a = new MemoryStream(Bytes(8), writable: false))
            await store.SaveAsync(first, a, overwrite: false);
        await using (var b = new MemoryStream(Bytes(8), writable: false))
            await store.SaveAsync(second, b, overwrite: false);

        Assert.ThrowsExactly<IOException>(() => store.Rename(first, "Second", overwrite: false));
        store.Rename(first, "Second", overwrite: true);
        Assert.AreEqual(1, store.List().Count);
    }

    [TestMethod]
    public async Task MoveChangesTheKindAndMovesTheFile()
    {
        var store = Store();
        var id = new GeneratorModelId(GeneratorType.Difficulty, "Pack");
        await using (var source = new MemoryStream(Bytes(64), writable: false))
            await store.SaveAsync(id, source, overwrite: false);

        var moved = store.Move(id, GeneratorType.NeuralDifficulty, "Pack", overwrite: false);

        Assert.AreEqual(new GeneratorModelId(GeneratorType.NeuralDifficulty, "Pack"), moved);
        Assert.IsFalse(File.Exists(Path.Combine(_folder, "Difficulty", "Pack.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_folder, "NeuralDifficulty", "Pack.neuralpack")));
        Assert.AreEqual(1, store.List().Count);
    }

    [TestMethod]
    public async Task MoveOntoAnotherModelNeedsOverwrite()
    {
        var store = Store();
        var first = new GeneratorModelId(GeneratorType.Difficulty, "First");
        var second = new GeneratorModelId(GeneratorType.NeuralDifficulty, "First");
        await using (var a = new MemoryStream(Bytes(8), writable: false))
            await store.SaveAsync(first, a, overwrite: false);
        await using (var b = new MemoryStream(Bytes(8), writable: false))
            await store.SaveAsync(second, b, overwrite: false);

        Assert.ThrowsExactly<IOException>(() => store.Move(first, GeneratorType.NeuralDifficulty, "First", overwrite: false));
        store.Move(first, GeneratorType.NeuralDifficulty, "First", overwrite: true);
        Assert.AreEqual(1, store.List().Count);
    }

    [TestMethod]
    public async Task MigrateCopiesAndKeepsTheSource()
    {
        var store = Store();
        var id = new GeneratorModelId(GeneratorType.Difficulty, "First");
        await using (var source = new MemoryStream(Bytes(128), writable: false))
            await store.SaveAsync(id, source, overwrite: false);
        string dest = Path.Combine(_root.FullName, "moved");

        var result = await store.MigrateCopyAsync(dest, overwrite: false);

        Assert.AreEqual(1, result.Copied);
        Assert.AreEqual(0, result.Skipped);
        Assert.AreEqual(0, result.Errors.Count);
        Assert.IsTrue(File.Exists(Path.Combine(_folder, "Difficulty", "First.json")));
        Assert.IsTrue(File.Exists(Path.Combine(dest, "Difficulty", "First.json")));
    }

    [TestMethod]
    public async Task MigrateSkipsCollisionsWithoutOverwrite()
    {
        var store = Store();
        await using (var source = new MemoryStream(Bytes(32), writable: false))
            await store.SaveAsync(new GeneratorModelId(GeneratorType.Difficulty, "First"), source, overwrite: false);
        string dest = Path.Combine(_root.FullName, "moved", "Difficulty");
        Directory.CreateDirectory(dest);
        await File.WriteAllBytesAsync(Path.Combine(dest, "First.json"), Bytes(32));

        var skipped = await store.MigrateCopyAsync(Path.Combine(_root.FullName, "moved"), overwrite: false);
        Assert.AreEqual(0, skipped.Copied);
        Assert.AreEqual(1, skipped.Skipped);

        var copied = await store.MigrateCopyAsync(Path.Combine(_root.FullName, "moved"), overwrite: true);
        Assert.AreEqual(1, copied.Copied);
        Assert.AreEqual(0, copied.Skipped);
    }

    [TestMethod]
    public async Task MigrateOntoTheSameFolderDoesNothing()
    {
        var store = Store();

        var result = await store.MigrateCopyAsync(_folder, overwrite: true);

        Assert.AreEqual(0, result.Copied);
        Assert.AreEqual(0, result.Skipped);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void CustomFolderOverridesTheDefault()
    {
        string custom = Path.Combine(_root.FullName, "elsewhere");

        var store = Store(custom);

        Assert.AreEqual(Path.GetFullPath(custom), store.ResolvedFolder);
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public async Task ReadAllBytesReturnsWhatWasSaved()
    {
        var store = Store();
        var id = new GeneratorModelId(GeneratorType.Difficulty, "First");
        byte[] bytes = Bytes(1024);
        await using (var source = new MemoryStream(bytes, writable: false))
            await store.SaveAsync(id, source, overwrite: false);

        CollectionAssert.AreEqual(bytes, await store.ReadAllBytesAsync(id));
    }

    [TestMethod]
    public async Task JsonWeightsRoundTripThroughTheTypedHelper()
    {
        var store = Store();
        var id = new GeneratorModelId(GeneratorType.Difficulty, "Weights");
        var weights = new TestWeights(3, [0.5, 1.5]);
        await using (var source = new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(weights, TestWeightsJsonContext.Default.TestWeights), writable: false))
            await store.SaveAsync(id, source, overwrite: false);

        var loaded = await store.LoadJsonAsync(id, TestWeightsJsonContext.Default.TestWeights);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(weights.Version, loaded.Version);
        CollectionAssert.AreEqual(weights.Values, loaded.Values);
    }

    [TestMethod]
    public async Task InvalidNamesThrow()
    {
        var store = Store();

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await using var source = new MemoryStream(Bytes(8), writable: false);
            await store.SaveAsync(new GeneratorModelId(GeneratorType.Difficulty, "  "), source, overwrite: false);
        });
    }
}
