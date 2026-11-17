using System.Globalization;
using System.Text;
using ChartShared.Core.Charting;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Lyrics;

/// <summary>What an UltraStar song's header says of it, beside its timing.</summary>
/// <param name="Audio">The song's audio file's name, which the <c>.txt</c> is beside; null when it has none.</param>
/// <param name="Vocals">The vocals stem's file name, or null.</param>
public sealed record UltraStarSong(string Title, string Artist, string? Creator = null, string? Genre = null, string? Year = null, string? Audio = null, string? Vocals = null);

/// <summary>
/// Writes a vocal part, or two as a duet, as an UltraStar <c>.txt</c> song, pitches and all. Written from the format's
/// published description (see THIRD-PARTY-NOTICES), not from any implementation; <see cref="UltraStarParser"/> reads it back.
/// </summary>
/// <remarks>
/// <para>The header is format <see cref="Version"/>'s, with the song's audio as both <c>#AUDIO</c> and the older
/// <c>#MP3</c>, for players that don't know the new one. <c>#GAP</c> is the first note's time, so beat 0 is that note.
/// Every note starts and ends on a whole beat, <c>15 / BPM</c> seconds, so <c>#BPM</c> is chosen to put each start and
/// end within <see cref="MaxErrorSeconds"/> of where it is in the chart: the first whole multiple of the song's average
/// tempo, from its first note to its last, that does (<see cref="BpmFor"/>). Notes snapped to the chart's grid line up with a
/// small multiple; notes that aren't need a short enough beat. Each note's beats are worked out from its time in the
/// audio, so a tempo change doesn't add up to a drift.</para>
/// <para>A note is written at its pitch less 60, as the format counts from C4. A spoken note (<c>#</c> or <c>^</c>) is a
/// rap note, <c>R</c>, which the games score without its pitch; every other is a normal note, <c>:</c>. A slide's syllable,
/// or a note with no lyric, is <c>~</c>, which carries the syllable before on. The chart's symbols are taken off each
/// syllable (<see cref="LyricSyllable"/>), and one that ends a word is followed by a space.</para>
/// <para>Each phrase is a line, ended by a <c>-</c> at its end beat; notes outside every phrase are lines of their own. Of
/// two notes on a tick only the lowest is written, and each note starts at least a beat after the one before. A duet is
/// written as <c>P1</c> and <c>P2</c>, with one timing.</para>
/// </remarks>
public static class UltraStarWriter
{
    /// <summary>The version of the format written.</summary>
    public const string Version = "1.1.0";

    /// <summary>The furthest, in seconds, a note's start or end may be moved to put it on a beat.</summary>
    public const double MaxErrorSeconds = 0.010;

    // When nothing else gives a tempo.
    private const double DefaultBpm = 120;
    private const string Carry = "~";

    /// <summary>
    /// The UltraStar song of <paramref name="singers"/>' notes and lyrics: one part, or two as a duet. Empty when none of
    /// them has a note with a lyric.
    /// </summary>
    /// <param name="secondsOf">The time in the audio, in seconds, of a tick: the song's delay included.</param>
    /// <param name="resolution">The chart's ticks a quarter note, which its average tempo is counted in.</param>
    /// <exception cref="ArgumentException"><paramref name="singers"/> isn't one or two parts.</exception>
    public static string Write(IReadOnlyList<VocalTrackContent> singers, Func<long, double> secondsOf, int resolution, UltraStarSong song)
    {
        ArgumentNullException.ThrowIfNull(singers);
        ArgumentNullException.ThrowIfNull(secondsOf);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        if (singers.Count is not (1 or 2))
            throw new ArgumentException("An UltraStar song is sung by one singer, or two as a duet.", nameof(singers));

        var parts = singers.Select(Notes).ToList();
        if (!parts.Any(static p => p.Any(static n => n.Lyric is not null)))
            return string.Empty;

        var times = parts.Select(p => TimesOf(p, secondsOf)).ToList();
        long gapMs = (long)Math.Round(times.Where(static t => t.Length > 0).Min(static t => t[0].Start) * 1000, MidpointRounding.AwayFromZero);
        double gap = gapMs / 1000.0;
        var ticks = parts.SelectMany(static p => p).Select(static n => n.Tick).ToList();
        int bpm = BpmFor(AverageBpm(ticks.Min(), ticks.Max(), secondsOf, resolution), times, gap);

        var output = new StringBuilder();
        Header(output, "VERSION", Version);
        Header(output, "TITLE", song.Title);
        Header(output, "ARTIST", song.Artist);
        Header(output, "CREATOR", song.Creator);
        Header(output, "GENRE", song.Genre);
        Header(output, "YEAR", song.Year);
        Header(output, "MP3", song.Audio);
        Header(output, "AUDIO", song.Audio);
        Header(output, "VOCALS", song.Vocals);
        Header(output, "BPM", bpm.ToString(CultureInfo.InvariantCulture));
        Header(output, "GAP", gapMs.ToString(CultureInfo.InvariantCulture));

        for (int s = 0; s < parts.Count; s++)
        {
            if (parts.Count > 1)
                output.Append('P').Append(s + 1).Append('\n');
            Singer(output, parts[s], Place(times[s], bpm, gap), singers[s].Phrases, tick => Beat(secondsOf(tick), bpm, gap));
        }

        output.Append("E\n");
        return output.ToString();
    }

    /// <summary>
    /// The song's average tempo, in quarter notes a minute, from the tick <paramref name="first"/> to <paramref name="last"/>:
    /// its first note to its last, so a tempo change before the singing, as a chart has to fill its silence, doesn't count.
    /// With one note, the tempo at it.
    /// </summary>
    internal static double AverageBpm(long first, long last, Func<long, double> secondsOf, int resolution)
    {
        if (last <= first)
            last = first + resolution;
        double seconds = secondsOf(last) - secondsOf(first);
        return seconds > 0 ? (last - first) / (double)resolution / seconds * 60 : DefaultBpm;
    }

    /// <summary>
    /// The <c>#BPM</c> written: the first whole multiple of <paramref name="baseBpm"/>, to the nearest whole number, that puts
    /// every start and end of <paramref name="singers"/>' notes within <see cref="MaxErrorSeconds"/> of its time, beat 0 at
    /// <paramref name="gap"/>. No later than the first whose beat is short enough that rounding to one can't move a note
    /// further, which only notes less than a beat apart, moved apart, could.
    /// </summary>
    /// <param name="singers">Each singer's notes' start and end times, in seconds, each end no later than the next start.</param>
    internal static int BpmFor(double baseBpm, IReadOnlyList<(double Start, double End)[]> singers, double gap)
    {
        if (!double.IsFinite(baseBpm) || baseBpm <= 0)
            baseBpm = DefaultBpm;
        for (int k = 1; ; k++)
        {
            int bpm = (int)Math.Round(baseBpm * k, MidpointRounding.AwayFromZero);
            if (bpm < 1)
                continue;
            if (15.0 / bpm <= 2 * MaxErrorSeconds || singers.All(t => ErrorOf(t, Place(t, bpm, gap), bpm, gap) <= MaxErrorSeconds))
                return bpm;
        }
    }

    // Each note's start and end, the end no later than the next note's start, in seconds.
    private static (double Start, double End)[] TimesOf(List<(long Tick, long EndTick, int Pitch, string? Lyric)> notes, Func<long, double> secondsOf) =>
        [.. notes.Select((n, i) => (secondsOf(n.Tick), secondsOf(i + 1 < notes.Count ? Math.Min(n.EndTick, notes[i + 1].Tick) : n.EndTick)))];

    // The beat nearest a time, beat 0 at the gap.
    private static int Beat(double seconds, int bpm, double gap) => (int)Math.Round((seconds - gap) * bpm / 15, MidpointRounding.AwayFromZero);

    // Each note's start beat, after the one before's, and end beat, at least a beat after its start and not after the next's.
    private static (int[] Starts, int[] Ends) Place((double Start, double End)[] times, int bpm, double gap)
    {
        var starts = new int[times.Length];
        for (int i = 0; i < times.Length; i++)
            starts[i] = i > 0 ? Math.Max(Beat(times[i].Start, bpm, gap), starts[i - 1] + 1) : Beat(times[i].Start, bpm, gap);
        var ends = new int[times.Length];
        for (int i = 0; i < times.Length; i++)
        {
            ends[i] = Math.Max(Beat(times[i].End, bpm, gap), starts[i] + 1);
            if (i + 1 < times.Length)
                ends[i] = Math.Min(ends[i], starts[i + 1]);
        }

        return (starts, ends);
    }

    // How far the furthest start or end is from its beat, in seconds.
    private static double ErrorOf((double Start, double End)[] times, (int[] Starts, int[] Ends) beats, int bpm, double gap)
    {
        double worst = 0;
        for (int i = 0; i < times.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(gap + (beats.Starts[i] * 15.0 / bpm) - times[i].Start));
            worst = Math.Max(worst, Math.Abs(gap + (beats.Ends[i] * 15.0 / bpm) - times[i].End));
        }

        return worst;
    }

    // A part's notes, the lowest on each tick, each with the first lyric on its tick, or null.
    private static List<(long Tick, long EndTick, int Pitch, string? Lyric)> Notes(VocalTrackContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lyrics = new Dictionary<long, string>();
        foreach (var lyric in content.Lyrics)
            lyrics.TryAdd(lyric.Tick, lyric.Text);
        return [.. content.Notes
            .GroupBy(static n => n.Tick)
            .Select(static g => g.MinBy(static n => n.Pitch))
            .OrderBy(static n => n.Tick)
            .Select(n => (n.Tick, n.EndTick, n.Pitch, lyrics.GetValueOrDefault(n.Tick)))];
    }

    private static void Singer(
        StringBuilder output, List<(long Tick, long EndTick, int Pitch, string? Lyric)> notes, (int[] Starts, int[] Ends) beats,
        IEnumerable<VocalPhraseEvent> phrases, Func<long, int> beatOf)
    {
        var (starts, ends) = beats;
        var lines = PhraseGroups.By(Enumerable.Range(0, notes.Count), i => notes[i].Tick, phrases);
        for (int l = 0; l < lines.Count; l++)
        {
            var (phrase, line) = lines[l];
            foreach (int i in line)
            {
                var (kind, syllable) = Syllable(notes[i].Lyric);
                output.Append(CultureInfo.InvariantCulture, $"{kind} {starts[i]} {ends[i] - starts[i]} {notes[i].Pitch - UltraStarParser.PitchZero} {syllable}\n");
            }

            if (l + 1 < lines.Count)
            {
                // At the phrase's end, but not before its last note's, nor after the next line's first starts.
                int last = ends[line[^1]], next = starts[lines[l + 1].Items[0]];
                int at = phrase is { } p ? beatOf(p.EndTick) : last;
                output.Append(CultureInfo.InvariantCulture, $"- {Math.Clamp(at, last, Math.Max(last, next))}\n");
            }
        }
    }

    // The kind of note a lyric's is, and its syllable as written.
    private static (char Kind, string Syllable) Syllable(string? lyric)
    {
        if (lyric is null)
            return (':', Carry);
        var syllable = LyricSyllable.Of(lyric);
        char kind = syllable.IsUnpitched ? 'R' : ':';
        string text = syllable.Text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (syllable.IsSlide || text.Length == 0)
            return (kind, Carry);
        return (kind, syllable.JoinsNext ? text : text + " ");
    }

    private static void Header(StringBuilder output, string key, string? value)
    {
        string? text = value?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (!string.IsNullOrEmpty(text))
            output.Append('#').Append(key).Append(':').Append(text).Append('\n');
    }
}
