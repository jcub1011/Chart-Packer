using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Merging;

/// <summary>A chart's MIDI file, and what places its notes against its audio.</summary>
/// <param name="Midi">The MIDI file.</param>
/// <param name="Delay">How much later than its ticks say the chart plays: song.ini's <c>delay</c>, or a <c>.chart</c>'s <c>Offset</c>.</param>
/// <param name="Options">How its notes are read: <c>pro_drums</c>.</param>
public sealed record MidiChart(byte[] Midi, TimeSpan Delay, ChartReadOptions Options)
{
    /// <summary>
    /// The chart <paramref name="bytes"/>, stored as <paramref name="chartName"/>, as a MIDI file to
    /// merge from: a <c>.chart</c> converted, with its delay and <c>pro_drums</c> worked out from the
    /// song's metadata <paramref name="pairs"/>.
    /// </summary>
    /// <returns>The chart, and the conversion when it was a <c>.chart</c>.</returns>
    /// <exception cref="ChartFormatException">The chart is malformed, or can't be written as MIDI.</exception>
    internal static (MidiChart Chart, ChartConversion? Conversion) Load(byte[] bytes, string chartName, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var options = ChartReadOptions.From(pairs);
        var original = ChartReader.Read(bytes, chartName, options);
        var delay = SongChartSettings.DelayOf(pairs, original);
        if (ChartFiles.StoredNameFor(chartName) != ChartFiles.ChartName)
            return (new MidiChart(bytes, delay, options), null);

        var conversion = ChartToMidi.Convert(bytes, options);
        return (new MidiChart(conversion.Midi, delay, new ChartReadOptions(conversion.ProDrums)), conversion);
    }
}

/// <summary>A merged MIDI file.</summary>
/// <param name="Midi">The MIDI file.</param>
/// <param name="Warnings">Tracks whose notes didn't come out exactly as their source has them.</param>
public sealed record MidiMergeResult(byte[] Midi, IReadOnlyList<ChartMergeWarning> Warnings);

/// <summary>Combines the parts of several charts' MIDI files into one.</summary>
/// <remarks>
/// <para>The base chart gives the tempo map, the division, and every track that isn't a part (events,
/// venue, beat); each part's tracks come whole from the chart chosen for it.</para>
/// <para>A part from a chart with another tempo map or delay is placed by song time: each event goes
/// to the tick of the base chart that plays at the same moment against the audio, to the nearest
/// tick, so a sustain keeps its length in time. A part from a chart with the same tempo map and
/// delay is copied tick for tick.</para>
/// <para>The result is read back and each modelled part compared with its source. Where moving to
/// another tempo map or division changes which notes are natural HOPOs, force markers keep them as
/// they were; anything else that changed is a warning.</para>
/// </remarks>
public static class MidiMerger
{
    /// <summary>Merges <paramref name="parts"/>, each from its chart, onto <paramref name="baseChart"/>.</summary>
    /// <param name="baseChart">The chart whose tempo map, events and other tracks are kept.</param>
    /// <param name="parts">The parts to take, and the chart to take each from. Any part not listed is left out, the base chart's too.</param>
    /// <exception cref="ChartFormatException">A chart is malformed, or the result didn't check out.</exception>
    public static MidiMergeResult Merge(MidiChart baseChart, IReadOnlyList<(MidiChart Source, MidiPart Part)> parts)
    {
        ArgumentNullException.ThrowIfNull(baseChart);
        ArgumentNullException.ThrowIfNull(parts);

        var baseFile = Parse(baseChart.Midi);
        var structural = baseFile.Tracks
            .Where(t => MidiPartCatalog.PartOf(t.Name) is null)
            .Select(t => baseChart.Midi[t.Chunk.Start..t.Chunk.End])
            .ToList();

        var placed = new List<PlacedTrack>();
        foreach (var (source, part) in parts.OrderBy(p => MidiPartCatalog.OrderOf(p.Part)).ThenBy(p => p.Part.Id, StringComparer.Ordinal))
        {
            var file = ReferenceEquals(source, baseChart) ? baseFile : Parse(source.Midi);
            var map = MapOf(file.Tempo, source.Delay, baseFile.Tempo, baseChart.Delay);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in file.Tracks)
            {
                if (track.Name is not { } name || !part.Has(name) || !seen.Add(name))
                    continue;
                placed.Add(new PlacedTrack(name, Place(source.Midi, track.Events, map), source, map));
            }
        }

        var readOptions = parts.FirstOrDefault(p => p.Part.Id == "drums").Source?.Options ?? baseChart.Options;
        byte[] midi = Build(baseFile.Division, structural, placed);
        var docs = new Dictionary<MidiChart, ChartDocument>(ReferenceEqualityComparer.Instance);
        var warnings = new List<ChartMergeWarning>();

        for (int pass = 0; ; pass++)
        {
            var (_, _, chunks) = MidiFile.ReadChunks(midi);
            var tempo = MidiChartReader.TempoOf(midi, baseFile.Division, chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.ReadEvents(midi, c)).ToList());
            if (!tempo.SameAs(baseFile.Tempo))
                throw new ChartFormatException("The base chart keeps tempo changes in a part that isn't being kept, so its tempo map can't be.");

            var merged = ChartReader.Read(midi, ChartFiles.MidiName, readOptions);
            var done = new HashSet<ChartInstrument>();
            bool fixedAny = false;
            warnings.Clear();

            foreach (var track in placed)
            {
                // The reader takes the first track of each instrument, so only that one needs checking.
                if (!MidiLayout.TrackNames.TryGetValue(track.Name, out var instrument) || !done.Add(instrument))
                    continue;

                if (!docs.TryGetValue(track.Source, out var sourceDoc))
                    docs[track.Source] = sourceDoc = ChartReader.Read(track.Source.Midi, ChartFiles.MidiName, track.Source.Options);

                foreach (var difficulty in sourceDoc.DifficultiesOf(instrument))
                {
                    var key = new ChartTrackKey(instrument, difficulty);
                    var expected = sourceDoc.Tracks[key].Notes
                        .Select(n => n with { Tick = track.Map(n.Tick), Length = n.Length > 0 ? track.Map(n.Tick + n.Length) - track.Map(n.Tick) : 0 })
                        .ToList();
                    var result = NoteCheck.Compare(expected, merged.Tracks.GetValueOrDefault(key)?.Notes);
                    if (result.HopoFixes.Count > 0 && pass == 0)
                    {
                        track.Messages.AddRange(NoteCheck.ForceMarkers(ChartInstruments.KindOf(instrument), difficulty, result.HopoFixes));
                        fixedAny = true;
                    }

                    var issue = result.Issue;
                    if (result.HopoFixes.Count > 0 && pass > 0 && !(issue >= ChartMergeIssue.MarkingsChanged))
                        issue = ChartMergeIssue.MarkingsChanged;
                    if (issue is { } found)
                        warnings.Add(new ChartMergeWarning(found, track.Name.ToUpperInvariant(), difficulty));
                }
            }

            if (!fixedAny)
                return new MidiMergeResult(midi, [.. warnings]);
            midi = Build(baseFile.Division, structural, placed);
        }
    }

    /// <summary>
    /// The tick of <paramref name="to"/> that plays, against the audio, when <paramref name="tick"/>
    /// of <paramref name="from"/> does; to the nearest tick, and never before the start.
    /// </summary>
    public static long Retime(long tick, TempoMap from, TimeSpan fromDelay, TempoMap to, TimeSpan toDelay)
    {
        double seconds = from.SecondsAt(tick) + (fromDelay - toDelay).TotalSeconds;
        double placed = Math.Round(to.TickAt(seconds), MidpointRounding.AwayFromZero);
        return placed <= 0 ? 0 : (long)placed;
    }

    /// <summary>
    /// Moves ticks of <paramref name="from"/> to the ticks of <paramref name="to"/> that play at the same
    /// moment; see <see cref="Retime"/>. Tick for tick when the tempo maps and delays are the same.
    /// </summary>
    internal static Func<long, long> MapOf(TempoMap from, TimeSpan fromDelay, TempoMap to, TimeSpan toDelay) =>
        from.SameAs(to) && fromDelay == toDelay
            ? tick => tick
            : tick => Retime(tick, from, fromDelay, to, toDelay);

    /// <summary>
    /// The track <paramref name="events"/> of <paramref name="midi"/> as messages for another file:
    /// placed by <paramref name="map"/>, the track's name at the start, and its tempo, time-signature
    /// and end-of-track events left out.
    /// </summary>
    internal static List<MidiMessage> Place(byte[] midi, List<MidiEvent> events, Func<long, long> map) =>
        events
            .Where(e => !IsTempoOrEnd(midi, e))
            .Select(e => MidiMessage.Of(midi, e) with { Tick = IsName(midi, e) ? 0 : map(e.Tick) })
            .ToList();

    private static byte[] Build(int division, List<byte[]> structural, List<PlacedTrack> placed) =>
        MidiWriter.WriteFile(division, [.. structural, .. placed.Select(t => MidiWriter.Track(t.Messages))]);

    private static bool IsName(byte[] bytes, MidiEvent e) => e.IsMeta && bytes[e.DataStart] == 0x03;

    // A part's own tempo events would change the merged chart's tempo map, so they stay behind.
    private static bool IsTempoOrEnd(byte[] bytes, MidiEvent e) => e.IsMeta && bytes[e.DataStart] is 0x51 or 0x58 or MidiFile.EndOfTrack;

    private static ParsedFile Parse(byte[] midi)
    {
        var (_, division, chunks) = MidiFile.ReadChunks(midi);
        var tracks = chunks
            .Where(c => c.Id == "MTrk")
            .Select(c =>
            {
                var events = MidiFile.ReadEvents(midi, c);
                return new ParsedTrack(c, events, MidiFile.NameOf(midi, events));
            })
            .ToList();
        return new ParsedFile(division, tracks, MidiChartReader.TempoOf(midi, division, tracks.Select(t => t.Events).ToList()));
    }

    private sealed record ParsedTrack(MidiChunk Chunk, List<MidiEvent> Events, string? Name);

    private sealed record ParsedFile(int Division, List<ParsedTrack> Tracks, TempoMap Tempo);

    private sealed record PlacedTrack(string Name, List<MidiMessage> Messages, MidiChart Source, Func<long, long> Map);
}
