using ChartShared.Audio.Decoding;
using ChartShared.Audio.Mixing;
using ChartShared.Core.Optimization;

namespace ChartShared.Audio.Encoding;

/// <summary>
/// The Library Optimization Tool's audio optimizer: transcodes a stem to Ogg Opus at the chosen bitrate, or mixes
/// several stems down to one Opus stem, with <see cref="OpusTranscoder"/> (managed, AOT-safe, no native dependency).
/// </summary>
/// <remarks>
/// <para><b>Bitrate.</b> <see cref="AudioOptimizeOptions.BitrateKbps"/> is per stereo stem and a mono stem gets half:
/// 128 kbps by default (<see cref="AudioOptimizeOptions.Default"/>), 80 kbps for <see cref="AudioOptimizeOptions.Smaller"/>.</para>
/// <para><b>Estimates.</b> Rather than encode a whole file, the estimate seeks to equally spaced windows (the middles of
/// equal parts of the song), encodes each at the chosen bitrate into a sink that counts bytes, and scales the encoded size
/// per second of audio to the whole duration (the stream headers, which a window carries but a whole file carries once, are
/// counted once). Only the sampled windows are decoded, so damage elsewhere in a stem passes the estimate and fails the
/// optimize. It's the size of what would be written; what it's compared with, and so the
/// skip rules (below the margin, larger than the input), is <see cref="OptimizationPlanner"/>'s. A file that's short enough
/// that the windows would cover it is encoded whole, which makes its estimate exact. A stem that's already Opus is reported
/// <see cref="OptimizeEstimate.AlreadyOptimal"/> without being decoded, so it's skipped.</para>
/// <para><b>Mixdown.</b> Given several sources, it writes one stereo Opus stem of their mix, which the caller names (the
/// planner names it <c>song.opus</c>) and whose sources it removes. What that costs:</para>
/// <list type="bullet">
/// <item>The games can no longer mute, solo or weight an instrument: per-instrument isolation (and the stem a game
/// mutes when you miss notes) is gone. Only what's left separate is still its own stem: <c>preview</c> and <c>crowd</c>
/// are never mixed in.</item>
/// <item>The stems are summed at their recorded levels and the sum is clipped to full scale; it isn't normalised
/// (<see cref="StemMixer"/>). Stems that peak together can clip, audibly, where playing them separately didn't.</item>
/// <item><c>drums_1</c> to <c>drums_4</c> are stems like any other: each is mixed in on its own, not folded into
/// <c>drums</c> first, so a chart with a kit split across them ends up with it all in the mix.</item>
/// <item>The stems are lossy-decoded and encoded again, so the mix is a generation further from the masters than a
/// transcode of each stem alone is. A stem that's damaged fails the mixdown rather than being left out of it.</item>
/// <item>The planner skips a mixdown whose output name is taken by a file it wouldn't replace, as
/// <see cref="OpusPackTranscoder"/> refuses two stems that would pack as one name.</item>
/// </list>
/// </remarks>
/// <param name="windowCount">How many windows an estimate encodes.</param>
/// <param name="windowSeconds">How long each window is.</param>
public sealed class OpusAudioOptimizer(int windowCount = OpusAudioOptimizer.DefaultWindowCount, double windowSeconds = OpusAudioOptimizer.DefaultWindowSeconds) : IAudioOptimizer
{
    /// <summary>How many windows an estimate encodes by default.</summary>
    public const int DefaultWindowCount = 8;

    /// <summary>How many seconds long each window is by default.</summary>
    public const double DefaultWindowSeconds = 3;

    private readonly int _windowCount = windowCount >= 1 ? windowCount : throw new ArgumentOutOfRangeException(nameof(windowCount));
    private readonly double _windowSeconds = windowSeconds > 0 ? windowSeconds : throw new ArgumentOutOfRangeException(nameof(windowSeconds));

    /// <inheritdoc />
    public bool IsAvailable => OpusTranscoder.IsAvailable;

    /// <inheritdoc />
    public Task<OptimizeEstimate> EstimateAsync(IReadOnlyList<AssetFile> sources, AudioOptimizeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        if (sources.Count == 0)
            throw new ArgumentException("There's nothing to estimate.", nameof(sources));

        if (sources.Count == 1 && IsOpus(sources[0].Asset))
            return Task.FromResult(new OptimizeEstimate(sources[0].Asset.Length, OutputExtension, AlreadyOptimal: true));

        return Task.Run(() => Estimate(sources, options.BitrateKbps, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task OptimizeAsync(IReadOnlyList<AssetFile> sources, string destinationPath, AudioOptimizeOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(options);
        if (sources.Count == 0)
            throw new ArgumentException("There's nothing to optimize.", nameof(sources));

        return Task.Run(() => Optimize(sources, destinationPath, options.BitrateKbps, progress, cancellationToken), cancellationToken);
    }

    private const string OutputExtension = ".opus";

    /// <summary>
    /// Whether the stem is Opus already, by its name or its probe, without decoding it. Such a stem is deliberately left
    /// alone whatever bitrate is asked for: encoding lossy audio again to the same codec at a slightly lower rate adds
    /// artifacts for little saving.
    /// </summary>
    private static bool IsOpus(ChartAsset asset) =>
        asset.Extension.Equals(OutputExtension, StringComparison.OrdinalIgnoreCase)
        || string.Equals(asset.Probe?.CodecKey, "audio/opus", StringComparison.OrdinalIgnoreCase);

    private OptimizeEstimate Estimate(IReadOnlyList<AssetFile> sources, int bitrateKbps, CancellationToken cancellationToken)
    {
        using var opened = Open(sources);
        var audio = opened.Audio;
        string name = NameOf(sources, null);

        long total = audio.LengthFrames;
        long windowFrames = Math.Max(1, (long)Math.Round(_windowSeconds * audio.SampleRate));

        // Short enough that the windows would cover it, or of unknown length: encode all of it, which is exact.
        if (total < 0 || total <= windowFrames * _windowCount)
            return new OptimizeEstimate(EncodedSize(audio, bitrateKbps, name, cancellationToken), OutputExtension);

        long bytes = 0;
        long frames = 0;
        for (int i = 0; i < _windowCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The middle of the i-th of equal parts of the song.
            long start = Math.Clamp((long)((((i + 0.5) * total) / _windowCount) - (windowFrames / 2.0)), 0, total - windowFrames);
            var window = new PcmWindow(audio, start, windowFrames);

            // Each window is a stream of its own, so it carries the headers that a whole encode carries once: they're left
            // out of the per-frame rate and added back once, below. Frames are counted as read, not as the window declared
            // them, so a source that ends early doesn't dilute the rate.
            bytes += EncodedSize(window, bitrateKbps, name, cancellationToken) - OggOpusWriter.StreamHeaderBytes;
            frames += window.FramesRead;
        }

        if (frames == 0)
            throw new InvalidDataException($"'{name}' has no audio.");

        // Bytes per frame of what was sampled, over the whole song, and the headers once.
        return new OptimizeEstimate(OggOpusWriter.StreamHeaderBytes + (long)Math.Round((double)bytes / frames * total), OutputExtension);
    }

    private static long EncodedSize(IPcmSource source, int bitrateKbps, string name, CancellationToken cancellationToken)
    {
        var sink = new CountingStream();
        OpusTranscoder.Transcode(source, sink, bitrateKbps, name, cancellationToken: cancellationToken);
        return sink.BytesWritten;
    }

    private static void Optimize(IReadOnlyList<AssetFile> sources, string destinationPath, int bitrateKbps, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Opened before the destination exists, and outside the try: a source that can't be read, or a destination that
        // can't be created, hasn't written anything, so there's nothing of this call's to delete (and whatever is already at
        // the path isn't this call's to delete).
        using var opened = Open(sources);
        var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            using (output)
            {
                var fileProgress = progress is null ? null : new InlineProgress(f => progress.Report(f));
                OpusTranscoder.Transcode(opened.Audio, output, bitrateKbps, NameOf(sources, destinationPath), fileProgress, cancellationToken);
            }
        }
        catch
        {
            // Nothing partial is left for the caller to find: the file is closed by now, so it can be deleted.
            TryDelete(destinationPath);
            throw;
        }
    }

    /// <summary>What the audio is called in messages: the one source's name, or the mix's output name.</summary>
    private static string NameOf(IReadOnlyList<AssetFile> sources, string? destinationPath) =>
        sources.Count == 1 ? sources[0].Asset.StoredName : destinationPath is null ? "the mixed stems" : Path.GetFileName(destinationPath);

    /// <summary>The sources opened, and the one stream of audio they make: the source itself, or the mix of several.</summary>
    private sealed class Opened : IDisposable
    {
        private readonly List<IPcmSource> _sources;

        public Opened(List<IPcmSource> sources, IPcmSource audio)
        {
            _sources = sources;
            Audio = audio;
        }

        public IPcmSource Audio { get; }

        public void Dispose()
        {
            foreach (var source in _sources)
                source.Dispose();
        }
    }

    /// <exception cref="InvalidDataException">A source isn't audio it can decode.</exception>
    /// <exception cref="IOException">A source can't be read.</exception>
    private static Opened Open(IReadOnlyList<AssetFile> files)
    {
        var sources = new List<IPcmSource>(files.Count);
        try
        {
            foreach (var file in files)
            {
                sources.Add(AudioDecoder.Open(
                    new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read), file.Path));
            }

            if (sources.Count == 1)
                return new Opened(sources, sources[0]);

            var mixer = new StemMixer([.. files.Select((f, i) => KeyValuePair.Create(f.Asset.StoredName, sources[i]))]);
            return new Opened(sources, new MixedPcmSource(mixer));
        }
        catch
        {
            foreach (var source in sources)
                source.Dispose();
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reports on the calling thread, where <see cref="Progress{T}"/> would post to a synchronization context and arrive out of order.</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<TranscodeFileProgress>
    {
        public void Report(TranscodeFileProgress value) => report(value.Fraction);
    }
}
