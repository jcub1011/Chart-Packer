#if AFTER
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ChartShared.Audio.Mixing;
using ChartShared.Core.Sng;

namespace PlaybackBench;

/// <summary>
/// The loops that go a vector of samples or bytes at a time, each beside the one-at-a-time loop it took the place of,
/// which is kept here to measure against.
/// </summary>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class VectorBenchmarks
{
    // A 20 ms stereo chunk at 44.1 kHz, as the playback thread mixes, and the 64 KiB a .sng is read in.
    private const int ChunkSamples = 44100 / 50 * 2;
    private const int ReadBytes = 64 * 1024;

    private readonly float[] _output = new float[ChunkSamples];
    private readonly float[] _stem = new float[ChunkSamples];
    private readonly float[] _mono = new float[1024];
    private readonly float[] _stereo = new float[2048];
    private readonly byte[] _file = new byte[ReadBytes];
    private readonly byte[] _mask = new byte[SngMask.MaskSize];

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);
        for (int i = 0; i < ChunkSamples; i++)
            _stem[i] = (random.NextSingle() * 2f) - 1f;
        for (int i = 0; i < _mono.Length; i++)
            _mono[i] = (random.NextSingle() * 2f) - 1f;
        random.NextBytes(_file);
        random.NextBytes(_mask);
    }

    /// <summary>One chunk of a five-stem song: each stem added on, then the sum clipped.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory("Mix")]
    public void MixOneAtATime()
    {
        _output.AsSpan().Clear();
        for (int stem = 0; stem < 5; stem++)
        {
            for (int i = 0; i < _output.Length; i++)
                _output[i] += _stem[i] * 0.8f;
        }

        foreach (ref float sample in _output.AsSpan())
            sample = Math.Clamp(sample, -1f, 1f);
    }

    [Benchmark, BenchmarkCategory("Mix")]
    public void Mix()
    {
        _output.AsSpan().Clear();
        for (int stem = 0; stem < 5; stem++)
            StemMixer.AddScaled(_output, _stem, 0.8f);
        StemMixer.Clip(_output);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Mono")]
    public void MonoToStereoOneAtATime()
    {
        for (int i = 0; i < _mono.Length; i++)
            _stereo[i * 2] = _stereo[(i * 2) + 1] = _mono[i];
    }

    [Benchmark, BenchmarkCategory("Mono")]
    public void MonoToStereo() => StemReader.MonoToStereo(_mono, _stereo);

    [Benchmark(Baseline = true), BenchmarkCategory("Mask")]
    public void MaskOneAtATime()
    {
        Span<byte> key = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
            key[i] = (byte)(i ^ _mask[i & 0xF]);
        for (int j = 0; j < _file.Length; j++)
            _file[j] ^= key[j & 0xFF];
    }

    [Benchmark, BenchmarkCategory("Mask")]
    public void Mask() => SngMask.Apply(_file, _mask);
}
#endif
