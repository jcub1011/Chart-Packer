using BenchmarkDotNet.Attributes;
using ChartPacker.Audio.Decoding;
using ChartPacker.Audio.Waveforms;

namespace DecodeBench;

/// <summary>
/// Each stem read the ways the app reads it, and by FFmpeg's decoder for comparison. Every read but
/// <see cref="Open"/> starts from an open source, rewound to the start, so opening isn't in its time.
/// </summary>
public class DecodeBenchmarks
{
    private static readonly int Parallelism = Math.Max(1, Environment.ProcessorCount - 1);

    private OpenStem? _stem;
    private IPcmSource? _source;
    private float[] _buffer = [];
    private FfmpegReference.Decoder? _ffmpeg;
    private int _channels;

    /// <summary>The stem's label, as the host found it; see <see cref="StemLocation"/>.</summary>
    [ParamsSource(nameof(Stems))]
    public string Audio { get; set; } = "";

    public static IEnumerable<string> Stems() => StemLocation.FromEnvironment().Select(s => s.Label);

    [GlobalSetup]
    public void Setup()
    {
        _stem = StemLocation.FromEnvironment().Single(s => s.Label == Audio).Open();
        _source = _stem.OpenSource();
        _channels = _source.Channels;
        _buffer = new float[4096 * _channels];

        // Only the FFmpeg benchmark needs it, and the host leaves that out when FFmpeg can't be used.
        if (FfmpegReference.UnavailableReason is null)
            _ffmpeg = FfmpegReference.Open(_stem.OpenStream());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ffmpeg?.Dispose();
        _source?.Dispose();
        _stem?.Dispose();
    }

    /// <summary>Opening a decoder: the MP3 and Opus index scans, and NVorbis's setup.</summary>
    [Benchmark]
    public int Open()
    {
        using var source = _stem!.OpenSource();
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
                using var source = _stem!.OpenSource();
                reader.ReadChunk(index, source);
            }
        });
        return reader.Finish();
    }

    /// <summary>Decoding the whole stem with FFmpeg's decoder, on one thread, with the output thrown away.</summary>
    [Benchmark]
    public long Ffmpeg() => _ffmpeg!.Run(_channels).Frames;
}
