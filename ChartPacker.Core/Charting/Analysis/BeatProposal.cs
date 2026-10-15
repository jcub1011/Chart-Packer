namespace ChartPacker.Core.Charting.Analysis;

/// <summary>A stretch of a <see cref="BeatProposal"/> at one steady tempo.</summary>
/// <param name="FirstBeat">The index of its first beat.</param>
/// <param name="LastBeat">The index of its last beat. Neighbouring regions share their boundary beat.</param>
/// <param name="Bpm">Its tempo, from a least-squares line through its beats.</param>
/// <param name="Confidence">How sure the estimate is, from 0 to 1: how many beats an onset backs, and how evenly they're spaced.</param>
public readonly record struct TempoRegion(int FirstBeat, int LastBeat, double Bpm, float Confidence)
{
    /// <summary>Below this, a region is marked for the author to map by hand (charter-spec C14 item 4).</summary>
    public const float LowConfidence = 0.5f;

    /// <summary>The region is too unsteady (rubato, a breakdown) to trust.</summary>
    public bool IsLowConfidence => Confidence < LowConfidence;

    /// <summary>How many beats it has.</summary>
    public int BeatCount => LastBeat - FirstBeat + 1;
}

/// <summary>
/// Beat times proposed from the audio (charter-spec C14 item 4), as plain data: the editor shows them as a ghost tempo
/// map, and turns them into tempo markers only when the author accepts them.
/// </summary>
/// <param name="Beats">Every beat, in audio seconds, ascending.</param>
/// <param name="BeatConfidence">How sure each beat is, from 0 to 1, one per beat.</param>
/// <param name="Regions">The stretches of steady tempo, in order, covering every beat.</param>
/// <param name="Bpm">The song's main tempo, or <see langword="null"/> when no region is confident.</param>
/// <param name="MayBeHalfTempo">The beats may be at half the song's tempo: the author may want to double it.</param>
public sealed record BeatProposal(
    IReadOnlyList<double> Beats,
    IReadOnlyList<float> BeatConfidence,
    IReadOnlyList<TempoRegion> Regions,
    double? Bpm,
    bool MayBeHalfTempo)
{
    /// <summary>No beats: the audio has no steady beat to find.</summary>
    public static BeatProposal None { get; } = new([], [], [], null, false);

    /// <summary>There are no beats.</summary>
    public bool IsEmpty => Beats.Count == 0;

    /// <summary>The first confident region's tempo, for the chart's starting BPM, else <see cref="Bpm"/>.</summary>
    public double? StartingBpm => Regions.Where(r => !r.IsLowConfidence).Select(r => (double?)r.Bpm).FirstOrDefault() ?? Bpm;
}

/// <summary>Where measure 1 most likely starts (charter-spec C14 item 5).</summary>
/// <param name="BeatIndex">The beat of the proposal it's on.</param>
/// <param name="Seconds">When, in audio seconds.</param>
/// <param name="Phase">Which beat of each measure the downbeats are, counted from the proposal's first beat.</param>
/// <param name="Confidence">How much more the chosen phase stands out than the next best, from 0 to 1.</param>
public readonly record struct DownbeatEstimate(int BeatIndex, double Seconds, int Phase, float Confidence);
