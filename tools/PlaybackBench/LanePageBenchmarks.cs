using BenchmarkDotNet.Attributes;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace PlaybackBench;

/// <summary>The check each lane's canvas makes every frame that its drawn page still serves.</summary>
[MemoryDiagnoser]
public class LanePageBenchmarks
{
    private LanePage _page = null!;
    private LaneProjection _now = null!;

    [GlobalSetup]
    public void Setup()
    {
        var slots = LaneSlots.Of(ChartInstrumentKind.FiveFret, LaneView.Highway);
        var screen = LaneProjection.AtCursor(LaneView.Highway, 0, 800, 40, 60, slots, 10, 0.13, 2);
        _page = LanePage.Of(screen, 1, 1);
        _now = LaneProjection.AtCursor(LaneView.Highway, 0, 800, 40, 60, slots, 10.5, 0.13, 2);
    }

    [Benchmark]
    public bool Fits() => _page.Fits(_now, 8);
}
