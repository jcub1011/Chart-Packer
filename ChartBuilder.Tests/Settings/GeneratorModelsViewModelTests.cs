using ChartShared.Core.Generation;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;

namespace ChartBuilder.Tests.Settings;

/// <summary>The generator model storage dialog (issue #154), with stub pickers and prompts, always in temp folders.</summary>
[TestClass]
public sealed class GeneratorModelsViewModelTests
{
    private DirectoryInfo _root = null!;
    private GeneratorModelStore _store = null!;
    private StubFiles _files = null!;
    private StubFolders _folders = null!;
    private StubConfirmations _confirmations = null!;
    private StubReveal _reveal = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-generator-dialog-");
        _store = new GeneratorModelStore(
            new GeneratorModelFolderFile(Path.Combine(_root.FullName, "generator-model-settings.json")),
            Path.Combine(_root.FullName, "GeneratorModels"));
        _files = new StubFiles();
        _folders = new StubFolders();
        _confirmations = new StubConfirmations();
        _reveal = new StubReveal();
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private GeneratorModelsViewModel Dialog() => new(_store, _files, _folders, _confirmations, _reveal);

    private async Task<GeneratorModelId> SaveModelAsync(GeneratorType type, string name, int size = 64)
    {
        var id = new GeneratorModelId(type, name);
        var bytes = new byte[size];
        new Random(154).NextBytes(bytes);
        await using var source = new MemoryStream(bytes, writable: false);
        await _store.SaveAsync(id, source, overwrite: false);
        return id;
    }

    private Task<GeneratorModelId> SaveModelAsync(string name, int size = 64) =>
        SaveModelAsync(GeneratorType.Difficulty, name, size);

    [TestMethod]
    public void ListsWhatsStored()
    {
        Assert.AreEqual(0, Dialog().Models.Count);

        SaveModelAsync("First").GetAwaiter().GetResult();

        var dialog = Dialog();
        Assert.AreEqual(1, dialog.Models.Count);
        Assert.AreEqual("First", dialog.Models[0].Name);
        Assert.AreEqual("First.json", dialog.Models[0].FileName);
        Assert.IsFalse(string.IsNullOrEmpty(dialog.Models[0].SizeText));
    }

    [TestMethod]
    public async Task RenameGivesTheSelectedModelItsEditedName()
    {
        var id = await SaveModelAsync("Old");
        var dialog = Dialog();
        dialog.SelectedModel = dialog.Models.Single(m => m.Id == id);
        dialog.NewName = "New";

        await dialog.RenameCommand.ExecuteAsync(null);

        Assert.IsTrue(File.Exists(Path.Combine(_store.ResolvedFolder, "Difficulty", "New.json")));
        Assert.IsFalse(File.Exists(Path.Combine(_store.ResolvedFolder, "Difficulty", "Old.json")));
        Assert.AreEqual("New", dialog.SelectedModel?.Name);
    }

    [TestMethod]
    public async Task RetypeMovesTheSelectedModelToTheOtherKind()
    {
        var id = await SaveModelAsync("Pack");
        var dialog = Dialog();
        dialog.SelectedModel = dialog.Models.Single(m => m.Id == id);
        dialog.NewType = GeneratorType.NeuralDifficulty;

        await dialog.RenameCommand.ExecuteAsync(null);

        Assert.IsFalse(File.Exists(Path.Combine(_store.ResolvedFolder, "Difficulty", "Pack.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_store.ResolvedFolder, "NeuralDifficulty", "Pack.neuralpack")));
        Assert.AreEqual(new GeneratorModelId(GeneratorType.NeuralDifficulty, "Pack"), dialog.SelectedModel?.Id);
    }

    [TestMethod]
    public async Task RenameOntoAnotherModelWithoutConfirmingKeepsBoth()
    {
        var first = await SaveModelAsync("First");
        await SaveModelAsync("Second");
        _confirmations.Confirm = false;
        var dialog = Dialog();
        dialog.SelectedModel = dialog.Models.Single(m => m.Id == first);
        dialog.NewName = "Second";

        await dialog.RenameCommand.ExecuteAsync(null);

        Assert.AreEqual(2, _store.List().Count);
    }

    [TestMethod]
    public async Task RetypeOntoAnotherModelWithoutConfirmingKeepsBoth()
    {
        var first = await SaveModelAsync(GeneratorType.Difficulty, "Pack");
        await SaveModelAsync(GeneratorType.NeuralDifficulty, "Pack");
        _confirmations.Confirm = false;
        var dialog = Dialog();
        dialog.SelectedModel = dialog.Models.Single(m => m.Id == first);
        dialog.NewType = GeneratorType.NeuralDifficulty;

        await dialog.RenameCommand.ExecuteAsync(null);

        Assert.AreEqual(2, _store.List().Count);
    }

    [TestMethod]
    public async Task DeleteAsksFirst()
    {
        var id = await SaveModelAsync("Old");
        _confirmations.Confirm = false;
        var dialog = Dialog();
        dialog.SelectedModel = dialog.Models.Single(m => m.Id == id);

        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.AreEqual(1, _store.List().Count);

        _confirmations.Confirm = true;
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.AreEqual(0, _store.List().Count);
    }

    [TestMethod]
    public async Task ImportKeepsAValidFileName()
    {
        string source = Path.Combine(_root.FullName, "DifficultyImported.json");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        _files.Picked = [source];
        var dialog = Dialog();

        await dialog.ImportCommand.ExecuteAsync(null);

        Assert.AreEqual(new GeneratorModelId(GeneratorType.Difficulty, "Imported"), dialog.Models.Single().Id);
    }

    [TestMethod]
    public async Task ImportNeuralPackOffersNeuralFirst()
    {
        string source = Path.Combine(_root.FullName, "my-pack.neuralpack");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        _files.Picked = [source];
        _confirmations.Choice = ConfirmChoice.First;
        var dialog = Dialog();

        await dialog.ImportCommand.ExecuteAsync(null);

        Assert.AreEqual(new GeneratorModelId(GeneratorType.NeuralDifficulty, "my-pack"), dialog.Models.Single().Id);
    }

    [TestMethod]
    public async Task ImportLetsTheUserPickTheOtherKind()
    {
        string source = Path.Combine(_root.FullName, "my-pack.neuralpack");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        _files.Picked = [source];
        _confirmations.Choice = ConfirmChoice.Second;
        var dialog = Dialog();

        await dialog.ImportCommand.ExecuteAsync(null);

        Assert.AreEqual(new GeneratorModelId(GeneratorType.Difficulty, "my-pack"), dialog.Models.Single().Id);
    }

    [TestMethod]
    public async Task ImportCancelledKindSkipsTheFile()
    {
        string source = Path.Combine(_root.FullName, "my-pack.neuralpack");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        _files.Picked = [source];
        _confirmations.Choice = ConfirmChoice.Cancel;
        var dialog = Dialog();

        await dialog.ImportCommand.ExecuteAsync(null);

        Assert.AreEqual(0, dialog.Models.Count);
    }

    [TestMethod]
    public async Task ImportWithoutConfirmingAnOverwriteKeepsTheStoredModel()
    {
        var id = await SaveModelAsync("First", size: 8);
        string source = Path.Combine(_root.FullName, "DifficultyFirst.json");
        await File.WriteAllBytesAsync(source, new byte[16]);
        _files.Picked = [source];
        _confirmations.Confirm = false;
        var dialog = Dialog();

        await dialog.ImportCommand.ExecuteAsync(null);

        await using var read = _store.OpenRead(id);
        Assert.AreEqual(8, read.Length);
    }

    [TestMethod]
    public async Task ExportCopiesTheSelectedModel()
    {
        var id = await SaveModelAsync("First");
        string dest = Path.Combine(_root.FullName, "exports");
        _folders.Picked = dest;
        var dialog = Dialog();
        dialog.SelectedModel = dialog.Models.Single(m => m.Id == id);

        await dialog.ExportCommand.ExecuteAsync(null);

        Assert.IsTrue(File.Exists(Path.Combine(dest, "First.json")));
    }

    [TestMethod]
    public async Task ChangeFolderCopiesWhatsStoredAndKeepsTheOldFiles()
    {
        await SaveModelAsync("First");
        string dest = Path.Combine(_root.FullName, "moved");
        _folders.Picked = dest;
        _confirmations.Choice = ConfirmChoice.First;
        var dialog = Dialog();

        await dialog.ChangeFolderCommand.ExecuteAsync(null);

        Assert.AreEqual(Path.GetFullPath(dest), _store.ResolvedFolder);
        Assert.IsTrue(File.Exists(Path.Combine(dest, "Difficulty", "First.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_root.FullName, "GeneratorModels", "Difficulty", "First.json")));
    }

    [TestMethod]
    public async Task ChangeFolderWithoutCopyingJustChangesTheFolder()
    {
        await SaveModelAsync("First");
        string dest = Path.Combine(_root.FullName, "moved");
        _folders.Picked = dest;
        _confirmations.Choice = ConfirmChoice.Second;
        var dialog = Dialog();

        await dialog.ChangeFolderCommand.ExecuteAsync(null);

        Assert.AreEqual(Path.GetFullPath(dest), _store.ResolvedFolder);
        Assert.IsFalse(Directory.Exists(dest));
        Assert.AreEqual(0, dialog.Models.Count);
    }

    private sealed class StubFiles : IFilePickerService
    {
        public IReadOnlyList<string> Picked { get; set; } = [];

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult(Picked);

        public Task<string?> PickAudioAsync(string title) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickAudioFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickBackgroundAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickChartAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickLyricsSourceAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickSongAsync(string title) => Task.FromResult<string?>(null);
    }

    private sealed class StubFolders : IFolderPickerService
    {
        public string? Picked { get; set; }

        public Task<string?> PickFolderAsync(string title, string? startFolder = null) => Task.FromResult(Picked);
    }

    private sealed class StubConfirmations : IConfirmationService
    {
        public bool Confirm { get; set; } = true;

        public ConfirmChoice Choice { get; set; } = ConfirmChoice.First;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(Confirm);

        public Task<ConfirmChoice> ChooseAsync(string title, string message, string firstLabel, string secondLabel, string? firstIcon = null, string? secondIcon = null) =>
            Task.FromResult(Choice);

        public Task AlertAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class StubReveal : IFileRevealService
    {
        public void Reveal(IReadOnlyList<string> paths)
        {
        }

        public void OpenFolder(string folder)
        {
            Opened.Add(folder);
        }

        public List<string> Opened { get; } = [];
    }
}
