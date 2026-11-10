using BenchmarkDotNet.Attributes;
using ChartShared.Core.Charting.Playtest;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;

namespace PlaybackBench;

/// <summary>
/// What the playtest layer reads each frame of a lane played: a run's marks and strays in the stretch on screen. The Old
/// benchmarks are what the layer did before (an interface enumerator over every judged note), the New ones what it does now.
/// </summary>
[MemoryDiagnoser]
public class PlaytestMarksBenchmarks
{
    private const double TicksPerSecond = 960;
    private static readonly InstrumentAction[] Frets = [InstrumentAction.Green, InstrumentAction.Red, InstrumentAction.Yellow, InstrumentAction.Blue, InstrumentAction.Orange];

    private PlaytestRun _run = null!;
    private long _from;
    private long _to;

    /// <summary>How many notes the song has: a few thousand for a long chart.</summary>
    [Params(2000, 6000)]
    public int Notes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var notes = new List<ChartNote>();
        for (int i = 0; i < Notes; i++)
            notes.Add(new ChartNote((long)Math.Round((1 + (i * 0.1)) * TicksPerSecond), 0, i % 5, NoteFlags.None));

        var chart = PlaytestChart.Frets(new NoteTrack(notes, []), static t => t / TicksPerSecond, static s => s * TicksPerSecond);
        _run = new PlaytestRun(new FiveFretEngine(chart, new HitWindow(), new FretRules()));
        _run.Start(0, 1, static _ => false, newPass: true);

        // Plays every fifth note wrong (a strum beside it, so it's missed and overstrummed) and hits the rest.
        for (int i = 0; i < Notes; i++)
        {
            double t = 1 + (i * 0.1);
            if (i % 5 == 4)
            {
                _run.Handle(Action(InstrumentAction.StrumDown, true, t + 0.05));
                continue;
            }

            _run.Handle(Action(Frets[i % 5], true, t - 0.01));
            _run.Handle(Action(InstrumentAction.StrumDown, true, t));
            _run.Handle(Action(Frets[i % 5], false, t + 0.03));
        }

        _run.Advance((Notes * 0.1) + 5, 1);

        // Three seconds on screen, around the middle of the song.
        _from = (long)Math.Round((1 + (Notes * 0.05)) * TicksPerSecond);
        _to = _from + (long)(3 * TicksPerSecond);
    }

    private static TimedAction Action(InstrumentAction action, bool down, double song) =>
        new(new ActionEvent(action, down, song, InputDeviceId.Keyboard), song, 1);

    /// <summary>The loop the layer had: every mark, the hits skipped.</summary>
    [Benchmark(Baseline = true)]
    public long OldMarks()
    {
        long seen = 0;
        foreach (var ((tick, lane), mark) in _run.Marks)
        {
            if (mark.Kind == JudgeKind.Hit || tick < _from - 1 || tick > _to + 1)
                continue;
            seen += tick + lane;
        }

        return seen;
    }

    /// <summary>The loop the layer had over the overstrums and overhits.</summary>
    [Benchmark]
    public double OldStrays()
    {
        double seen = 0;
        foreach (var stray in _run.Strays)
            seen += stray.Song;
        return seen;
    }

#if AFTER
    [Benchmark]
    public long NewMarks()
    {
        long seen = 0;
        foreach (var (tick, lane, _) in _run.FlaggedBetween(_from - 1, _to + 1))
            seen += tick + lane;
        return seen;
    }

    [Benchmark]
    public double NewStrays()
    {
        double seen = 0;
        foreach (var stray in _run.StraySpan)
            seen += stray.Song;
        return seen;
    }
#endif
}
