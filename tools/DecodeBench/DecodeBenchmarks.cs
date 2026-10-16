using BenchmarkDotNet.Attributes;
using ChartShared.Audio.Decoding;
using ChartShared.Audio.Waveforms;

namespace DecodeBench;

/// <summary>
/// Each stem read the ways the app reads it, on each backend. Every read but <see cref="Open"/> starts from
/// an open source, rewound to the start, so opening isn't in its time.
/// </summary>
public class DecodeBenchmarks
{
    private static readonly int Parallelism = Math.Max(1, Environment.ProcessorCount - 1);

    private OpenStem? _stem;
    private IPcmSource? _source;
    private float[] _buffer = [];

    /// <summary>The stem's label, as the host found it; see <see cref="StemLocation"/>.</summary>
    [ParamsSource(nameof(Stems))]
    public string Audio { get; set; } = "";

    /// <summary>What decodes the packets. The host leaves out FFmpeg when it can't be used.</summary>
    [Params(AudioBackend.Managed, AudioBackend.Ffmpeg)]
    public AudioBackend Backend { get; set; }

    public static IEnumerable<string> Stems() => StemLocation.FromEnvironment().Select(s => s.Label);

    [GlobalSetup]
    public void Setup()
    {
        _stem = StemLocation.FromEnvironment().Single(s => s.Label == Audio).Open();
        _source = _stem.OpenSource(Backend);
        _buffer = new float[4096 * _source.Channels];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _source?.Dispose();
        _stem?.Dispose();
    }

    /// <summary>Opening a decoder: the MP3 and Ogg index scans, and the decoder's setup.</summary>
    [Benchmark]
    public int Open()
    {
        using var source = _stem!.OpenSource(Backend);
        return source.Channels;
    }

    /// <summary>Decoding the whole stem, with the output thrown away.</summary>
    [Benchmark]
    public long Decode()
    {
        _source!.SeekToFrame(0);
        long frames = 0;
        int read;
        while ((read = _source.ReadFrames(_buffer)) > 0)
            frames += read;
        return frames;
    }

    /// <summary>The whole stem's peaks, on one thread.</summary>
    [Benchmark]
    public StemPeaks PeaksOneThread()
    {
        _source!.SeekToFrame(0);
        return WaveformPeaks.Compute(Audio, _source);
    }

    /// <summary>
    /// The peaks as the player reads them (<c>PlayerViewModel.ReadPeaks</c>): in 15-second chunks side by side,
    /// each chunk but the first from a source opened for it, so those opens count.
    /// </summary>
    [Benchmark]
    public StemPeaks PeaksChunked()
    {
        _source!.SeekToFrame(0);
        var reader = new StemPeaksReader(Audio, _source);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism };
        Parallel.For(0, reader.ChunkCount, options, index =>
        {
            if (index == 0)
            {
                reader.ReadChunk(0, _source);
            }
            else
            {
                using var source = _stem!.OpenSource(Backend);
                reader.ReadChunk(index, source);
            }
        });
        return reader.Finish();
    }
}
