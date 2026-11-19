using ChartShared.Audio.Waveforms;

namespace ChartShared.Core.Tests.Audio;

/// <summary>How peaks are kept: at half precision, in chunks, with silent ones taking no memory.</summary>
[TestClass]
public sealed class HalfChunksTests
{
    // Chunks of 16 values, to see them fill.
    private const int Shift = 4;

    [TestMethod]
    public void NothingPutOrOnlySilencePutTakesNoChunk()
    {
        var values = new HalfChunks(100, Shift);
        long empty = values.SizeBytes;

        for (int i = 0; i < 100; i++)
            values.Set(i, 0f);

        Assert.AreEqual(empty, values.SizeBytes);
        Assert.AreEqual(7, values.ChunkCount);
        Assert.IsTrue(Enumerable.Range(0, 100).All(i => values[i] == Half.Zero));
        Assert.IsTrue(Enumerable.Range(0, 7).All(c => values.ChunkAt(c) is null));
    }

    [TestMethod]
    public void ASoundMakesOneChunk()
    {
        var values = new HalfChunks(100, Shift);
        long empty = values.SizeBytes;

        values.Set(37, 0.5f);

        Assert.AreEqual(empty + (16 * 2), values.SizeBytes);
        Assert.AreEqual((Half)0.5f, values[37]);
        Assert.AreEqual(Half.Zero, values[36]);
        Assert.IsNotNull(values.ChunkAt(2));
        Assert.IsNull(values.ChunkAt(1));
    }

    [TestMethod]
    public void ValuesPutFromSeveralThreadsAtOnceAreAllKept()
    {
        var values = new HalfChunks(64 * 1024, Shift);

        Parallel.For(0, 64 * 1024, i => values.Set(i, (float)((i % 7) + 1) / 8));

        for (int i = 0; i < 64 * 1024; i++)
            Assert.AreEqual((Half)((float)((i % 7) + 1) / 8), values[i], $"Value {i}.");
    }

    [TestMethod]
    public void StretchesComeAChunkAtATimeAndSilentOnesAreEmpty()
    {
        var values = new HalfChunks(64, Shift);
        values.Set(5, 0.25f);
        values.Set(40, 0.75f);

        var stretches = new List<(int Start, int Length, bool IsSilent)>();
        foreach (var span in values.Spans(3, 45))
            stretches.Add((span.Start, span.Length, span.IsSilent));

        Assert.AreSequenceEqual([(3, 13, false), (16, 16, true), (32, 13, false)], stretches);
    }

    [TestMethod]
    public void TrimmedItKeepsOnlyWhatsLeftAndExtendedItGoesOn()
    {
        var values = new HalfChunks(40, Shift);
        for (int i = 0; i < 40; i++)
            values.Set(i, 0.5f);

        var trimmed = values.Trimmed(20);
        var extended = trimmed.Extended(20, [(Half)0.25f, (Half)0.25f]);

        Assert.AreEqual(20, trimmed.Count);
        Assert.AreEqual(22, extended.Count);
        Assert.AreEqual((Half)0.5f, extended[19]);
        Assert.AreEqual((Half)0.25f, extended[21]);

        // What's past the end of the trimmed values is silent, as a chunk shared with what it was trimmed from isn't changed.
        Assert.AreEqual((Half)0.5f, values[21]);
        Assert.IsTrue(trimmed.Spans(16, 20).GetEnumerator().MoveNext());
    }
}
