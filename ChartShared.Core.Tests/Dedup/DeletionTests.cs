using System.Text.Json;
using ChartShared.Core.Dedup;
using ChartShared.Core.Scanning;
using ChartShared.Core.Trash;
using static ChartShared.Core.Tests.Dedup.DedupFixtures;

namespace ChartShared.Core.Tests.Dedup;

[TestClass]
public sealed class DeletionPlanTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "library");

    [TestMethod]
    public void TheKeeperCantBeDeleted()
    {
        var keeper = Fingerprint(SongEntryKind.SngFile, Path.Combine(Root, "Song.sng"));

        Assert.ThrowsExactly<InvalidOperationException>(() => DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [keeper]));
    }

    [TestMethod]
    public void ACopyListedTwiceIsRejected()
    {
        var keeper = Fingerprint(SongEntryKind.SngFile, Path.Combine(Root, "Song.sng"));
        var copy = Fingerprint(SongEntryKind.SngFile, Path.Combine(Root, "Song (1).sng"));

        var items = DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy, copy]);

        Assert.ThrowsExactly<InvalidOperationException>(() => DeletionPlan.Validate(items));
    }

    [TestMethod]
    public void AKeeperFromAnotherGroupCantBeDeleted()
    {
        var a = Fingerprint(SongEntryKind.SngFile, Path.Combine(Root, "A.sng"));
        var b = Fingerprint(SongEntryKind.SngFile, Path.Combine(Root, "B.sng"));

        var items = DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(a), [b])
            .Concat(DeletionPlan.Create(DedupTier.SameChart, DeletionKeeper.Of(b), [a]))
            .ToList();

        Assert.ThrowsExactly<InvalidOperationException>(() => DeletionPlan.Validate(items));
    }

    [TestMethod]
    public void AFolderHoldingTheKeeperCantBeDeleted()
    {
        var folder = Fingerprint(SongEntryKind.SongFolder, Path.Combine(Root, "Song"));
        var merged = new DeletionKeeper(Path.Combine(Root, "Song", "Merged.sng"), SongEntryKind.SngFile, EntryStamps.ForFile(1, DateTime.UtcNow));

        var items = DeletionPlan.Create(DedupTier.MergeCandidate, merged, [folder]);

        Assert.ThrowsExactly<InvalidOperationException>(() => DeletionPlan.Validate(items));
    }

    [TestMethod]
    public void ACopyWithUnsavedEditsCantBeDeleted()
    {
        var keeper = Fingerprint(SongEntryKind.SngFile, Path.Combine(Root, "Song.sng"));
        var copy = Fingerprint(SongEntryKind.SongFolder, Path.Combine(Root, "Song"));

        var items = DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]);

        Assert.ThrowsExactly<InvalidOperationException>(() => DeletionPlan.Validate(items, path => path == copy.Entry.FullPath));
        DeletionPlan.Validate(items);
    }
}

[TestClass]
public sealed class DeletionExecutorTests
{
    private DirectoryInfo _root = null!;
    private string _logPath = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-delete-");
        _logPath = Path.Combine(_root.FullName, "log", "dedup-log.jsonl");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task ACopyIsMovedToTheTrashAndLogged()
    {
        var (keeper, copy) = await PairAsync();
        var trash = new FakeTrash(_root.CreateSubdirectory("bin"));

        var outcomes = await RunAsync(trash, DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]));

        Assert.AreEqual(DeletionStatus.Trashed, outcomes.Single().Status);
        Assert.IsFalse(File.Exists(copy.Entry.FullPath));
        Assert.IsTrue(Directory.Exists(keeper.Entry.FullPath));

        var lines = File.ReadAllLines(_logPath);
        Assert.HasCount(2, lines);
        using var outcome = JsonDocument.Parse(lines[1]);
        Assert.AreEqual("Trashed", outcome.RootElement.GetProperty("action").GetString());
        Assert.AreEqual(copy.Entry.FullPath, outcome.RootElement.GetProperty("victim").GetString());
        Assert.AreEqual(keeper.Entry.FullPath, outcome.RootElement.GetProperty("keeper").GetString());
    }

    [TestMethod]
    public async Task AMissingKeeperStopsTheDelete()
    {
        var (keeper, copy) = await PairAsync();
        Directory.Delete(keeper.Entry.FullPath, recursive: true);

        var outcome = (await RunAsync(new FakeTrash(_root.CreateSubdirectory("bin")), DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]))).Single();

        Assert.AreEqual(DeletionSkipReason.KeeperMissing, outcome.Reason);
        Assert.IsTrue(File.Exists(copy.Entry.FullPath));
    }

    [TestMethod]
    public async Task AChangedKeeperStopsTheDelete()
    {
        var (keeper, copy) = await PairAsync();
        File.AppendAllText(Path.Combine(keeper.Entry.FullPath, "notes.chart"), "\n");

        var outcome = (await RunAsync(new FakeTrash(_root.CreateSubdirectory("bin")), DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]))).Single();

        Assert.AreEqual(DeletionSkipReason.KeeperChanged, outcome.Reason);
        Assert.IsTrue(File.Exists(copy.Entry.FullPath));
    }

    [TestMethod]
    public async Task ACopyRewrittenWithTheSameSizeIsLeftAlone()
    {
        var (keeper, copy) = await PairAsync();
        File.SetLastWriteTimeUtc(copy.Entry.FullPath, DateTime.UtcNow.AddMinutes(5));

        var outcome = (await RunAsync(new FakeTrash(_root.CreateSubdirectory("bin")), DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]))).Single();

        Assert.AreEqual(DeletionSkipReason.VictimChanged, outcome.Reason);
        Assert.IsTrue(File.Exists(copy.Entry.FullPath));
    }

    [TestMethod]
    public async Task AMissingCopyIsSkipped()
    {
        var (keeper, copy) = await PairAsync();
        File.Delete(copy.Entry.FullPath);

        var outcome = (await RunAsync(new FakeTrash(_root.CreateSubdirectory("bin")), DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]))).Single();

        Assert.AreEqual(DeletionSkipReason.VictimMissing, outcome.Reason);
    }

    [TestMethod]
    public async Task AFailedTrashLeavesTheCopyInPlace()
    {
        var (keeper, copy) = await PairAsync();
        var trash = new FakeTrash(_root.CreateSubdirectory("bin")) { Result = TrashResult.Fail("No bin here.") };

        var outcome = (await RunAsync(trash, DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]))).Single();

        Assert.AreEqual(DeletionStatus.Failed, outcome.Status);
        Assert.AreEqual("No bin here.", outcome.Error);
        Assert.IsTrue(File.Exists(copy.Entry.FullPath));
    }

    [TestMethod]
    public async Task ATrashThatClaimsSuccessButLeavesTheCopyFails()
    {
        var (keeper, copy) = await PairAsync();
        var trash = new FakeTrash(_root.CreateSubdirectory("bin")) { Result = TrashResult.Trashed, Move = false };

        var outcome = (await RunAsync(trash, DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]))).Single();

        Assert.AreEqual(DeletionStatus.Failed, outcome.Status);
    }

    [TestMethod]
    public async Task DecliningWindowsWarningKeepsTheCopy()
    {
        var (keeper, copy) = await PairAsync();
        var trash = new FakeTrash(_root.CreateSubdirectory("bin")) { Result = new TrashResult(TrashStatus.Declined), Move = false };

        var outcome = (await RunAsync(trash, DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]))).Single();

        Assert.AreEqual(DeletionSkipReason.DeclinedPermanentDelete, outcome.Reason);
    }

    [TestMethod]
    public async Task EditsMadeAfterPlanningStopTheDelete()
    {
        var (keeper, copy) = await PairAsync();
        var items = DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]);
        int calls = 0;

        // Clean when the plan is checked, edited by the time the copy is reached.
        var outcomes = await DeletionExecutor.RunAsync(items, new FakeTrash(_root.CreateSubdirectory("bin")), new DeletionLog(_logPath), _ => calls++ > 0, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(DeletionSkipReason.HasUnsavedChanges, outcomes.Single().Reason);
    }

    [TestMethod]
    public async Task CancellingStopsBeforeTheNextCopy()
    {
        var (keeper, copy) = await PairAsync();
        var second = await CopyAsync(copy, "Song (2).sng");
        var items = DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy, second]);
        using var cts = new CancellationTokenSource();

        var outcomes = await DeletionExecutor.RunAsync(
            items,
            new FakeTrash(_root.CreateSubdirectory("bin")),
            new DeletionLog(_logPath),
            progress: new Inline<DeletionOutcome>(_ => cts.Cancel()),
            cancellationToken: cts.Token);

        Assert.HasCount(1, outcomes);
        Assert.IsTrue(File.Exists(second.Entry.FullPath));
    }

    [TestMethod]
    public async Task NothingIsDeletedWhenTheLogCantBeOpened()
    {
        var (keeper, copy) = await PairAsync();
        string blocker = Path.Combine(_root.FullName, "blocker");
        File.WriteAllText(blocker, "a file where the log's folder should be");

        var items = DeletionPlan.Create(DedupTier.Exact, DeletionKeeper.Of(keeper), [copy]);
        await Assert.ThrowsAsync<IOException>(() => DeletionExecutor.RunAsync(items, new FakeTrash(_root.CreateSubdirectory("bin")), new DeletionLog(Path.Combine(blocker, "log.jsonl")), cancellationToken: TestContext.CancellationToken));

        Assert.IsTrue(File.Exists(copy.Entry.FullPath));
    }

    private Task<IReadOnlyList<DeletionOutcome>> RunAsync(ITrash trash, IReadOnlyList<DeletionItem> items) =>
        DeletionExecutor.RunAsync(items, trash, new DeletionLog(_logPath), cancellationToken: TestContext.CancellationToken);

    /// <summary>A folder and the <c>.sng</c> packed from it, fingerprinted.</summary>
    private async Task<(SongFingerprint Folder, SongFingerprint Sng)> PairAsync()
    {
        var folder = Song(_root, "Song");
        var sng = await PackAsync(folder, Path.Combine(_root.FullName, "Song (1).sng"));
        var analysis = await DedupAnalyzer.AnalyzeAsync([folder, sng], cancellationToken: TestContext.CancellationToken);
        var entries = analysis.Exact.Single().Entries;
        return (entries.Single(e => e.Entry.Kind == SongEntryKind.SongFolder), entries.Single(e => e.Entry.Kind == SongEntryKind.SngFile));
    }

    private async Task<SongFingerprint> CopyAsync(SongFingerprint sng, string name)
    {
        string path = Path.Combine(_root.FullName, name);
        File.Copy(sng.Entry.FullPath, path);
        var analysis = await DedupAnalyzer.AnalyzeAsync([sng.Entry, SngEntry(path)], cancellationToken: TestContext.CancellationToken);
        return analysis.Exact.Single().Entries.Single(e => e.Entry.FullPath == path);
    }

    /// <summary>Moves items into a folder, or reports whatever it's told to.</summary>
    private sealed class FakeTrash(DirectoryInfo bin) : ITrash
    {
        public TrashResult? Result { get; init; }

        public bool Move { get; init; } = true;

        public TrashKind Kind => TrashKind.Trash;

        public TrashResult MoveToTrash(string path, bool isDirectory)
        {
            if (Result is { Status: not TrashStatus.Trashed } result)
                return result;

            if (Move)
            {
                string target = Path.Combine(bin.FullName, Guid.NewGuid().ToString("N"));
                if (isDirectory)
                    Directory.Move(path, target);
                else
                    File.Move(path, target);
            }

            return Result ?? TrashResult.Trashed;
        }
    }

    public TestContext TestContext { get; set; }
}

[TestClass]
public sealed class EntryStampsTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-stamp-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AFolderStampChangesWithAnyFileInside()
    {
        var song = Folder(_root, "Song", IniFor("A", "T"), ("stems/guitar.ogg", Bytes(10, 1)));
        var before = EntryStamps.Capture(song.FullPath, SongEntryKind.SongFolder, TestContext.CancellationToken);

        Assert.AreEqual(before, EntryStamps.Capture(song.FullPath, SongEntryKind.SongFolder, TestContext.CancellationToken));

        File.SetLastWriteTimeUtc(Path.Combine(song.FullPath, "stems", "guitar.ogg"), DateTime.UtcNow.AddMinutes(1));
        var touched = EntryStamps.Capture(song.FullPath, SongEntryKind.SongFolder, TestContext.CancellationToken);
        Assert.AreNotEqual(before, touched);

        File.WriteAllText(Path.Combine(song.FullPath, "extra.txt"), "");
        Assert.AreNotEqual(touched, EntryStamps.Capture(song.FullPath, SongEntryKind.SongFolder, TestContext.CancellationToken));
    }

    [TestMethod]
    public void JunkTheOsWritesDoesNotChangeAFolderStamp()
    {
        var song = Folder(_root, "Song", IniFor("A", "T"), ("notes.chart", Utf8(Chart)));
        var before = EntryStamps.Capture(song.FullPath, SongEntryKind.SongFolder, TestContext.CancellationToken);

        File.WriteAllText(Path.Combine(song.FullPath, "Thumbs.db"), "thumbnails");
        File.WriteAllText(Path.Combine(song.FullPath, ".DS_Store"), "view settings");
        Directory.CreateDirectory(Path.Combine(song.FullPath, "__MACOSX"));
        File.WriteAllText(Path.Combine(song.FullPath, "__MACOSX", "._notes.chart"), "resource fork");

        Assert.AreEqual(before, EntryStamps.Capture(song.FullPath, SongEntryKind.SongFolder, TestContext.CancellationToken));
    }

    [TestMethod]
    public void AMissingSongHasNoStamp()
    {
        Assert.IsNull(EntryStamps.Capture(Path.Combine(_root.FullName, "gone"), SongEntryKind.SongFolder, TestContext.CancellationToken));
        Assert.IsNull(EntryStamps.Capture(Path.Combine(_root.FullName, "gone.sng"), SongEntryKind.SngFile, TestContext.CancellationToken));
    }

    public TestContext TestContext { get; set; }
}
