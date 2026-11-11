using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Tests.Charts;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>
/// <see cref="OptimizeJob"/> on temp folders and temp <c>.sng</c>s, with fake optimizers: the in-place swap and the
/// output folder, the temp files left after success, cancel and failure, locks, drafts and failures.
/// </summary>
[TestClass]
public sealed class OptimizeJobTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-opt-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private static byte[] Anchored() => new MidiBuilder()
        .Track("Song").Tempo(0, 120).Text(0, "[chartpacker_anchor 0]").Text(960, "[other]")
        .Track("PART GUITAR").Note(0, 96)
        .Build();

    private const string Ini = "; made by hand\n[song]\nname   = Song\nartist =\nfrets  = Someone\n";

    private static (string, byte[])[] Files() =>
    [
        ("guitar.ogg", Bytes(1000, 1)), ("album.png", Bytes(800, 2)), ("video.mp4", Bytes(5000, 3)),
        ("highway.png", Bytes(300, 4)), ("notes.mid", Anchored()),
    ];

    private static IReadOnlyList<KeyValuePair<string, string>> SngMetadata() =>
        [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", ""), KeyValuePair.Create("frets", "Someone")];

    private async Task<SongEntry> FolderChartAsync(string name = "Folder") => Folder(_root.FullName, name, Ini, Files());

    private Task<SongEntry> SngChartAsync(string name = "Packed") => SngAsync(_root.FullName, name, SngMetadata(), Files());

    private async Task<ChartPlan> PlanAsync(SongEntry entry, OptimizeOptions options, FakeOptimizer optimizer, bool stamp = true)
    {
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        if (!stamp)
            chart = chart with { Stamp = null };
        return await OptimizationPlanner.PlanAsync(chart, options, new OptimizerSet(optimizer, optimizer, optimizer), TestContext.CancellationToken);
    }

    private static OptimizerSet SetOf(FakeOptimizer optimizer) => new(optimizer, optimizer, optimizer);

    private async Task<OptimizeJobResult> RunAsync(IReadOnlyList<ChartPlan> plans, OptimizeOptions options, FakeOptimizer optimizer,
        OptimizeJobServices? services = null, IProgress<OptimizeProgress>? progress = null, CancellationToken cancellationToken = default) =>
        await OptimizeJob.RunAsync(plans, options, SetOf(optimizer), services, progress, cancellationToken == default ? TestContext.CancellationToken : cancellationToken);

    [TestMethod]
    public async Task AFolderIsSwappedInPlace()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plan = await PlanAsync(entry, options, optimizer);
        string ini = Path.Combine(entry.FullPath, "song.ini");
        long before = Directory.EnumerateFiles(entry.FullPath).Sum(f => new FileInfo(f).Length);

        var result = await RunAsync([plan], options, optimizer);

        var item = result.Items.Single();
        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["album.png", "guitar.opus", "highway.png", "notes.mid", "song.ini", "video.webm"], Names(entry));
        Assert.AreEqual(500, (await ReadAsync(entry, "guitar.opus"))!.Length);
        Assert.AreEqual(400, (await ReadAsync(entry, "album.png"))!.Length);
        Assert.AreEqual(2500, (await ReadAsync(entry, "video.webm"))!.Length);
        Assert.AreSequenceEqual(Bytes(300, 4), await ReadAsync(entry, "highway.png"), "highway.* is left alone.");
        Assert.IsTrue((await ReadAsync(entry, "notes.mid"))!.Length < Anchored().Length, "The editor events are gone from notes.mid.");
        string text = (await File.ReadAllTextAsync(ini, TestContext.CancellationToken)).ReplaceLineEndings("\n");
        StringAssert.Contains(text, "; made by hand\n[song]\nname   = Song\n", "song.ini keeps its comment and spacing.");
        StringAssert.Contains(text, "charter");
        Assert.IsFalse(text.Contains("artist") || text.Contains("frets"));
        Assert.AreEqual(before, item.BytesBefore);
        Assert.AreEqual(Directory.EnumerateFiles(entry.FullPath).Sum(f => new FileInfo(f).Length), item.BytesAfter);
        Assert.IsTrue(item.BytesSaved > 0);
        Assert.AreEqual(entry.FullPath, item.OutputPath);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task ASngIsSwappedInPlace()
    {
        var entry = await SngChartAsync();
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plan = await PlanAsync(entry, options, optimizer);
        long before = new FileInfo(entry.FullPath).Length;

        var result = await RunAsync([plan], options, optimizer);

        var item = result.Items.Single();
        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["album.png", "guitar.opus", "highway.png", "notes.mid", "video.webm"], Names(entry));
        Assert.AreEqual(500, (await ReadAsync(entry, "guitar.opus"))!.Length);
        Assert.AreSequenceEqual(Bytes(300, 4), await ReadAsync(entry, "highway.png"));
        Assert.IsTrue((await ReadAsync(entry, "notes.mid"))!.Length < Anchored().Length);
        var read = await SngReader.ReadAsync(entry.FullPath, TestContext.CancellationToken);
        Assert.IsFalse(read.HasErrors);
        Assert.AreSequenceEqual(["name", "charter"], read.Metadata.Select(p => p.Key));
        Assert.AreEqual(before, item.BytesBefore);
        Assert.AreEqual(new FileInfo(entry.FullPath).Length, item.BytesAfter);
        Assert.IsTrue(item.BytesAfter < item.BytesBefore);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task ResultsCarryBytesBeforeAndAfterPerAsset()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer();
        var options = AllOn();

        var result = await RunAsync([await PlanAsync(entry, options, optimizer)], options, optimizer);

        var assets = result.Items.Single().Assets;
        var guitar = assets.Single(a => a.StoredName == "guitar.ogg");
        Assert.AreEqual(AssetOutcome.Applied, guitar.Outcome);
        Assert.AreEqual(OptimizeStrategy.Audio, guitar.Strategy);
        Assert.AreEqual(1000, guitar.BytesBefore);
        Assert.AreEqual(500, guitar.BytesAfter);
        Assert.AreEqual("guitar.opus", guitar.OutputName);
        var video = assets.Single(a => a.StoredName == "video.mp4");
        Assert.AreEqual(5000, video.BytesBefore);
        Assert.AreEqual(2500, video.BytesAfter);
        Assert.AreEqual(OptimizeStrategy.Metadata, assets.Single(a => a.Strategy == OptimizeStrategy.Metadata).Strategy);
    }

    [TestMethod]
    public async Task AnOutputFolderGetsTheOptimizedChartsAndTheOriginalsAreKept()
    {
        var folder = await FolderChartAsync();
        var sng = await SngChartAsync();
        var optimizer = new FakeOptimizer();
        string output = Path.Combine(_root.FullName, "Out");
        var options = AllOn(outputFolder: output);
        var plans = new[] { await PlanAsync(folder, options, optimizer), await PlanAsync(sng, options, optimizer) };
        byte[] sngBefore = await File.ReadAllBytesAsync(sng.FullPath, TestContext.CancellationToken);
        byte[] iniBefore = await File.ReadAllBytesAsync(Path.Combine(folder.FullPath, "song.ini"), TestContext.CancellationToken);

        var result = await RunAsync(plans, options, optimizer);

        Assert.AreEqual(2, result.Succeeded, string.Join("; ", result.Items.Select(i => i.ErrorMessage)));
        Assert.AreSequenceEqual(sngBefore, await File.ReadAllBytesAsync(sng.FullPath, TestContext.CancellationToken));
        Assert.AreSequenceEqual(iniBefore, await File.ReadAllBytesAsync(Path.Combine(folder.FullPath, "song.ini"), TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(Path.Combine(folder.FullPath, "guitar.ogg")));
        var folderCopy = new SongEntry(SongEntryKind.SongFolder, Path.Combine(output, "Folder"), "Folder", "Folder");
        var sngCopy = new SongEntry(SongEntryKind.SngFile, Path.Combine(output, "Packed.sng"), "Packed.sng", "Packed");
        Assert.AreEqual(500, (await ReadAsync(folderCopy, "guitar.opus"))!.Length);
        Assert.AreEqual(500, (await ReadAsync(sngCopy, "guitar.opus"))!.Length);
        Assert.AreEqual(folderCopy.FullPath, result.Items[0].OutputPath);
        Assert.AreEqual(sngCopy.FullPath, result.Items[1].OutputPath);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);

        var again = await RunAsync(plans, options, optimizer);
        Assert.IsTrue(again.Items.All(i => i.SkipReason == OptimizeItemSkipReason.OutputExists), "An existing output is never overwritten.");
    }

    [TestMethod]
    public async Task StemsAreMixedDownInPlace()
    {
        var entry = await SngAsync(_root.FullName, "Stems", SngMetadata(), ("guitar.ogg", Bytes(1000, 1)), ("bass.ogg", Bytes(1000, 2)), ("preview.ogg", Bytes(400, 3)));
        var optimizer = new FakeOptimizer();
        var options = Only(OptimizeStrategy.StemMixdown) with { StemMixdown = new AudioOptimizeOptions(Enabled: true) };

        var result = await RunAsync([await PlanAsync(entry, options, optimizer)], options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, result.Items[0].Status, result.Items[0].ErrorMessage);
        Assert.AreSequenceEqual(["preview.ogg", "song.opus"], Names(entry));
        Assert.AreEqual(1000, (await ReadAsync(entry, "song.opus"))!.Length);
        Assert.AreEqual(1, optimizer.Optimized.Count);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AFileCanBeRemoved()
    {
        var entry = await FolderChartAsync();
        var options = Only(OptimizeStrategy.Video) with { Video = new VideoOptimizeOptions(Enabled: true, Remove: true) };
        var optimizer = new FakeOptimizer();

        var result = await RunAsync([await PlanAsync(entry, options, optimizer)], options, optimizer);

        Assert.IsFalse(File.Exists(Path.Combine(entry.FullPath, "video.mp4")));
        Assert.AreEqual(0, optimizer.Optimized.Count);
        Assert.AreEqual(5000, result.Items[0].BytesSaved);
    }

    [TestMethod]
    public async Task MetadataCleanupAloneEditsSongIniAndLeavesTheOtherFiles()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer();
        var options = Only(OptimizeStrategy.Metadata);

        var result = await RunAsync([await PlanAsync(entry, options, optimizer)], options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, result.Items[0].Status, result.Items[0].ErrorMessage);
        Assert.AreSequenceEqual(["album.png", "guitar.ogg", "highway.png", "notes.mid", "song.ini", "video.mp4"], Names(entry));
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(entry.FullPath, "song.ini"), TestContext.CancellationToken)).Contains("artist"));
        Assert.AreEqual(0, optimizer.Estimated.Count);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellingLeavesTheChartAndNoTempFiles(bool sng)
    {
        var entry = sng ? await SngChartAsync() : await FolderChartAsync();
        var other = sng ? await SngChartAsync("Other") : await FolderChartAsync("Other");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plans = new[] { await PlanAsync(entry, options, optimizer), await PlanAsync(other, options, optimizer) };
        optimizer.Estimated.Clear();
        optimizer.OnOptimize = (_, _) =>
        {
            // The first file written is the one the cancel arrives during, after something is already staged.
            if (optimizer.Optimized.Count == 2)
                cts.Cancel();
        };
        byte[] before = sng ? await File.ReadAllBytesAsync(entry.FullPath, TestContext.CancellationToken) : [];
        var names = Names(entry);
        var progress = new SyncProgress<OptimizeProgress>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => OptimizeJob.RunAsync(plans, options, SetOf(optimizer), null, progress, cts.Token));

        Assert.AreSequenceEqual(names, Names(entry), "A cancelled chart is as it was.");
        if (sng)
            Assert.AreSequenceEqual(before, await File.ReadAllBytesAsync(entry.FullPath, TestContext.CancellationToken));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
        Assert.IsFalse(progress.Reports.Any(r => r.Completed is not null), "No chart finished.");
        Assert.AreSequenceEqual(Names(other), names.Select(n => n), "The second chart wasn't started.");
    }

    [TestMethod]
    public async Task CancellingBetweenChartsKeepsTheFinishedOnes()
    {
        var first = await FolderChartAsync("First");
        var second = await FolderChartAsync("Second");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plans = new[] { await PlanAsync(first, options, optimizer), await PlanAsync(second, options, optimizer) };
        var progress = new CancelAfterFirst(cts);

        await Assert.ThrowsAsync<OperationCanceledException>(() => OptimizeJob.RunAsync(plans, options, SetOf(optimizer), null, progress, cts.Token));

        Assert.IsTrue(File.Exists(Path.Combine(first.FullPath, "guitar.opus")), "The finished chart stays optimized.");
        Assert.IsTrue(File.Exists(Path.Combine(second.FullPath, "guitar.ogg")));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    private sealed class CancelAfterFirst(CancellationTokenSource cts) : IProgress<OptimizeProgress>
    {
        public void Report(OptimizeProgress value)
        {
            if (value.Completed is not null)
                cts.Cancel();
        }
    }

    [TestMethod]
    public async Task AFailingFileIsReportedAndTheChartsOtherFilesCarryOn()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { FailWhen = files => files[0].Asset.Stem == "guitar" };
        var options = AllOn();

        var result = await RunAsync([await PlanAsync(entry, options, optimizer)], options, optimizer);

        var item = result.Items.Single();
        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        var guitar = item.Assets.Single(a => a.StoredName == "guitar.ogg");
        Assert.AreEqual(AssetOutcome.Failed, guitar.Outcome);
        Assert.AreEqual("The optimizer failed.", guitar.Detail);
        Assert.IsTrue(File.Exists(Path.Combine(entry.FullPath, "guitar.ogg")));
        Assert.IsFalse(File.Exists(Path.Combine(entry.FullPath, "guitar.opus")));
        Assert.IsTrue(File.Exists(Path.Combine(entry.FullPath, "video.webm")));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AFailingChartIsReportedAndDoesNotStopTheRun()
    {
        var broken = await SngChartAsync("Broken");
        var good = await FolderChartAsync("Good");
        var optimizer = new FakeOptimizer();
        var options = Only(OptimizeStrategy.Metadata);
        // Not stamped, so it's reached; then it's no longer a readable .sng.
        var plans = new[] { await PlanAsync(broken, options, optimizer, stamp: false), await PlanAsync(good, options, optimizer) };
        await File.WriteAllBytesAsync(broken.FullPath, Bytes(64, 9), TestContext.CancellationToken);

        var result = await RunAsync(plans, options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Failed, result.Items[0].Status);
        Assert.IsFalse(string.IsNullOrEmpty(result.Items[0].ErrorMessage));
        Assert.AreEqual(OptimizeItemStatus.Succeeded, result.Items[1].Status);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AResultNoSmallerThanItsSourceIsNotKept()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { WrittenLength = files => files.Sum(f => f.Asset.Length) + 10 };
        var options = Only(OptimizeStrategy.Audio);

        var result = await RunAsync([await PlanAsync(entry, options, optimizer)], options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Skipped, result.Items[0].Status);
        Assert.AreEqual(OptimizeSkipReason.WouldGrow.ToString(), result.Items[0].Assets.Single().Detail);
        Assert.IsTrue(File.Exists(Path.Combine(entry.FullPath, "guitar.ogg")));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AChartLockedElsewhereIsSkippedAndTheOthersRun()
    {
        var locked = await FolderChartAsync("Locked");
        var free = await FolderChartAsync("Free");
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plans = new[] { await PlanAsync(locked, options, optimizer), await PlanAsync(free, options, optimizer) };
        var set = new SongLockSet(SongLockHolder.ChartPacker);
        string[] before = [.. Names(locked)];

        OptimizeJobResult result;
        using (SongLock.TryAcquire(locked.FullPath, SongLockHolder.ChartBuilder) ?? throw new AssertFailedException("The lock is free."))
            result = await RunAsync(plans, options, optimizer, new OptimizeJobServices(Locks: set));

        Assert.AreEqual(OptimizeItemStatus.Skipped, result.Items[0].Status);
        Assert.AreEqual(OptimizeItemSkipReason.InUseElsewhere, result.Items[0].SkipReason);
        Assert.AreSequenceEqual(before, Names(locked));
        Assert.AreEqual(OptimizeItemStatus.Succeeded, result.Items[1].Status);
        Assert.IsFalse(set.Holds(free.FullPath), "A chart is let go once it's done.");
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AChartWithUnsavedChangesIsSkipped()
    {
        var withDraft = await FolderChartAsync("Draft");
        var plain = await FolderChartAsync("Plain");
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plans = new[] { await PlanAsync(withDraft, options, optimizer), await PlanAsync(plain, options, optimizer) };

        var result = await RunAsync(plans, options, optimizer, new OptimizeJobServices(HasDrafts: path => path == withDraft.FullPath));

        Assert.AreEqual(OptimizeItemSkipReason.HasUnsavedChanges, result.Items[0].SkipReason);
        Assert.IsTrue(File.Exists(Path.Combine(withDraft.FullPath, "guitar.ogg")));
        Assert.AreEqual(OptimizeItemStatus.Succeeded, result.Items[1].Status);
    }

    [TestMethod]
    public async Task FilesTheAppHoldsOpenAreReleasedWhileTheChartIsHeld()
    {
        var worked = await FolderChartAsync("Worked");
        var idle = await FolderChartAsync("Idle");
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var idlePlan = await OptimizationPlanner.PlanAsync(
            await AssetInspector.InspectAsync(idle, cancellationToken: TestContext.CancellationToken), OptimizeOptions.Default, OptimizerSet.None, TestContext.CancellationToken);
        var plans = new[] { await PlanAsync(worked, options, optimizer), idlePlan };
        var set = new SongLockSet(SongLockHolder.ChartPacker);
        var released = new List<(string Path, bool Held)>();

        var result = await RunAsync(plans, options, optimizer, new OptimizeJobServices(Locks: set, ReleaseFiles: (entry, _) =>
        {
            released.Add((entry.FullPath, set.Holds(entry.FullPath)));
            return Task.CompletedTask;
        }));

        Assert.AreEqual(1, released.Count, "A chart with nothing to do isn't disturbed.");
        Assert.AreEqual((worked.FullPath, true), released[0]);
        Assert.AreEqual(OptimizeItemSkipReason.NothingToDo, result.Items[1].SkipReason);
    }

    [TestMethod]
    public async Task AChartThatChangedSinceItWasPlannedIsSkipped()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plan = await PlanAsync(entry, options, optimizer);
        await File.WriteAllBytesAsync(Path.Combine(entry.FullPath, "new.txt"), [1], TestContext.CancellationToken);

        var result = await RunAsync([plan], options, optimizer);

        Assert.AreEqual(OptimizeItemSkipReason.ChartChanged, result.Items[0].SkipReason);
        Assert.IsTrue(File.Exists(Path.Combine(entry.FullPath, "guitar.ogg")));
        Assert.AreEqual(0, optimizer.Optimized.Count);
    }

    [TestMethod]
    public async Task ASngAnotherReaderHasOpenIsStillSwapped()
    {
        var entry = await SngChartAsync();
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plan = await PlanAsync(entry, options, optimizer);

        // How the inspector holds a song's audio: open for reading, with the delete shared.
        using (new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var result = await RunAsync([plan], options, optimizer);

            Assert.AreEqual(OptimizeItemStatus.Succeeded, result.Items[0].Status, result.Items[0].ErrorMessage);
        }

        Assert.AreEqual(500, (await ReadAsync(entry, "guitar.opus"))!.Length);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task ProgressReportsEachChartStartAndFinish()
    {
        var a = await FolderChartAsync("A");
        var b = await SngChartAsync("B");
        var optimizer = new FakeOptimizer();
        var options = AllOn();
        var plans = new[] { await PlanAsync(a, options, optimizer), await PlanAsync(b, options, optimizer) };
        var progress = new SyncProgress<OptimizeProgress>();

        var result = await RunAsync(plans, options, optimizer, progress: progress);

        Assert.AreEqual(2, progress.Reports.Count(r => r.Completed is not null));
        Assert.AreSequenceEqual(result.Items, progress.Reports.Where(r => r.Completed is not null).Select(r => r.Completed!));
        Assert.AreEqual(2, progress.Reports.Last().Done);
        Assert.IsTrue(progress.Reports.All(r => r.Total == 2));
        Assert.IsTrue(progress.Reports.Any(r => r.CurrentAsset == "guitar.ogg"));
        Assert.IsTrue(progress.Reports.Any(r => r.CurrentAsset == "guitar.ogg" && r.FileFraction == 1), "An optimizer's own progress is passed on.");
        Assert.AreEqual(result.Items.Sum(i => i.BytesSaved), result.BytesSaved);
    }
}
