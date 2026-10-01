using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class SongFileGateTests
{
    private const string Song = @"C:\Songs\Song";
    private const string Other = @"C:\Songs\Other";

    // Long enough that a turn that should wait would have been given by then.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    [TestMethod]
    public async Task AWriteWaitsForAReadAlreadyRunning()
    {
        var gate = new SongFileGate();
        var read = await gate.ReadAsync([Song]);

        var write = gate.WriteAsync([Song]);
        await AssertWaitingAsync(write);

        read.Dispose();
        (await write).Dispose();
    }

    [TestMethod]
    public async Task AReadWaitsForAWriteRunning()
    {
        var gate = new SongFileGate();
        var write = await gate.WriteAsync([Song]);

        var read = gate.ReadAsync([Song]);
        await AssertWaitingAsync(read);

        write.Dispose();
        (await read).Dispose();
    }

    [TestMethod]
    public async Task ReadsOfTheSameSongRunSideBySide()
    {
        var gate = new SongFileGate();
        using var first = await gate.ReadAsync([Song]);

        var second = gate.ReadAsync([Song]);

        Assert.IsTrue(second.IsCompleted);
        (await second).Dispose();
    }

    [TestMethod]
    public async Task DifferentSongsDontWaitForEachOther()
    {
        var gate = new SongFileGate();
        using var write = await gate.WriteAsync([Song]);

        var other = gate.WriteAsync([Other]);

        Assert.IsTrue(other.IsCompleted);
        (await other).Dispose();
    }

    [TestMethod]
    public async Task AWriteWaitsIfAnyOfItsSongsIsBusy()
    {
        var gate = new SongFileGate();
        var read = await gate.ReadAsync([Other]);

        var write = gate.WriteAsync([Song, Other]);
        await AssertWaitingAsync(write);

        read.Dispose();
        (await write).Dispose();
    }

    [TestMethod]
    public async Task AReadAskedForAfterAWaitingWriteWaitsForIt()
    {
        var gate = new SongFileGate();
        var first = await gate.ReadAsync([Song]);
        var write = gate.WriteAsync([Song]);

        var later = gate.ReadAsync([Song]);
        await AssertWaitingAsync(later);

        first.Dispose();
        var writing = await write;
        await AssertWaitingAsync(later);

        writing.Dispose();
        (await later).Dispose();
    }

    [TestMethod]
    public async Task NoSongsIsGivenATurnAtOnce()
    {
        var gate = new SongFileGate();
        using var write = await gate.WriteAsync([Song]);

        var none = gate.WriteAsync([]);

        Assert.IsTrue(none.IsCompleted);
        (await none).Dispose();
    }

    [TestMethod]
    public async Task DisposingTwiceReleasesOnce()
    {
        var gate = new SongFileGate();
        var read = await gate.ReadAsync([Song]);
        read.Dispose();
        read.Dispose();

        using var write = await gate.WriteAsync([Song]).WaitAsync(TimeSpan.FromSeconds(5));
        var next = gate.ReadAsync([Song]);

        await AssertWaitingAsync(next);
        write.Dispose();
        (await next).Dispose();
    }

    private static async Task AssertWaitingAsync(Task<IDisposable> turn)
    {
        await Task.Delay(Settle);
        Assert.IsFalse(turn.IsCompleted, "The turn was given while the song was busy.");
    }
}
