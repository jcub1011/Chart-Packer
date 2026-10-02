using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Merging;

/// <summary>How a link between two songs' charts of a part works out how far apart the songs are.</summary>
public enum AlignmentMethod
{
    /// <summary>The part's first notes play together.</summary>
    FirstNote,

    /// <summary>As many of the part's notes play together as can, all through the song; see <see cref="SourceAligner"/>.</summary>
    NotePattern,

    /// <summary>The songs' audio, each mixed down, sounds together: for songs that share no part, or have no chart.</summary>
    Audio,
}

/// <summary>A song's chart, read for lining up: when the notes of each part it can be lined up by start.</summary>
/// <remarks>
/// The parts are those with one instrument the chart reader models (guitar, bass, drums, keys and the like), and the
/// vocals and harmonies. Times are of the song's own audio: its delay included.
/// </remarks>
public sealed class AlignmentChart
{
    // The difficulties each instrument's part is read at, and its notes at each; the vocals' under null.
    private readonly Dictionary<string, Dictionary<ChartDifficulty, AlignmentNotes>> _instruments;
    private readonly Dictionary<string, AlignmentNotes> _vocals;

    private AlignmentChart(TempoMap tempo, TimeSpan delay, Dictionary<string, Dictionary<ChartDifficulty, AlignmentNotes>> instruments, Dictionary<string, AlignmentNotes> vocals)
    {
        Tempo = tempo;
        Delay = delay;
        _instruments = instruments;
        _vocals = vocals;
        PartIds = [.. MidiPartCatalog.Known.Select(p => p.Id).Where(id => instruments.ContainsKey(id) || vocals.ContainsKey(id))];
    }

    /// <summary>The chart's tempo map.</summary>
    public TempoMap Tempo { get; }

    /// <summary>How much later than its ticks say the chart plays against its audio.</summary>
    public TimeSpan Delay { get; }

    /// <summary>The parts it can be lined up by, by <see cref="MidiPart.Id"/>, in catalog order.</summary>
    public IReadOnlyList<string> PartIds { get; }

    /// <summary>Reads <paramref name="chart"/> for lining up.</summary>
    /// <exception cref="ChartFormatException">The chart is malformed.</exception>
    public static AlignmentChart Of(MidiChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var doc = ChartReader.Read(chart.Midi, ChartFiles.MidiName, chart.Options);
        double delay = chart.Delay.TotalSeconds;

        var instruments = new Dictionary<string, Dictionary<ChartDifficulty, AlignmentNotes>>(StringComparer.Ordinal);
        foreach (var part in MidiPartCatalog.Known)
        {
            if (MidiPartCatalog.InstrumentsOf(part) is not [var instrument])
                continue;

            var byDifficulty = new Dictionary<ChartDifficulty, AlignmentNotes>();
            foreach (var difficulty in doc.DifficultiesOf(instrument))
            {
                var notes = doc.Tracks[new ChartTrackKey(instrument, difficulty)].Notes;
                bool drums = ChartInstruments.KindOf(instrument) == ChartInstrumentKind.Drums;
                byDifficulty[difficulty] = AlignmentNotes.Of(notes.Select(n => (Seconds: doc.Tempo.SecondsAt(n.Tick) + delay, Kind: drums ? DrumKindOf(n) : 0)), doc.Tempo, notes[0].Tick);
            }

            if (byDifficulty.Count > 0)
                instruments[part.Id] = byDifficulty;
        }

        var vocals = new Dictionary<string, AlignmentNotes>(StringComparer.Ordinal);
        var vocalChart = VocalReader.Read(chart.Midi, ChartFiles.MidiName, doc.Tempo);
        foreach (var (partId, trackName) in new[] { ("vocals", VocalReader.VocalsTrackName), ("harmonies", "HARM1") })
        {
            if (vocalChart.Parts.FirstOrDefault(p => p.TrackName.Equals(trackName, StringComparison.OrdinalIgnoreCase)) is { Notes.Count: > 0 } part)
                vocals[partId] = AlignmentNotes.Of(part.Notes.Select(n => (Seconds: doc.Tempo.SecondsAt(n.Tick) + delay, Kind: 0)), doc.Tempo, part.Notes.Min(n => n.Tick));
        }

        return new AlignmentChart(doc.Tempo, chart.Delay, instruments, vocals);
    }

    /// <summary>The difficulties <paramref name="partId"/> is read at, easiest first; empty for the vocals, which have none, or a part it doesn't have.</summary>
    public IReadOnlyList<ChartDifficulty> DifficultiesOf(string partId) =>
        _instruments.TryGetValue(partId, out var byDifficulty) ? [.. byDifficulty.Keys.Order()] : [];

    /// <summary><paramref name="partId"/>'s notes at <paramref name="difficulty"/>, or the vocals' without one.</summary>
    internal AlignmentNotes? NotesOf(string partId, ChartDifficulty? difficulty) =>
        difficulty is { } d
            ? _instruments.GetValueOrDefault(partId)?.GetValueOrDefault(d)
            : _vocals.GetValueOrDefault(partId);

    /// <summary>Whether <paramref name="partId"/> is read without difficulties, as the vocals are.</summary>
    internal bool IsVocal(string partId) => _vocals.ContainsKey(partId);

    // Kicks and snares are charted alike whoever charts them, where the other pads (cymbals or toms, four lanes or five)
    // aren't; kept apart, a steady hi-hat can't line up on every beat on its own.
    private static int DrumKindOf(ChartNote note) => note.Lane switch
    {
        ChartNote.OpenLane => 0,
        0 => 1,
        _ => 2,
    };
}

/// <summary>When the notes of one part start, in seconds of the song's own audio, for lining up.</summary>
internal sealed class AlignmentNotes
{
    private AlignmentNotes(double[][] byKind, double first, double last, double beatSeconds)
    {
        ByKind = byKind;
        First = first;
        Last = last;
        BeatSeconds = beatSeconds;
        Count = byKind.Sum(k => k.Length);
    }

    /// <summary>For each kind of note, when one starts, once each, in order. A chord is one note of each kind in it.</summary>
    public double[][] ByKind { get; }

    /// <summary>When the first note starts.</summary>
    public double First { get; }

    /// <summary>When the last note starts.</summary>
    public double Last { get; }

    /// <summary>How long a beat is at the first note.</summary>
    public double BeatSeconds { get; }

    public int Count { get; }

    /// <param name="firstTick">The tick of the first note, for the beat there.</param>
    public static AlignmentNotes Of(IEnumerable<(double Seconds, int Kind)> notes, TempoMap tempo, long firstTick)
    {
        var all = notes.ToList();
        int kinds = all.Count == 0 ? 0 : all.Max(n => n.Kind) + 1;
        var byKind = Enumerable.Range(0, kinds)
            .Select(k => all.Where(n => n.Kind == k).Select(n => n.Seconds).Distinct().Order().ToArray())
            .ToArray();
        var change = tempo.Tempos.Last(t => t.Tick <= Math.Max(0, firstTick));
        return new AlignmentNotes(byKind, all.Min(n => n.Seconds), all.Max(n => n.Seconds), change.MicrosecondsPerQuarter / 1_000_000.0);
    }

    /// <summary>These notes, only those that start from <paramref name="from"/> up to <paramref name="to"/>, in seconds.</summary>
    public AlignmentNotes Between(double from, double to)
    {
        var byKind = ByKind.Select(k => k.Where(t => t >= from && t <= to).ToArray()).ToArray();
        var starts = byKind.SelectMany(k => k).ToList();
        return starts.Count == 0
            ? new AlignmentNotes(byKind, from, from, BeatSeconds)
            : new AlignmentNotes(byKind, starts.Min(), starts.Max(), BeatSeconds);
    }
}

/// <summary>How far one song is from another by a link, worked out one way.</summary>
/// <param name="Method">How it was worked out.</param>
/// <param name="OffsetMs">How far to move the second song, in milliseconds, later when positive, to line it up with the first.</param>
/// <param name="Matched">
/// For <see cref="AlignmentMethod.NotePattern"/>, about how many of the notes line up at the offset; zero for
/// <see cref="AlignmentMethod.FirstNote"/>, which only lines up one.
/// </param>
/// <param name="Match">
/// For a note pattern, the share of the notes that could line up that do, from 0 to 1; for audio, how alike the two mixes'
/// beats and hits are at the offset, from 0 to 1. 0 for a first note.
/// </param>
/// <param name="IsAmbiguous">For a note pattern or audio, another offset, as a beat away, lines up nearly as well.</param>
/// <param name="NarrowedBy">
/// For a note pattern, what it was looked for near, within half a beat, as looking all through the song was ambiguous: the
/// audio's offset, or the first notes'. <see langword="null"/> when it wasn't.
/// </param>
/// <param name="DriftMs">
/// For a note pattern or audio, how much further the end of the song lines up than its start, in milliseconds: a song a
/// different length, or played at a different speed, which no one offset lines up all through. <see langword="null"/> when
/// there isn't enough at both ends to tell, or for a first note.
/// </param>
public sealed record AlignmentEstimate(AlignmentMethod Method, int OffsetMs, int Matched = 0, double Match = 0, bool IsAmbiguous = false, AlignmentMethod? NarrowedBy = null, int? DriftMs = null)
{
    /// <summary>For a note pattern, it was only looked for near another estimate; see <see cref="NarrowedBy"/>.</summary>
    public bool IsNarrowed => NarrowedBy is not null;

    /// <summary>
    /// Whether it can be taken without looking. A note pattern: enough notes line up, half of them or more, and no other
    /// offset lines up nearly as many. Audio: the mixes are at least <see cref="SourceAligner.MinAudioMatch"/> alike, and no
    /// other offset is nearly as alike. A first note can't be told either way, so never is.
    /// </summary>
    public bool IsReliable => Method switch
    {
        AlignmentMethod.NotePattern => Matched >= SourceAligner.MinMatchedNotes && Match >= SourceAligner.MinMatch && !IsAmbiguous,
        AlignmentMethod.Audio => Match >= SourceAligner.MinAudioMatch && !IsAmbiguous,
        _ => false,
    };

    /// <summary>The end of the song lines up more than <see cref="SourceAligner.DriftToleranceMs"/> away from its start.</summary>
    public bool HasDrift => DriftMs is { } drift && Math.Abs(drift) > SourceAligner.DriftToleranceMs;

    /// <summary>The estimate the other way round: how far to move the first song to line it up with the second.</summary>
    public AlignmentEstimate Reversed() => this with { OffsetMs = -OffsetMs, DriftMs = -DriftMs };
}

/// <summary>
/// Two songs that can be lined up, and how far apart they are: by a part they share, its first notes and its note pattern;
/// or by their audio.
/// </summary>
/// <param name="From">The song lined up with.</param>
/// <param name="To">The song to line up: <see cref="AlignmentEstimate.OffsetMs"/> is how far to move it.</param>
/// <param name="PartId">The part they share, by <see cref="MidiPart.Id"/>; <see langword="null"/> for their audio.</param>
/// <param name="Difficulty">The difficulty both are read at: the hardest both have. <see langword="null"/> for the vocals, or audio.</param>
/// <param name="FirstNote">For a part, how far apart its first notes put the songs.</param>
/// <param name="Pattern">For a part, how far apart its note pattern puts them.</param>
/// <param name="Audio">For their audio, how far apart it puts them.</param>
public sealed record AlignmentLink(int From, int To, string? PartId, ChartDifficulty? Difficulty, AlignmentEstimate? FirstNote, AlignmentEstimate? Pattern, AlignmentEstimate? Audio = null)
{
    /// <summary>A link by the songs' audio.</summary>
    public static AlignmentLink ByAudio(int from, int to, AlignmentEstimate audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return new AlignmentLink(from, to, null, null, null, null, audio);
    }

    /// <summary>The link is by the songs' audio rather than a part.</summary>
    public bool IsAudio => Audio is not null;

    /// <summary>The ways it lines the songs up: a part's note pattern and first notes, or the audio.</summary>
    public IReadOnlyList<AlignmentMethod> Methods => [.. new[] { Pattern, FirstNote, Audio }.OfType<AlignmentEstimate>().Select(e => e.Method)];

    /// <summary>The estimate <paramref name="method"/> gives.</summary>
    /// <exception cref="ArgumentException">The link doesn't line the songs up that way.</exception>
    public AlignmentEstimate Estimate(AlignmentMethod method) => method switch
    {
        AlignmentMethod.NotePattern => Pattern,
        AlignmentMethod.FirstNote => FirstNote,
        _ => Audio,
    } ?? throw new ArgumentException($"The link doesn't line the songs up by {method}.", nameof(method));

    /// <summary>For a part, the note pattern when it's <see cref="AlignmentEstimate.IsReliable"/>, otherwise the first notes; for audio, the audio.</summary>
    public AlignmentMethod PreferredMethod =>
        IsAudio ? AlignmentMethod.Audio : Pattern?.IsReliable == true ? AlignmentMethod.NotePattern : AlignmentMethod.FirstNote;

    /// <summary>The link the other way round: <see cref="From"/> lined up with <see cref="To"/>.</summary>
    public AlignmentLink Reversed() => new(To, From, PartId, Difficulty, FirstNote?.Reversed(), Pattern?.Reversed(), Audio?.Reversed());
}

/// <summary>What one song is lined up by: a link from a song nearer the one whose audio is kept, and the estimate of it used.</summary>
public sealed record AlignmentChoice(AlignmentLink Link, AlignmentMethod Method)
{
    /// <summary>How far the link moves the song, in milliseconds, from where the song it's lined up with is.</summary>
    public int OffsetMs => Link.Estimate(Method).OffsetMs;
}

/// <summary>
/// Works out how far to move each song being merged so that its chart lines up with the audio kept: by the parts the songs
/// share, each song lined up with one already lined up, starting from the song whose audio is kept.
/// </summary>
/// <remarks>
/// <para>Two songs that share a part are lined up by it two ways. The first notes: how far apart the part's first notes
/// are, which is right as long as both charts start at the same place in the song. And the note pattern: the offset at
/// which the most notes of the part start together, all through the song, which still holds when one chart starts
/// earlier, as with an intro only one charts, or has notes the other hasn't.</para>
/// <para>For the note pattern, every pair of notes (one of each song, of the same kind) within <see cref="MaxShiftMs"/> of
/// each other is counted in a histogram of how far apart they are, a millisecond to a bin, smoothed so that notes a few
/// milliseconds out still count. Its highest peak is the offset, refined to the notes near it. As music repeats, another
/// peak a beat or a bar away can come close: then the result is <see cref="AlignmentEstimate.IsAmbiguous"/>, and is
/// looked for again within half a beat of the first notes' offset. The start and the end of the song are then lined up
/// apart, to tell a song a different length or speed, which no one offset fixes.</para>
/// </remarks>
public static class SourceAligner
{
    /// <summary>The furthest apart two songs are looked for, either way, in milliseconds.</summary>
    public const int MaxShiftMs = 60_000;

    /// <summary>The fewest notes that must line up for a note pattern to be <see cref="AlignmentEstimate.IsReliable"/>.</summary>
    public const int MinMatchedNotes = 8;

    /// <summary>The smallest share of the notes that must line up for a note pattern to be <see cref="AlignmentEstimate.IsReliable"/>.</summary>
    public const double MinMatch = 0.5;

    /// <summary>How alike two songs' audio must be at their offset for it to be <see cref="AlignmentEstimate.IsReliable"/>, from 0 to 1.</summary>
    /// <remarks>The same recording comes out near 1, and another master of it well over half; different songs hardly ever reach a fifth.</remarks>
    public const double MinAudioMatch = 0.3;

    /// <summary>How much further, in milliseconds, the end of a song can line up than its start before it's <see cref="AlignmentEstimate.HasDrift"/>.</summary>
    public const int DriftToleranceMs = 10;

    // Notes this far apart, in milliseconds, count as together, falling away from there.
    private const double SmoothingMs = 6;
    private const int SmoothingRadius = 18;

    // A second peak at least this far from the first that comes within this share of it makes the result ambiguous.
    private const int SecondPeakMs = 40;
    private const double AmbiguousShare = 0.85;

    // Notes this close to the peak, in milliseconds, refine it.
    private const double RefineMs = 15;

    // How far either side of the offset the start and the end of the song are looked for, in milliseconds, for drift.
    private const int DriftSearchMs = 50;

    /// <summary>How far apart <paramref name="partId"/>'s first notes put the songs: how far to move <paramref name="to"/> to line it up with <paramref name="from"/>.</summary>
    /// <param name="difficulty">The difficulty to read both at; <see langword="null"/> for the vocals.</param>
    /// <exception cref="ArgumentException">A song doesn't have the part at that difficulty.</exception>
    public static AlignmentEstimate FirstNote(AlignmentChart from, AlignmentChart to, string partId, ChartDifficulty? difficulty)
    {
        var (a, b) = NotesOf(from, to, partId, difficulty);
        return new AlignmentEstimate(AlignmentMethod.FirstNote, Milliseconds(a.First - b.First));
    }

    /// <summary>
    /// How far apart <paramref name="partId"/>'s note pattern puts the songs: how far to move <paramref name="to"/> to line it
    /// up with <paramref name="from"/>, looked for within <see cref="MaxShiftMs"/>. If that's ambiguous, it's looked for again
    /// within half a beat of <paramref name="audioMs"/>, the songs' audio's offset, then of the first notes' offset: where it
    /// lines up there nearly as well as it can anywhere, that's the one of the peaks it is.
    /// </summary>
    /// <param name="audioMs">How far apart the songs' audio puts them, when that's reliable; it doesn't hang on how either is charted.</param>
    /// <inheritdoc cref="FirstNote"/>
    public static AlignmentEstimate NotePattern(AlignmentChart from, AlignmentChart to, string partId, ChartDifficulty? difficulty, int? audioMs = null)
    {
        var (a, b) = NotesOf(from, to, partId, difficulty);
        var estimate = Pattern(a, b, -MaxShiftMs, MaxShiftMs);
        if (estimate.IsAmbiguous)
        {
            var centers = new List<(int Center, AlignmentMethod By)>();
            if (audioMs is { } audio)
                centers.Add((audio, AlignmentMethod.Audio));
            centers.Add((Milliseconds(a.First - b.First), AlignmentMethod.FirstNote));

            int radius = Math.Max(SecondPeakMs + SmoothingRadius, (int)Math.Round(a.BeatSeconds * 500));
            foreach (var (center, by) in centers)
            {
                // Only a peak that was in the running: a worse one nearby is no answer, as where one chart has an intro the other
                // hasn't, the first notes are far from where the rest lines up.
                var narrowed = Pattern(a, b, center - radius, center + radius) with { NarrowedBy = by };
                if (narrowed.IsReliable && narrowed.Match >= estimate.Match * AmbiguousShare)
                {
                    estimate = narrowed;
                    break;
                }
            }
        }

        return estimate with { DriftMs = DriftOf(a, b, estimate.OffsetMs) };
    }

    /// <summary>
    /// Every link between two of <paramref name="charts"/> (by source; <see langword="null"/> for a song without one): one for
    /// each part two songs share, read at the hardest difficulty both have, each way round.
    /// </summary>
    /// <param name="audioLinks">
    /// The links between the songs' audio, if they've been worked out: where one is reliable, a note pattern that's ambiguous
    /// is looked for near it; see <see cref="NotePattern"/>.
    /// </param>
    /// <param name="cancellationToken">Stops working them out.</param>
    /// <exception cref="OperationCanceledException">It was stopped.</exception>
    public static IReadOnlyList<AlignmentLink> Links(IReadOnlyList<AlignmentChart?> charts, IReadOnlyList<AlignmentLink>? audioLinks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(charts);
        var links = new List<AlignmentLink>();
        for (int i = 0; i < charts.Count; i++)
        {
            for (int j = i + 1; j < charts.Count; j++)
            {
                if (charts[i] is not { } a || charts[j] is not { } b)
                    continue;

                foreach (string partId in a.PartIds.Intersect(b.PartIds))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ChartDifficulty? difficulty = null;
                    if (!a.IsVocal(partId))
                    {
                        var both = a.DifficultiesOf(partId).Intersect(b.DifficultiesOf(partId)).ToList();
                        if (both.Count == 0)
                            continue;
                        difficulty = both.Max();
                    }

                    int? audioMs = audioLinks?.FirstOrDefault(l => l.From == i && l.To == j && l.Audio is { IsReliable: true })?.Audio!.OffsetMs;
                    var link = new AlignmentLink(i, j, partId, difficulty, FirstNote(a, b, partId, difficulty), NotePattern(a, b, partId, difficulty, audioMs));
                    links.Add(link);
                    links.Add(link.Reversed());
                }
            }
        }

        return links;
    }

    /// <summary>
    /// The link each song is best lined up by, by source, starting from <paramref name="reference"/>: by the route of links
    /// that costs least, where a reliable note pattern costs least, then reliable audio, then first notes, and audio that
    /// isn't reliable most; between routes that cost the same, the one whose last link lines up best. The charts are what
    /// are being lined up, so a link between them comes first; audio lines up charts only as well as each is timed to its
    /// own audio. A song no link reaches isn't listed.
    /// </summary>
    public static IReadOnlyDictionary<int, AlignmentChoice> Solve(int reference, IReadOnlyList<AlignmentLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var cost = new Dictionary<int, (int Cost, double Match)> { [reference] = (0, 1) };
        var choices = new Dictionary<int, AlignmentChoice>();
        var done = new HashSet<int>();

        // Dijkstra's, over a handful of songs.
        while (cost.Keys.Where(k => !done.Contains(k)).OrderBy(k => cost[k].Cost).ThenByDescending(k => cost[k].Match).ThenBy(k => k).Cast<int?>().FirstOrDefault() is { } at)
        {
            done.Add(at);
            foreach (var link in links.Where(l => l.From == at && !done.Contains(l.To)))
            {
                var method = link.PreferredMethod;
                var estimate = link.Estimate(method);
                var reached = (Cost: cost[at].Cost + CostOf(estimate), estimate.Match);
                if (!cost.TryGetValue(link.To, out var best) || reached.Cost < best.Cost || (reached.Cost == best.Cost && reached.Match > best.Match))
                {
                    cost[link.To] = reached;
                    choices[link.To] = new AlignmentChoice(link, method);
                }
            }
        }

        return choices;
    }

    // What a link taken by an estimate adds to a route's cost for Solve.
    private static int CostOf(AlignmentEstimate estimate) => estimate.Method switch
    {
        AlignmentMethod.NotePattern => 2,
        AlignmentMethod.Audio when estimate.IsReliable => 3,
        AlignmentMethod.FirstNote => 4,
        _ => 8,
    };

    /// <summary>
    /// How far each song moves, in milliseconds, by source: <paramref name="reference"/> not at all; one with a choice as
    /// far as the song it's lined up with does, and the link's offset more; and each by its <paramref name="nudges"/> on
    /// top. A song lined up through itself, or through one no choice lines up, moves by its nudge alone.
    /// </summary>
    /// <param name="count">How many songs there are.</param>
    /// <param name="nudges">How far each song is moved by hand on top of its link, by source; the reference's is ignored.</param>
    public static int[] OffsetsOf(int count, int reference, IReadOnlyDictionary<int, AlignmentChoice> choices, IReadOnlyDictionary<int, int>? nudges = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var offsets = new int?[count];
        int Nudge(int source) => source == reference ? 0 : nudges?.GetValueOrDefault(source) ?? 0;

        // A song's choice only counts when it leads to the reference without coming back round, so this ends.
        int OffsetOf(int source)
        {
            if (offsets[source] is { } known)
                return known;
            if (source == reference)
                return (offsets[source] = 0).Value;

            int linked = 0;
            if (choices.TryGetValue(source, out var choice) && choice.Link.To == source && IsLinked(choice.Link.From, reference, choices, new HashSet<int> { source }))
                linked = OffsetOf(choice.Link.From) + choice.OffsetMs;
            return (offsets[source] = linked + Nudge(source)).Value;
        }

        for (int i = 0; i < count; i++)
            OffsetOf(i);
        return [.. offsets.Select(o => o ?? 0)];
    }

    /// <summary>
    /// Whether <paramref name="source"/> is <paramref name="reference"/>, or lined up with it through <paramref name="choices"/>
    /// without passing through any of <paramref name="avoiding"/>: what a song can be lined up with without going round in a circle.
    /// </summary>
    public static bool IsLinked(int source, int reference, IReadOnlyDictionary<int, AlignmentChoice> choices, IReadOnlySet<int> avoiding)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(avoiding);
        var seen = new HashSet<int>();
        for (int at = source; ; at = choices[at].Link.From)
        {
            if (avoiding.Contains(at) || !seen.Add(at))
                return false;
            if (at == reference)
                return true;
            if (!choices.ContainsKey(at))
                return false;
        }
    }

    /// <summary>The note pattern within <paramref name="lowMs"/> to <paramref name="highMs"/>; see <see cref="SourceAligner"/>.</summary>
    internal static AlignmentEstimate Pattern(AlignmentNotes a, AlignmentNotes b, int lowMs, int highMs)
    {
        int bins = highMs - lowMs + 1;
        var counts = new double[bins];
        ForEachPair(a, b, lowMs / 1000.0 - 0.0005, highMs / 1000.0 + 0.0005, d =>
        {
            int bin = (int)Math.Round(d * 1000, MidpointRounding.AwayFromZero) - lowMs;
            if (bin >= 0 && bin < bins)
                counts[bin]++;
        });

        Span<double> weights = stackalloc double[(SmoothingRadius * 2) + 1];
        for (int k = -SmoothingRadius; k <= SmoothingRadius; k++)
            weights[k + SmoothingRadius] = Math.Exp(-(k * k) / (2 * SmoothingMs * SmoothingMs));

        var smoothed = new double[bins];
        int peak = 0;
        for (int i = 0; i < bins; i++)
        {
            double sum = 0;
            for (int k = Math.Max(-SmoothingRadius, -i); k <= SmoothingRadius && i + k < bins; k++)
                sum += counts[i + k] * weights[k + SmoothingRadius];
            smoothed[i] = sum;
            if (sum > smoothed[peak])
                peak = i;
        }

        // The next highest peak of its own, not the first's shoulder: notes that line up a little loosely spread it wide.
        double best = smoothed[peak];
        double second = 0;
        for (int i = 0; i < bins; i++)
        {
            bool isPeak = (i == 0 || smoothed[i] >= smoothed[i - 1]) && (i == bins - 1 || smoothed[i] >= smoothed[i + 1]);
            if (isPeak && Math.Abs(i - peak) >= SecondPeakMs && smoothed[i] > second)
                second = smoothed[i];
        }

        // Refined to the pairs near the peak, each by how near it is.
        double peakSeconds = (peak + lowMs) / 1000.0;
        double weighted = 0, weight = 0;
        ForEachPair(a, b, peakSeconds - (RefineMs / 1000), peakSeconds + (RefineMs / 1000), d =>
        {
            double off = (d - peakSeconds) * 1000;
            double w = Math.Exp(-(off * off) / (2 * SmoothingMs * SmoothingMs));
            weighted += d * w;
            weight += w;
        });
        int offset = weight > 0 ? Milliseconds(weighted / weight) : peak + lowMs;

        int possible = a.ByKind.Zip(b.ByKind, (x, y) => Math.Min(x.Length, y.Length)).Sum();
        return new AlignmentEstimate(
            AlignmentMethod.NotePattern,
            offset,
            (int)Math.Round(Math.Min(best, possible)),
            possible > 0 ? Math.Min(1, best / possible) : 0,
            best > 0 && second >= best * AmbiguousShare);
    }

    /// <summary>How much further the last third of <paramref name="b"/>'s notes line up than its first third, near <paramref name="offsetMs"/>.</summary>
    private static int? DriftOf(AlignmentNotes a, AlignmentNotes b, int offsetMs)
    {
        double third = (b.Last - b.First) / 3;
        var start = Pattern(a, b.Between(b.First, b.First + third), offsetMs - DriftSearchMs, offsetMs + DriftSearchMs);
        var end = Pattern(a, b.Between(b.Last - third, b.Last), offsetMs - DriftSearchMs, offsetMs + DriftSearchMs);
        return start.Matched >= MinMatchedNotes && end.Matched >= MinMatchedNotes && start.Match >= MinMatch && end.Match >= MinMatch
            ? end.OffsetMs - start.OffsetMs
            : null;
    }

    /// <summary>
    /// Calls <paramref name="each"/> with how far apart, in seconds, each note of <paramref name="a"/> is after each of
    /// <paramref name="b"/> of the same kind, for those from <paramref name="low"/> to <paramref name="high"/> apart.
    /// </summary>
    private static void ForEachPair(AlignmentNotes a, AlignmentNotes b, double low, double high, Action<double> each)
    {
        for (int kind = 0; kind < Math.Min(a.ByKind.Length, b.ByKind.Length); kind++)
        {
            double[] x = a.ByKind[kind], y = b.ByKind[kind];
            int start = 0;
            foreach (double t in y)
            {
                while (start < x.Length && x[start] - t < low)
                    start++;
                for (int i = start; i < x.Length && x[i] - t <= high; i++)
                    each(x[i] - t);
            }
        }
    }

    private static (AlignmentNotes A, AlignmentNotes B) NotesOf(AlignmentChart from, AlignmentChart to, string partId, ChartDifficulty? difficulty)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(partId);
        return (from.NotesOf(partId, difficulty) ?? throw new ArgumentException($"The first song has no '{partId}' to line up by.", nameof(partId)),
            to.NotesOf(partId, difficulty) ?? throw new ArgumentException($"The second song has no '{partId}' to line up by.", nameof(partId)));
    }

    private static int Milliseconds(double seconds) => (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
}
