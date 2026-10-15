using ChartShared.Core.Charts;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Tests.Charts;

namespace ChartShared.Core.Tests.Packing;

/// <summary>What a packed <c>.sng</c> leaves out: Chart Builder's tempo anchors, and the keys of parts without notes.</summary>
[TestClass]
public sealed class ExportCleanupTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-cleanup-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    [TestMethod]
    public void StrippingLeavesOutTheAnchorsAndKeepsTheRest()
    {
        byte[] midi = Anchored();

        byte[] stripped = ExportCleanup.StripEditorEvents(midi)!;

        var texts = TempoTexts(stripped);
        Assert.AreSequenceEqual([(0L, "Song"), (960L, "[other]")], texts);
        var (_, chunks) = MidiChartReader.ChunksOf(stripped);
        var tempos = MidiFile.ReadEvents(stripped, chunks[0]).Where(e => e.IsMeta && MidiFile.MetaOf(stripped, e) is (0x51, _, _)).Select(e => e.Tick).ToList();
        Assert.AreSequenceEqual([0L, 480L], tempos);
        var (_, original) = MidiChartReader.ChunksOf(midi);
        Assert.AreSequenceEqual(midi[original[1].Start..], stripped[chunks[1].Start..], "The other tracks are kept byte for byte.");
    }

    [TestMethod]
    public void AFileWithoutAnchorsHasNothingToStrip()
    {
        byte[] midi = new MidiBuilder().Track("Song").Tempo(0, 120).Text(0, "[other]").Track("PART GUITAR").Note(0, 96).Build();

        Assert.IsNull(ExportCleanup.StripEditorEvents(midi));
    }

    [TestMethod]
    public async Task PackingStoresNotesMidWithoutAnchorsAndLeavesTheFolderAlone()
    {
        var folder = _root.CreateSubdirectory("Song");
        byte[] midi = Anchored();
        await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "notes.mid"), midi, TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "song.ini"), "[song]\nname = Song\n", TestContext.CancellationToken);
        string sng = Path.Combine(_root.FullName, "Song.sng");

        var plan = await PackPlanner.PlanAsync(folder.FullName, cancellationToken: TestContext.CancellationToken);
        await Packer.PackAsync(plan, sng, overwrite: false, cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual([(0L, "Song"), (960L, "[other]")], TempoTexts(StoredChart(sng)));
        Assert.AreSequenceEqual(midi, await File.ReadAllBytesAsync(Path.Combine(folder.FullName, "notes.mid"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CopyingASngLeavesOutWhatPackingDoesAndLeavesTheSourceAlone()
    {
        string source = Path.Combine(_root.FullName, "Source.sng");
        string midiPath = Path.Combine(_root.FullName, "notes.mid");
        byte[] midi = Anchored();
        await File.WriteAllBytesAsync(midiPath, midi, TestContext.CancellationToken);
        await SngWriter.WriteAsync(
            source, SngMask.Generate(),
            [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("diff_guitar", "3"), KeyValuePair.Create("diff_drums", "-1")],
            [new SngSourceFile("notes.mid", midiPath, midi.Length)], overwrite: false, cancellationToken: TestContext.CancellationToken);
        byte[] before = await File.ReadAllBytesAsync(source, TestContext.CancellationToken);
        string target = Path.Combine(_root.FullName, "Copy.sng");

        await ExportCleanup.CopySngAsync(source, target, TestContext.CancellationToken);

        var copy = SngReader.Read(target, TestContext.CancellationToken);
        Assert.IsFalse(copy.HasErrors);
        Assert.AreSequenceEqual(["name", "diff_guitar"], [.. copy.Metadata.Select(p => p.Key)]);
        Assert.AreSequenceEqual([(0L, "Song"), (960L, "[other]")], TempoTexts(StoredChart(target)));
        Assert.AreSequenceEqual(before, await File.ReadAllBytesAsync(source, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ACleanSngIsCopiedAsItIs()
    {
        string source = Path.Combine(_root.FullName, "Source.sng");
        string midiPath = Path.Combine(_root.FullName, "notes.mid");
        byte[] midi = new MidiBuilder().Track("Song").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Build();
        await File.WriteAllBytesAsync(midiPath, midi, TestContext.CancellationToken);
        await SngWriter.WriteAsync(
            source, SngMask.Generate(), [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("diff_guitar", "3")],
            [new SngSourceFile("notes.mid", midiPath, midi.Length)], overwrite: false, cancellationToken: TestContext.CancellationToken);
        string target = Path.Combine(_root.FullName, "Copy.sng");

        await ExportCleanup.CopySngAsync(source, target, TestContext.CancellationToken);

        Assert.AreSequenceEqual(
            await File.ReadAllBytesAsync(source, TestContext.CancellationToken),
            await File.ReadAllBytesAsync(target, TestContext.CancellationToken));
    }

    /// <summary>A guitar chart whose tempo track has two anchors among its other events.</summary>
    private static byte[] Anchored() => new MidiBuilder()
        .Track("Song").Tempo(0, 120).Text(0, "[chartpacker_anchor 0]").Tempo(480, 140).Text(480, "[chartpacker_anchor 500000]").Text(960, "[other]")
        .Track("PART GUITAR").Note(0, 96).Note(480, 97)
        .Build();

    /// <summary>The text and track-name events of <paramref name="midi"/>'s tempo track, at their ticks.</summary>
    private static List<(long Tick, string Text)> TempoTexts(byte[] midi)
    {
        var (_, chunks) = MidiChartReader.ChunksOf(midi);
        return [.. MidiFile.ReadEvents(midi, chunks[0])
            .Select(e => (e.Tick, Text: MidiFile.TextOf(midi, e, 0x01) ?? MidiFile.TextOf(midi, e, 0x03)))
            .Where(e => e.Text is not null)
            .Select(e => (e.Tick, e.Text!))];
    }

    private byte[] StoredChart(string sng)
    {
        var read = SngReader.Read(sng, TestContext.CancellationToken);
        var entry = read.Files.Single(f => f.Name == "notes.mid");
        return SongFile.ReadFromSng(sng, read.Header!, entry, ChartReader.MaxBytes, TestContext.CancellationToken)!;
    }
}
