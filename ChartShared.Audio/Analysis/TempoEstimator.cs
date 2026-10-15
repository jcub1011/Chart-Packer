using ChartShared.Core.Charting.Analysis;

namespace ChartShared.Audio.Analysis;

/// <summary>
/// Proposes beat times from onsets (charter-spec C14 item 4): a local tempo through the song (<see cref="Tempogram"/>),
/// beats placed along it (<see cref="BeatTracker"/>), each moved onto the onset it most likely is, and the result cut
/// into stretches of steady tempo, each with a confidence. Also proposes where measure 1 starts (C14 item 5).
/// </summary>
/// <remarks>
/// A stretch's tempo is the slope of a least-squares line through its beats, which is far more precise than any one
/// gap: over twenty beats with a millisecond of jitter it's within a hundredth of a BPM.
/// </remarks>
public static class TempoEstimator
{
    /// <summary>Beats are moved onto an onset at most this far away, or 15% of a beat if that's less.</summary>
    private const double SnapSeconds = 0.040;

    /// <summary>A stretch of steady tempo ends where a gap is this much off its average…</summary>
    private const double IntervalTolerance = 0.04;

    /// <summary>…or a beat is this far off its line.</summary>
    private const double ResidualTolerance = 0.010;

    /// <summary>Stretches shorter than this are too short to trust.</summary>
    private const int MinRegionBeats = 4;

    /// <summary>Proposes beats through the whole of <paramref name="onsets"/>' audio.</summary>
    public static BeatProposal Estimate(OnsetAnalysis onsets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onsets);
        return Estimate(onsets, 0, onsets.FrameCount / onsets.FramesPerSecond, cancellationToken);
    }

    /// <summary>Proposes beats between <paramref name="fromSeconds"/> and <paramref name="toSeconds"/> of the audio.</summary>
    public static BeatProposal Estimate(OnsetAnalysis onsets, double fromSeconds, double toSeconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onsets);

        // A few seconds either side, so the tempo and beats at the edges are found as they are in the middle.
        var flux = onsets.Envelope(OnsetBand.Full);
        double fps = onsets.FramesPerSecond;
        int from = Math.Clamp((int)Math.Floor((fromSeconds - 3) * fps), 0, onsets.FrameCount);
        int to = Math.Clamp((int)Math.Ceiling((toSeconds + 3) * fps), 0, onsets.FrameCount);
        if (to - from < fps * 2)
            return BeatProposal.None;

        var tempogram = Tempogram.Of(flux.Values, from, to, fps, cancellationToken);
        var frames = BeatTracker.Track(flux.Values, from, to, fps, tempogram, cancellationToken);
        frames = Trimmed(frames, flux.Values);
        if (frames.Count < 2)
            return BeatProposal.None;

        var candidates = onsets.Candidates(OnsetBand.Full);
        var (beats, supported) = Refined(frames, fps, candidates);

        // Only the beats in the span asked for.
        var kept = Enumerable.Range(0, beats.Length).Where(i => beats[i] >= fromSeconds - 0.001 && beats[i] <= toSeconds + 0.001).ToList();
        if (kept.Count < 2)
            return BeatProposal.None;
        beats = [.. kept.Select(i => beats[i])];
        supported = [.. kept.Select(i => supported[i])];

        var regions = Regions(beats, supported);
        var confidence = new float[beats.Length];
        foreach (var region in regions)
        {
            for (int i = region.FirstBeat; i <= region.LastBeat; i++)
                confidence[i] = Math.Max(confidence[i], region.Confidence);
        }

        double? bpm = MainBpm(beats, regions);
        bool mayBeHalf = bpm is < 100 && DoubleScoresWell(tempogram);
        return new BeatProposal(beats, confidence, regions, bpm, mayBeHalf);
    }

    /// <summary>
    /// Where measure 1 most likely starts, among <paramref name="beats"/>: the beat of each measure (its phase) that has
    /// the strongest kick, and more weakly the strongest onsets, at the first such beat from where the music starts.
    /// </summary>
    /// <returns><see langword="null"/> when there are too few beats to tell.</returns>
    public static DownbeatEstimate? FindFirstDownbeat(OnsetAnalysis onsets, IReadOnlyList<double> beats, int beatsPerMeasure)
    {
        ArgumentNullException.ThrowIfNull(onsets);
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beatsPerMeasure);
        if (beats.Count == 0)
            return null;

        // The first minute's beats are enough to tell the phase, and nearer where it's used.
        var used = beats.TakeWhile(b => b <= beats[0] + 60).ToList();
        var full = onsets.Envelope(OnsetBand.Full);
        var kicks = used.Select(b => onsets.HasBand(OnsetBand.Kick) ? (double)onsets.Envelope(OnsetBand.Kick).MaxNear(b, 0.030) : 0).ToArray();
        var hits = used.Select(b => (double)full.MaxNear(b, 0.030)).ToArray();
        Normalise(kicks);
        Normalise(hits);

        var scores = new double[beatsPerMeasure];
        for (int phase = 0; phase < beatsPerMeasure; phase++)
        {
            double sum = 0;
            int count = 0;
            for (int i = phase; i < used.Count; i += beatsPerMeasure)
            {
                sum += kicks[i] + (0.5 * hits[i]);
                count++;
            }

            scores[phase] = count == 0 ? 0 : sum / count;
        }

        int best = 0;
        for (int phase = 1; phase < beatsPerMeasure; phase++)
        {
            if (scores[phase] > scores[best])
                best = phase;
        }

        double second = scores.Where((_, phase) => phase != best).DefaultIfEmpty(0).Max();
        float confidence = scores[best] > 0 ? (float)((scores[best] - second) / scores[best]) : 0;

        // From where the music starts: a pickup before measure 1 is left before it.
        var starts = onsets.Times(OnsetBand.Full, OnsetDetector.DefaultSensitivity);
        double musicStart = starts.Length > 0 ? starts[0] : beats[0];
        for (int i = best; i < beats.Count; i += beatsPerMeasure)
        {
            if (beats[i] >= musicStart - 0.020)
                return new DownbeatEstimate(i, beats[i], best, confidence);
        }

        return null;
    }

    private static void Normalise(double[] values)
    {
        double mean = values.Length == 0 ? 0 : values.Average();
        if (mean <= 0)
            return;
        for (int i = 0; i < values.Length; i++)
            values[i] /= mean;
    }

    /// <summary>
    /// <paramref name="frames"/> without the beats at either end that have no onset to speak of: the tracker carries
    /// the beat on through silence before and after the music.
    /// </summary>
    private static List<int> Trimmed(List<int> frames, ReadOnlySpan<float> flux)
    {
        if (frames.Count == 0)
            return frames;

        var strengths = new float[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            float max = 0;
            for (int m = Math.Max(0, frames[i] - 3); m <= Math.Min(flux.Length - 1, frames[i] + 3); m++)
                max = Math.Max(max, flux[m]);
            strengths[i] = max;
        }

        var sorted = strengths.Order().ToArray();
        float threshold = 0.25f * sorted[(int)(sorted.Length * 0.9)];
        int first = 0, last = frames.Count - 1;
        while (first <= last && strengths[first] < threshold)
            first++;
        while (last >= first && strengths[last] < threshold)
            last--;
        return first > last ? [] : frames.GetRange(first, last - first + 1);
    }

    /// <summary>
    /// Each beat moved onto the onset near it that stands out most, nearer ones preferred; a beat with no onset near is
    /// placed in line with the beats round it that have one.
    /// </summary>
    private static (double[] Beats, bool[] Supported) Refined(List<int> frames, double framesPerSecond, IReadOnlyList<DetectedOnset> candidates)
    {
        int count = frames.Count;
        var beats = new double[count];
        var supported = new bool[count];
        var times = candidates.Select(c => c.Seconds).ToArray();
        double least = OnsetDetector.ThresholdFor(0.9);

        for (int i = 0; i < count; i++)
        {
            double at = frames[i] / framesPerSecond;
            double period = i + 1 < count ? (frames[i + 1] - frames[i]) / framesPerSecond : i > 0 ? (frames[i] - frames[i - 1]) / framesPerSecond : 1;
            double reach = Math.Min(SnapSeconds, 0.15 * period);

            int start = LowerBound(times, at - reach);
            double bestScore = 0;
            for (int j = start; j < times.Length && times[j] <= at + reach; j++)
            {
                var candidate = candidates[j];
                if (candidate.Strength < least)
                    continue;
                double distance = (candidate.Seconds - at) / 0.020;
                double score = candidate.Strength * Math.Exp(-distance * distance);
                if (score > bestScore)
                {
                    bestScore = score;
                    beats[i] = candidate.Seconds;
                    supported[i] = true;
                }
            }

            if (!supported[i])
                beats[i] = at;
        }

        FillUnsupported(beats, supported);

        // Two beats moved onto one onset: the later one goes back in line.
        for (int i = 1; i < count; i++)
        {
            if (beats[i] <= beats[i - 1] + 0.001)
            {
                supported[i] = false;
                beats[i] = frames[i] / framesPerSecond;
            }
        }

        return (beats, supported);
    }

    /// <summary>Puts each unsupported beat in line with the nearest supported ones either side, or on one side at the ends.</summary>
    private static void FillUnsupported(double[] beats, bool[] supported)
    {
        var known = Enumerable.Range(0, beats.Length).Where(i => supported[i]).ToList();
        if (known.Count < 2)
            return;

        for (int i = 0; i < beats.Length; i++)
        {
            if (supported[i])
                continue;

            int after = known.FindIndex(k => k > i);
            int a, b;
            if (after <= 0)
                (a, b) = after == 0 ? (known[0], known[1]) : (known[^2], known[^1]);
            else
                (a, b) = (known[after - 1], known[after]);

            double slope = (beats[b] - beats[a]) / (b - a);
            beats[i] = beats[a] + (slope * (i - a));
        }
    }

    private static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>The beats cut into stretches of steady tempo, neighbours sharing their boundary beat; short ones joined and marked unsure.</summary>
    private static List<TempoRegion> Regions(double[] beats, bool[] supported)
    {
        var spans = new List<(int First, int Last)>();
        int start = 0;
        while (start < beats.Length - 1)
        {
            int end = start + 1;
            while (end + 1 < beats.Length)
            {
                double average = (beats[end] - beats[start]) / (end - start);
                double gap = beats[end + 1] - beats[end];
                if (Math.Abs(gap - average) > IntervalTolerance * average)
                    break;
                if (end + 1 - start >= 2 && MaxResidual(beats.AsSpan(start, end + 2 - start)) > ResidualTolerance)
                    break;
                end++;
            }

            spans.Add((start, end));
            start = end;
        }

        // Runs of short stretches become one unsure stretch.
        var regions = new List<TempoRegion>();
        int? shortFrom = null;
        int shortTo = 0;
        foreach (var (first, last) in spans)
        {
            if (last - first + 1 < MinRegionBeats)
            {
                shortFrom ??= first;
                shortTo = last;
                continue;
            }

            if (shortFrom is { } from)
                regions.Add(Unsure(beats, from, shortTo));
            shortFrom = null;
            regions.Add(Region(beats, supported, first, last));
        }

        if (shortFrom is { } trailing)
            regions.Add(Unsure(beats, trailing, shortTo));
        return regions;
    }

    private static double MaxResidual(ReadOnlySpan<double> beats)
    {
        var (intercept, period, _) = BeatMath.FitLine(beats);
        double max = 0;
        for (int i = 0; i < beats.Length; i++)
            max = Math.Max(max, Math.Abs(beats[i] - (intercept + (period * i))));
        return max;
    }

    private static TempoRegion Region(double[] beats, bool[] supported, int first, int last)
    {
        var span = beats.AsSpan(first, last - first + 1);
        var (_, period, rms) = BeatMath.FitLine(span);
        int backed = 0;
        for (int i = first; i <= last; i++)
        {
            if (supported[i])
                backed++;
        }

        double support = (double)backed / (last - first + 1);
        double regularity = Math.Clamp(1 - (rms / (0.04 * period)), 0, 1);
        return new TempoRegion(first, last, 60 / period, (float)(support * regularity));
    }

    private static TempoRegion Unsure(double[] beats, int first, int last)
    {
        double bpm = last > first ? 60 / ((beats[last] - beats[first]) / (last - first)) : 0;
        return new TempoRegion(first, last, bpm, TempoRegion.LowConfidence / 2);
    }

    /// <summary>The tempo most of the confident beats are at: the median of their stretches', weighted by how long each lasts.</summary>
    private static double? MainBpm(double[] beats, List<TempoRegion> regions)
    {
        var confident = regions.Where(r => !r.IsLowConfidence).OrderBy(r => r.Bpm).ToList();
        if (confident.Count == 0)
            return null;

        double total = confident.Sum(r => beats[r.LastBeat] - beats[r.FirstBeat]);
        double running = 0;
        foreach (var region in confident)
        {
            running += beats[region.LastBeat] - beats[region.FirstBeat];
            if (running >= total / 2)
                return region.Bpm;
        }

        return confident[^1].Bpm;
    }

    /// <summary>Twice the path's tempo scores nearly as well as the path itself, on average: the beats may be at half tempo.</summary>
    private static bool DoubleScoresWell(Tempogram tempogram)
    {
        int octave = (int)Math.Round(Math.Log(2) / Math.Log(Tempogram.Step));
        double ratio = 0;
        int count = 0;
        for (int w = 0; w < tempogram.WindowCount; w++)
        {
            int tempo = tempogram.Path[w];
            float here = tempogram.Score(w, tempo);
            if (here <= 0 || tempo + octave >= Tempogram.TempoCount)
                continue;
            ratio += tempogram.Score(w, tempo + octave) / here;
            count++;
        }

        return count > 0 && ratio / count >= 0.6;
    }
}
