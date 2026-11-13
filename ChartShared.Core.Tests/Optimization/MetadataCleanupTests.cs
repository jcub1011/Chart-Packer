using ChartShared.Core.Charts;
using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Tests.Charts;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary><see cref="MetadataCleanup"/>: what it finds in a folder's <c>song.ini</c> and a <c>.sng</c>'s metadata, step by step.</summary>
[TestClass]
public sealed class MetadataCleanupTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-opt-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private static MetadataCleanupOptions Only(Func<MetadataCleanupOptions, MetadataCleanupOptions> step) =>
        step(new MetadataCleanupOptions(Enabled: true, PruneEmptyParts: false, StripEditorEvents: false, DropEmptyValues: false, ReplaceLegacyKeys: false));

    private static byte[] Anchored() => new MidiBuilder()
        .Track("Song").Tempo(0, 120).Text(0, "[chartpacker_anchor 0]").Text(960, "[other]")
        .Track("PART GUITAR").Note(0, 96)
        .Build();

    private static string[] Keys(MetadataCleanupPlan plan) => [.. plan.After.Select(p => p.Key)];

    [TestMethod]
    public async Task EmptyValuesAreDropped()
    {
        var entry = Folder(_root.FullName, "S", "[song]\nname = Song\nartist =\nalbum =   \n");

        var plan = await MetadataCleanup.AnalyzeAsync(entry, Only(o => o with { DropEmptyValues = true }), TestContext.CancellationToken);

        Assert.IsTrue(plan.HasChanges);
        Assert.AreSequenceEqual(["name"], Keys(plan));
    }

    [TestMethod]
    public async Task ADuplicatedKeyWithAValueKeepsItsValueWhenOneOfItsPairsIsEmpty()
    {
        // A song.ini keeps only a key's last value, so only a .sng can hold a key twice.
        var entry = await SngAsync(_root.FullName, "S", [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", ""), KeyValuePair.Create("artist", "Someone")]);

        var plan = await MetadataCleanup.AnalyzeAsync(entry, Only(o => o with { DropEmptyValues = true }), TestContext.CancellationToken);

        Assert.IsFalse(plan.HasChanges, "A draft can only remove a key whole, which would lose Someone.");
        Assert.IsTrue(plan.After.Any(p => p.Key == "artist" && p.Value == "Someone"));
    }

    [TestMethod]
    public async Task ADuplicatedKeyThatIsEmptyEverywhereIsDropped()
    {
        var entry = await SngAsync(_root.FullName, "S", [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", ""), KeyValuePair.Create("artist", "  ")]);

        var plan = await MetadataCleanup.AnalyzeAsync(entry, Only(o => o with { DropEmptyValues = true }), TestContext.CancellationToken);

        Assert.AreSequenceEqual(["name"], Keys(plan));
    }

    [TestMethod]
    public async Task ALegacyKeyDuplicatedWithDifferentValuesIsLeftAlone()
    {
        var entry = await SngAsync(_root.FullName, "S", [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("frets", "One"), KeyValuePair.Create("frets", "Two")]);

        var plan = await MetadataCleanup.AnalyzeAsync(entry, Only(o => o with { ReplaceLegacyKeys = true }), TestContext.CancellationToken);

        Assert.IsFalse(plan.HasChanges);
        Assert.AreEqual(2, plan.After.Count(p => p.Key == "frets"));
    }

    [TestMethod]
    public async Task LegacyKeysAreRenamedUnlessTheReplacementIsThere()
    {
        var entry = Folder(_root.FullName, "S", "[song]\nname = Song\nfrets = Someone\ntrack = 4\nalbum_track = 9\n");

        var plan = await MetadataCleanup.AnalyzeAsync(entry, Only(o => o with { ReplaceLegacyKeys = true }), TestContext.CancellationToken);

        Assert.AreEqual("Someone", plan.After.Single(p => p.Key == "charter").Value);
        Assert.IsFalse(plan.After.Any(p => p.Key == "frets"));
        Assert.AreEqual("4", plan.After.Single(p => p.Key == "track").Value, "A legacy key whose replacement is already set is left, as its quick fix does.");
        Assert.AreEqual("9", plan.After.Single(p => p.Key == "album_track").Value);
    }

    [TestMethod]
    public async Task KeysOfPartsWithNoNotesAreDropped()
    {
        byte[] midi = Anchored();
        var entry = Folder(_root.FullName, "S", "[song]\nname = Song\ndiff_guitar = 3\ndiff_drums = 2\n", ("notes.mid", midi));

        var plan = await MetadataCleanup.AnalyzeAsync(entry, Only(o => o with { PruneEmptyParts = true }), TestContext.CancellationToken);

        Assert.AreSequenceEqual(["name", "diff_guitar"], Keys(plan));
        Assert.IsNull(plan.NewMidiBytes, "Stripping is its own step.");
    }

    [TestMethod]
    public async Task EditorEventsAreStrippedFromNotesMid()
    {
        byte[] midi = Anchored();
        var entry = Folder(_root.FullName, "S", null, ("notes.mid", midi));

        var plan = await MetadataCleanup.AnalyzeAsync(entry, Only(o => o with { StripEditorEvents = true }), TestContext.CancellationToken);

        Assert.IsTrue(plan.HasChanges);
        Assert.AreEqual("notes.mid", plan.MidiName);
        Assert.IsTrue(plan.NewMidiBytes!.Length < midi.Length);
        Assert.AreEqual(midi.Length, plan.MidiBytesBefore);
        Assert.IsFalse(plan.Edits.HasMetadataChanges);
        Assert.IsTrue(plan.BytesAfter < plan.BytesBefore);
    }

    [TestMethod]
    public async Task ADisabledStepChangesNothing()
    {
        var entry = Folder(_root.FullName, "S", "[song]\nname = Song\nartist =\nfrets = A\ndiff_drums = 2\n", ("notes.mid", Anchored()));

        var plan = await MetadataCleanup.AnalyzeAsync(entry, Only(o => o), TestContext.CancellationToken);

        Assert.IsFalse(plan.HasChanges);
        Assert.AreEqual(4, plan.After.Count);
    }

    [TestMethod]
    public async Task ACleanChartHasNothingToChange()
    {
        var entry = Folder(_root.FullName, "S", "[song]\nname = Song\ndiff_guitar = 3\n", ("notes.mid", new MidiBuilder().Track("Song").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Build()));

        var plan = await MetadataCleanup.AnalyzeAsync(entry, new MetadataCleanupOptions(Enabled: true), TestContext.CancellationToken);

        Assert.IsFalse(plan.HasChanges);
    }

    [TestMethod]
    public async Task ASngIsAnalysedFromItsMetadata()
    {
        var entry = await SngAsync(_root.FullName, "Packed",
            [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", ""), KeyValuePair.Create("frets", "Someone"), KeyValuePair.Create("diff_guitar", "3"), KeyValuePair.Create("diff_drums", "2")],
            ("notes.mid", Anchored()));

        var plan = await MetadataCleanup.AnalyzeAsync(entry, new MetadataCleanupOptions(Enabled: true), TestContext.CancellationToken);

        Assert.AreSequenceEqual(["name", "diff_guitar", "charter"], Keys(plan));
        Assert.IsNotNull(plan.NewMidiBytes);
        Assert.AreEqual("Someone", plan.After.Single(p => p.Key == "charter").Value);
    }

    [TestMethod]
    public async Task AnalysingWritesNothing()
    {
        var entry = Folder(_root.FullName, "S", "[song]\nname = Song\nartist =\n", ("notes.mid", Anchored()));
        string ini = await File.ReadAllTextAsync(Path.Combine(entry.FullPath, "song.ini"), TestContext.CancellationToken);
        byte[] midi = await File.ReadAllBytesAsync(Path.Combine(entry.FullPath, "notes.mid"), TestContext.CancellationToken);

        await MetadataCleanup.AnalyzeAsync(entry, new MetadataCleanupOptions(Enabled: true), TestContext.CancellationToken);

        Assert.AreEqual(ini, await File.ReadAllTextAsync(Path.Combine(entry.FullPath, "song.ini"), TestContext.CancellationToken));
        Assert.AreSequenceEqual(midi, await File.ReadAllBytesAsync(Path.Combine(entry.FullPath, "notes.mid"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AMalformedSngIsLeftAlone()
    {
        string path = Path.Combine(_root.FullName, "Bad.sng");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.CancellationToken);
        var entry = new SongEntry(SongEntryKind.SngFile, path, "Bad.sng", "Bad");

        var plan = await MetadataCleanup.AnalyzeAsync(entry, new MetadataCleanupOptions(Enabled: true), TestContext.CancellationToken);

        Assert.IsFalse(plan.HasChanges);
    }
}
