using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Scanning;

/// <summary>
/// <see cref="SongFileCommit"/>: a replace or delete waits for a reader to let go of the file, as another Chart Packer
/// does once it sees the song's lock (charter-spec §4.8). Only Windows refuses to replace or delete a file that's open.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class SongFileCommitTests
{
    private string _root = "";
    private string _target = "";
    private string _temp = "";

    [TestInitialize]
    public void CreateFiles()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-commit-").FullName;
        _target = Path.Combine(_root, "song.sng");
        _temp = _target + ".tmp";
        File.WriteAllBytes(_target, [1]);
        File.WriteAllBytes(_temp, [2]);
    }

    [TestCleanup]
    public void DeleteFiles() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task AReplaceWaitsForAReaderToLetGo()
    {
        var reader = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task letGo = Task.Delay(300, TestContext.CancellationToken).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);

        SongFileCommit.MoveOver(_temp, _target, TestContext.CancellationToken);

        await letGo;
        Assert.AreSequenceEqual(new byte[] { 2 }, File.ReadAllBytes(_target));
        Assert.IsFalse(File.Exists(_temp));
    }

    [TestMethod]
    public void AReplaceGivesUpOnAReaderThatHoldsOn()
    {
        using (new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => SongFileCommit.MoveOver(_temp, _target, TestContext.CancellationToken, TimeSpan.FromMilliseconds(300)));
        }

        Assert.AreSequenceEqual(new byte[] { 1 }, File.ReadAllBytes(_target));
        Assert.IsTrue(File.Exists(_temp), "The caller deletes its temp file.");
    }

    [TestMethod]
    public async Task ADeleteWaitsForAReaderToLetGo()
    {
        var reader = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task letGo = Task.Delay(300, TestContext.CancellationToken).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);

        SongFileCommit.Delete(_target, TestContext.CancellationToken);

        await letGo;
        Assert.IsFalse(File.Exists(_target));
    }

    [TestMethod]
    public void AMissingFileIsAlreadyDeleted()
    {
        File.Delete(_target);

        SongFileCommit.Delete(_target, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task AMoveAsideWaitsForAReaderOfWhatsMovedToLetGo()
    {
        string aside = _target + ".aside";
        var reader = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task letGo = Task.Delay(300, TestContext.CancellationToken).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);

        SongFileCommit.Move(_target, aside, TestContext.CancellationToken);

        await letGo;
        Assert.IsFalse(File.Exists(_target));
        Assert.AreSequenceEqual(new byte[] { 1 }, File.ReadAllBytes(aside));
    }

    [TestMethod]
    public async Task AnAsyncReplaceAndDeleteWaitForAReaderToLetGo()
    {
        var reader = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task letGo = Task.Delay(300, TestContext.CancellationToken).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);

        await SongFileCommit.MoveOverAsync(_temp, _target, TestContext.CancellationToken);
        await letGo;
        Assert.AreSequenceEqual(new byte[] { 2 }, File.ReadAllBytes(_target));

        reader = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read);
        letGo = Task.Delay(300, TestContext.CancellationToken).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);

        await SongFileCommit.DeleteAsync(_target, TestContext.CancellationToken);
        await letGo;
        Assert.IsFalse(File.Exists(_target));
    }

    [TestMethod]
    public void WaitingCanBeCancelled()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        using (new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<OperationCanceledException>(() => SongFileCommit.MoveOver(_temp, _target, cancel.Token));
    }

    [TestMethod]
    public async Task WaitingAsyncCanBeCancelled()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        using (new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<OperationCanceledException>(() => SongFileCommit.DeleteAsync(_target, cancel.Token));
    }

    public TestContext TestContext { get; set; } = null!;
}
