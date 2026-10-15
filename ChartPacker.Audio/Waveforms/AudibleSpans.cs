using ChartPacker.Core.Charting;

namespace ChartPacker.Audio.Waveforms;

/// <summary>
/// Where a song's stems are heard, from their peaks: the first and last stretch of sound loud enough to hear and long
/// enough not to be a click, for placing <c>[music_start]</c> and <c>[music_end]</c> (charter-spec C8 item 5).
/// </summary>
public static class AudibleSpans
{
    /// <summary>How far a sample reaches, either side of silence, to be heard: -60 dBFS, as the lanes draw silence.</summary>
    public const float Threshold = 0.001f;

    /// <summary>How long a stretch of sound lasts, in seconds, to count: shorter ones are clicks and pops.</summary>
    public const double MinSoundSeconds = 0.05;

    /// <summary>
    /// Where <paramref name="stems"/> are heard, each on its own so a mix can't cancel out, from the earliest start to the
    /// latest end; null when none is heard. Every stem must be read to its end (<see cref="StemPeaks.ReadCount"/>).
    /// </summary>
    /// <exception cref="ArgumentException">A stem isn't read to its end.</exception>
    public static AudibleRange? Of(IEnumerable<StemPeaks> stems, float threshold = Threshold, double minSoundSeconds = MinSoundSeconds)
    {
        ArgumentNullException.ThrowIfNull(stems);
        double? start = null, end = null;
        foreach (var stem in stems)
        {
            if (stem.ReadCount != stem.Count)
                throw new ArgumentException($"The stem {stem.Name} isn't read to its end.", nameof(stems));
            if (Of(stem, threshold, minSoundSeconds) is not { } heard)
                continue;
            start = start is { } s ? Math.Min(s, heard.StartSeconds) : heard.StartSeconds;
            end = end is { } e ? Math.Max(e, heard.EndSeconds) : heard.EndSeconds;
        }

        return start is { } first && end is { } last ? new AudibleRange(first, last) : null;
    }

    // Where one stem is heard: from the start of its first stretch of sound to the end of its last.
    private static AudibleRange? Of(StemPeaks stem, float threshold, double minSoundSeconds)
    {
        int bucketsPerSecond = stem.Resolution.BucketsPerSecond;
        int minRun = Math.Max(1, (int)Math.Ceiling(minSoundSeconds * bucketsPerSecond));
        int? first = null;
        int last = -1;
        int runStart = -1;
        for (int i = 0; i <= stem.Count; i++)
        {
            bool loud = i < stem.Count && Math.Max(-stem.Min[i], stem.Max[i]) >= threshold;
            if (loud)
            {
                if (runStart < 0)
                    runStart = i;
                continue;
            }

            // A stretch the stem ends in counts however short, as the sound is cut off rather than a click.
            if (runStart >= 0 && (i - runStart >= minRun || i == stem.Count))
            {
                first ??= runStart;
                last = i;
            }

            runStart = -1;
        }

        return first is { } from ? new AudibleRange((double)from / bucketsPerSecond, (double)last / bucketsPerSecond) : null;
    }
}
