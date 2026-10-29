using System.Text.Json.Nodes;
using ChartShared.Core.Keybinds;

namespace ChartShared.Core.Tests.Keybinds;

[TestClass]
public sealed class KeybindsServiceTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-keybinds-");
        _path = Path.Combine(_root.FullName, "keybinds.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private static KeybindCatalog Shared() => new(
    [
        Action("file.save", "Ctrl+S"),
        Action("edit.redo", "Ctrl+Y,Ctrl+Shift+Z", locked: true),
        Action("media.play", "Space", bare: true),
    ]);

    private static KeybindCatalog Builder() => new(Shared().Concat(
    [
        Action("builder.find", "Ctrl+F"),
    ]));

    private static KeybindCatalog Packer() => new(Shared().Concat(
    [
        Action("packer.rescan", "F5", bare: true),
    ]));

    private static KeyAction Action(string id, string defaults, bool locked = false, bool bare = false) => new()
    {
        Id = id,
        Name = id,
        Category = "Test",
        Scope = KeyScope.Global,
        Defaults = KeyChord.List(defaults.Split(',')),
        Locked = locked,
        AllowBare = bare,
    };

    [TestMethod]
    public void AMissingFileGivesTheDefaults()
    {
        var service = KeybindsService.Load(_path, Shared());

        Assert.AreEqual("Ctrl+S", service.Map.Display("file.save"));
        Assert.IsFalse(File.Exists(_path));
    }

    [TestMethod]
    public void ACorruptFileGivesTheDefaults()
    {
        File.WriteAllText(_path, "{ not json");

        var service = KeybindsService.Load(_path, Shared());

        Assert.AreEqual("Ctrl+S", service.Map.Display("file.save"));
    }

    [TestMethod]
    public void ChangedKeysReadBack()
    {
        var service = KeybindsService.Load(_path, Shared());
        service.Map.Set("file.save", KeyChord.List("Ctrl+W", "Ctrl+Alt+KeyW"));
        service.Map.Set("media.play", []);

        Assert.IsTrue(service.Save());
        var read = KeybindsService.Load(_path, Shared());

        CollectionAssert.AreEqual(KeyChord.List("Ctrl+W", "Ctrl+Alt+KeyW").ToArray(), read.Map.ChordsOf("file.save").ToArray());
        Assert.AreEqual(0, read.Map.ChordsOf("media.play").Count);
    }

    [TestMethod]
    public void OnlyChangedActionsAreWrittenAndTheVersionIsStamped()
    {
        var service = KeybindsService.Load(_path, Shared());
        service.Map.Set("file.save", KeyChord.List("Ctrl+W"));

        service.Save();

        var file = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.AreEqual(KeybindsService.SchemaVersion, (int)file["SchemaVersion"]!);
        CollectionAssert.AreEqual(new[] { "file.save" }, file["Bindings"]!.AsObject().Select(p => p.Key).ToArray());
    }

    [TestMethod]
    public void PuttingAKeyBackToItsDefaultTakesItOutOfTheFile()
    {
        var service = KeybindsService.Load(_path, Shared());
        service.Map.Set("file.save", KeyChord.List("Ctrl+W"));
        service.Save();

        service.Map.Reset("file.save");
        service.Save();

        var file = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.AreEqual(0, file["Bindings"]!.AsObject().Count);
    }

    [TestMethod]
    public void TheTwoAppsKeepEachOthersChangesInTheSharedFile()
    {
        var builder = KeybindsService.Load(_path, Builder());
        var packer = KeybindsService.Load(_path, Packer());

        builder.Map.Set("builder.find", KeyChord.List("Ctrl+Alt+KeyF"));
        builder.Save();
        packer.Map.Set("packer.rescan", KeyChord.List("F6"));
        packer.Save();

        var read = KeybindsService.Load(_path, Builder());
        Assert.AreEqual("Ctrl+Alt+F", read.Map.Display("builder.find"));
        var file = JsonNode.Parse(File.ReadAllText(_path))!["Bindings"]!.AsObject();
        Assert.IsTrue(file.ContainsKey("packer.rescan"), "the other app's entry survives, though this build doesn't know it");
    }

    [TestMethod]
    public void AnActionBothAppsShareIsReboundOnceForBoth()
    {
        var builder = KeybindsService.Load(_path, Builder());
        builder.Map.Set("file.save", KeyChord.List("Ctrl+W"));
        builder.Save();

        var packer = KeybindsService.Load(_path, Packer());

        Assert.AreEqual("Ctrl+W", packer.Map.Display("file.save"));
    }

    [TestMethod]
    public void ASaveKeepsWhatTheOtherAppChangedMeanwhileAndTheMapTakesIt()
    {
        var builder = KeybindsService.Load(_path, Builder());
        var packer = KeybindsService.Load(_path, Packer());

        // The Packer hasn't been activated since, so it hasn't seen this.
        builder.Map.Set("file.save", KeyChord.List("Ctrl+W"));
        builder.Save();
        packer.Map.Set("packer.rescan", KeyChord.List("F6"));

        Assert.IsTrue(packer.Save());

        var file = JsonNode.Parse(File.ReadAllText(_path))!["Bindings"]!.AsObject();
        Assert.IsTrue(file.ContainsKey("file.save"), "the other app's change is still in the file");
        Assert.IsTrue(file.ContainsKey("packer.rescan"));
        Assert.AreEqual("Ctrl+W", packer.Map.Display("file.save"), "and the map shows it without a restart");
        Assert.AreEqual("F6", packer.Map.Display("packer.rescan"));
        Assert.IsFalse(packer.ReloadIfChanged());
    }

    [TestMethod]
    public void LockedAndUnknownEntriesInTheFileAreIgnoredOnLoad()
    {
        File.WriteAllText(_path, """
            { "SchemaVersion": 1, "Bindings": {
                "edit.redo": ["Ctrl+R"], "file.save": ["Ctrl+W"], "mystery": ["Ctrl+M"], "media.play": ["Ctrl+"] } }
            """);

        var service = KeybindsService.Load(_path, Shared());

        Assert.AreEqual(2, service.Map.ChordsOf("edit.redo").Count);
        Assert.AreEqual("Ctrl+W", service.Map.Display("file.save"));
        Assert.AreEqual("Space", service.Map.Display("media.play"));
    }

    [TestMethod]
    public void ChangesFromAnotherProcessAreReloaded()
    {
        var mine = KeybindsService.Load(_path, Shared());
        Assert.IsFalse(mine.ReloadIfChanged());

        var other = KeybindsService.Load(_path, Shared());
        other.Map.Set("file.save", KeyChord.List("Ctrl+W"));
        other.Save();

        Assert.IsTrue(mine.ReloadIfChanged());
        Assert.AreEqual("Ctrl+W", mine.Map.Display("file.save"));
        Assert.IsFalse(mine.ReloadIfChanged());
    }

    [TestMethod]
    public void AFileFromANewerBuildIsNotOverwritten()
    {
        string original = """{ "SchemaVersion": 99, "Bindings": { "file.save": ["Ctrl+W"] } }""";
        File.WriteAllText(_path, original);
        var service = KeybindsService.Load(_path, Shared());

        service.Map.Set("file.save", KeyChord.List("Ctrl+E"));

        Assert.IsFalse(service.Save());
        Assert.AreEqual(original, File.ReadAllText(_path));
    }

    [TestMethod]
    public void AnExportedLayoutImportsIntoAnotherMap()
    {
        string layoutPath = Path.Combine(_root.FullName, "my-layout.json");
        var service = KeybindsService.Load(_path, Shared());
        service.Map.Set("file.save", KeyChord.List("Ctrl+W"));
        service.Map.Set("media.play", KeyChord.List("Ctrl+Space"));

        Assert.IsTrue(KeybindsLayout.Write(layoutPath, service.Map));
        var read = KeybindsLayout.Read(layoutPath);
        var other = new KeyMap(Shared());
        other.ApplyLayout(read.Bindings!, replace: true);

        Assert.AreEqual(LayoutFailure.None, read.Failure);
        Assert.AreEqual("Ctrl+W", other.Display("file.save"));
        Assert.AreEqual("Ctrl+Space", other.Display("media.play"));
    }

    [TestMethod]
    public void ALayoutFileThatIsNotOneIsRefused()
    {
        string path = Path.Combine(_root.FullName, "layout.json");

        Assert.AreEqual(LayoutFailure.Unreadable, KeybindsLayout.Read(path).Failure);

        File.WriteAllText(path, "not json");
        Assert.AreEqual(LayoutFailure.NotKeybinds, KeybindsLayout.Read(path).Failure);

        File.WriteAllText(path, """{ "Name": "x" }""");
        Assert.AreEqual(LayoutFailure.NotKeybinds, KeybindsLayout.Read(path).Failure);

        File.WriteAllText(path, """{ "Bindings": { "file.save": "Ctrl+W" } }""");
        Assert.AreEqual(LayoutFailure.NotKeybinds, KeybindsLayout.Read(path).Failure);

        File.WriteAllText(path, new string(' ', (int)KeybindsLayout.MaxBytes + 1));
        Assert.AreEqual(LayoutFailure.TooLarge, KeybindsLayout.Read(path).Failure);
    }
}
