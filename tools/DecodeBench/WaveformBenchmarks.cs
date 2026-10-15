using BenchmarkDotNet.Attributes;
using ChartShared.Audio.Decoding;
using ChartShared.Audio.Waveforms;

namespace DecodeBench;

/// <summary>
/// What the waveform's fidelity costs, at each <see cref="WaveformFidelity"/>: reading a stem's peaks, mixing several
/// stems' as the player and the lanes do, and working out the seek bar's columns from them. Decoded as the app
/// decodes, on <see cref="AudioBackend.Auto"/>.
/// </summary>
public class WaveformBenchmarks
{
    private static readonly int Parallelism = Math.Max(1, Environment.ProcessorCount - 1);

    // A seek bar about a thousand pixels wide, at three pixels a column.
    private const int SeekBarColumns = 333;

    // As many stems as a split drum kit's lane, or a few tracks heard together.
    private const int MixedStems = 4;

    private OpenStem? _stem;
    private IPcmSource? _source;
    private AudibleStem[] _mixed = [];
    private Waveform _waveform = Waveform.Empty;

    /// <summary>The stem's label, as the host found it; see <see cref="StemLocation"/>.</summary>
    [ParamsSource(nameof(Stems))]
    public string Audio { get; set; } = "";

    [Params(WaveformFidelity.Low, WaveformFidelity.Medium, WaveformFidelity.High, WaveformFidelity.Ultra)]
    public WaveformFidelity Fidelity { get; set; }

    public static IEnumerable<string> Stems() => StemLocation.FromEnvironment().Select(s => s.Label);

    private WaveformResolution Resolution => WaveformResolution.Of(Fidelity);

    [GlobalSetup]
    public void Setup()
    {
        _stem = StemLocation.FromEnvironment().Single(s => s.Label == Audio).Open();
        _source = _stem.OpenSource(AudioBackend.Auto);
        var peaks = WaveformPeaks.Compute(Audio, _source, Resolution);
        _mixed = [.. Enumerable.Range(0, MixedStems).Select(_ => new AudibleStem(peaks, IsMuted: false, IsSolo: false, Volume: 0.5f))];
        _waveform = WaveformPeaks.Of(peaks);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _source?.Dispose();
        _stem?.Dispose();
    }

    /// <summary>
    /// The peaks as the player reads them (<c>PlayerViewModel.ReadPeaks</c>): in 15-second chunks side by side,
    /// each chunk but the first from a source opened for it, so those opens count.
    /// </summary>
    [Benchmark]
    public StemPeaks PeaksChunked()
    {
        _source!.SeekToFrame(0);
        var reader = new StemPeaksReader(Audio, _source, resolution: Resolution);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism };
        Parallel.For(0, reader.ChunkCount, options, index =>
        {
            if (index == 0)
            {
                reader.ReadChunk(0, _source);
            }
            else
            {
                using var source = _stem!.OpenSource(AudioBackend.Auto);
                reader.ReadChunk(index, source);
            }
        });
        return reader.Finish();
    }

    /// <summary>
    /// Several stems summed from their samples for mixing, as the player's waveform is each time a track is switched,
    /// and as a lane of several stems is each time they've been read further.
    /// </summary>
    [Benchmark]
    public Waveform Mix() => WaveformPeaks.Mix(_mixed);

    /// <summary>The seek bar's columns, as <c>WaveformView</c> works them out each time its waveform changes.</summary>
    [Benchmark]
    public double Columns()
    {
        double sum = 0;
        double seconds = _waveform.Seconds;
        for (int column = 0; column < SeekBarColumns; column++)
        {
            int first = (int)Math.Floor((double)column / SeekBarColumns * seconds * _waveform.BucketsPerSecond);
            int last = (int)Math.Ceiling((double)(column + 1) / SeekBarColumns * seconds * _waveform.BucketsPerSecond);
            var (low, high, rms) = _waveform.LevelsOf(first, last);
            sum += high - low + rms;
        }

        return sum;
    }
}
