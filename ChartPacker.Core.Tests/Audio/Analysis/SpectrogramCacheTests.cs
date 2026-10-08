using ChartPacker.Audio.Analysis;

namespace ChartPacker.Core.Tests.Audio.Analysis;

/// <summary>AC C13.2: the spectrograms stay within their memory budget.</summary>
[TestClass]
public sealed class SpectrogramCacheTests
{
    // 10 s at 8 kHz: 313 columns of 256 bytes, about 80 KB.
    private const int Rate = 8000;
    private static readonly long Size = ((10 * Rate / Spectrogram.Hop) + 1) * Spectrogram.Rows;

    [TestMethod]
    public void TheLeastRecentlyUsedIsReleasedToStayWithinTheBudget()
    {
        using var workers = new AnalysisWorkers(1);
        using var cache = new SpectrogramCache(workers, (Size * 2) + 100);

        var a = Start(cache, "a");
        var b = Start(cache, "b");
        Assert.IsTrue(cache.TryGet(Key("a"), out _), "Touching a leaves b the least recently used.");
        var c = Start(cache, "c");

        Assert.IsFalse(a.IsReleased);
        Assert.IsTrue(b.IsReleased);
        Assert.IsFalse(c.IsReleased);
        Assert.IsFalse(cache.TryGet(Key("b"), out _));
        Assert.IsLessThanOrEqualTo(cache.BudgetBytes, cache.SizeBytes);
    }

    [TestMethod]
    public void TheSameAudioIsntComputedTwice()
    {
        using var workers = new AnalysisWorkers(1);
        using var cache = new SpectrogramCache(workers);
        int made = 0;

        var first = cache.GetOrStart(Key("a"), () => { made++; return Input(); }, 0).Spectrogram;
        var second = cache.GetOrStart(Key("a"), () => { made++; return Input(); }, 5).Spectrogram;

        Assert.AreSame(first, second);
        Assert.AreEqual(1, made);
    }

    [TestMethod]
    public void LoweringTheBudgetReleasesAtOnce()
    {
        using var workers = new AnalysisWorkers(1);
        using var cache = new SpectrogramCache(workers, Size * 3);
        var a = Start(cache, "a");
        var b = Start(cache, "b");

        cache.BudgetBytes = Size + 100;

        Assert.IsTrue(a.IsReleased);
        Assert.IsFalse(b.IsReleased);
        Assert.AreEqual(Size, cache.SizeBytes);
    }

    [TestMethod]
    public void OneTooBigForTheWholeBudgetIsRefused()
    {
        using var workers = new AnalysisWorkers(1);
        using var cache = new SpectrogramCache(workers, Size / 2);

        var result = cache.GetOrStart(Key("a"), Input, 0);

        Assert.IsTrue(result.IsOverBudget);
        Assert.AreEqual(Size, result.NeededBytes);
        Assert.AreEqual(0, cache.SizeBytes);
    }

    [TestMethod]
    public void SpectrogramsInUseArentReleasedToMakeRoomForAnother()
    {
        // Two lanes whose spectrograms don't both fit mustn't take turns releasing each other, frame after frame.
        using var workers = new AnalysisWorkers(1);
        using var cache = new SpectrogramCache(workers, (Size * 2) + 100) { KeepInUse = TimeSpan.FromMinutes(1) };
        var a = Start(cache, "a");
        var b = Start(cache, "b");

        var result = cache.GetOrStart(Key("c"), Input, 0);

        Assert.IsTrue(result.IsOverBudget);
        Assert.IsTrue(result.IsCrowded);
        Assert.IsFalse(a.IsReleased || b.IsReleased);
    }

    [TestMethod]
    public void ClearingReleasesEverything()
    {
        using var workers = new AnalysisWorkers(1);
        using var cache = new SpectrogramCache(workers);
        var a = Start(cache, "a");
        var b = Start(cache, "b");

        cache.Clear();

        Assert.IsTrue(a.IsReleased && b.IsReleased);
        Assert.AreEqual(0, cache.SizeBytes);
    }

    private static Spectrogram Start(SpectrogramCache cache, string name) => cache.GetOrStart(Key(name), Input, 0).Spectrogram!;

    private static AnalysisKey Key(string name) => new(name);

    private static AnalysisInput Input() => AnalysisSignals.Input(new float[10 * Rate], Rate);
}
