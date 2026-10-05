using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Merging;

/// <summary>A chart's MIDI file, and what places its notes against its audio.</summary>
/// <param name="Midi">The MIDI file.</param>
/// <param name="Delay">How much later than its ticks say the chart plays: song.ini's <c>delay</c>, or a <c>.chart</c>'s <c>Offset</c>.</param>
/// <param name="Options">How its notes are read: <c>pro_drums</c>, its sustain cutoff and HOPO threshold.</param>
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
        return (new MidiChart(conversion.Midi, delay, conversion.Options), conversion);
    }
}

/// <summary>A merged MIDI file.</summary>
/// <param name="Midi">The MIDI file.</param>
/// <param name="Warnings">Tracks whose notes didn't come out exactly as their source has them.</param>
/// <param name="SustainCutoff">
/// The <c>sustain_cutoff_threshold</c> the merged song.ini needs, when the default, a third of a beat,
/// would make some of its sustains notes.
/// </param>
public sealed record MidiMergeResult(byte[] Midi, IReadOnlyList<ChartMergeWarning> Warnings, long? SustainCutoff = null);

/// <summary>Combines the parts of several charts' MIDI files into one.</summary>
/// <remarks>
/// <para>The base chart gives the tempo map, the division, and every track that isn't a part (events,
/// venue, beat); each part's tracks come whole from the chart chosen for it.</para>
/// <para>A part from a chart with another tempo map or delay is placed by song time: each event goes
/// to the tick of the base chart that plays at the same moment against the audio, to the nearest
/// tick, so a sustain keeps its length in time. A part from a chart with the same tempo map and
/// delay is copied tick for tick.</para>
/// <para>A song has one sustain cutoff, so the merged chart takes the one its shortest sustain
/// needs, and any note its source reads differently from how it would read under that cutoff is
/// held for more or less time to read the same.</para>
/// <para>The result is read back and each modelled part compared with its source. Where moving to
/// another tempo map or division changes which notes are natural HOPOs, under any game's threshold,
/// force markers keep them as they were; anything else that changed is a warning.</para>
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

            // One track of each name: as YARG reads them, the last PART GUITAR or PART DRUMS, otherwise the first.
            var chosen = new Dictionary<string, ParsedTrack>(StringComparer.Ordinal);
            foreach (var track in file.Tracks)
            {
                if (track.Name is not { } name || !part.Has(name) || (chosen.ContainsKey(name) && !MidiLayout.OverridingTrackNames.Contains(name)))
                    continue;
                chosen[name] = track;
            }

            foreach (var track in file.Tracks.Where(t => t.Name is { } n && chosen.TryGetValue(n, out var c) && ReferenceEquals(c, t)))
                placed.Add(new PlacedTrack(track.Name!, Place(source.Midi, track.Events, map), source, map, GemsOf(track.Name!, source, file, track.Events)));
        }

        long? cutoff = SustainCutoff.For(baseFile.Division, placed.SelectMany(t => t.Gems.Sustains.Select(n => t.Messages[n.Off].Tick - t.Messages[n.On].Tick)).Cast<long?>().Min());
        foreach (var track in placed)
        {
            SustainCutoff.Normalize(track.Messages, track.Gems.Notes, track.Gems.Sustains.Contains, SustainCutoff.Of(baseFile.Division, cutoff));
            if (MidiLayout.TrackNames.ContainsKey(track.Name))
                MoveStarPower(track.Messages, track.Source.Options.MidiStarPowerPitch);
        }

        // As merged, the drums read as their source's song.ini has them, the HOPOs as the base's, and star power is on 116.
        var drums = parts.FirstOrDefault(p => p.Part.Id == "drums").Source?.Options ?? baseChart.Options;
        var readOptions = baseChart.Options with { ProDrums = drums.ProDrums, FiveLaneDrums = drums.FiveLaneDrums, SustainCutoff = cutoff, MultiplierNote = null };
        byte[] midi = Build(baseFile.Division, structural, placed);
        var docs = new Dictionary<MidiChart, ChartDocument>(ReferenceEqualityComparer.Instance);
        var warnings = new List<ChartMergeWarning>();

        for (int pass = 0; ; pass++)
        {
            var (_, _, chunks) = MidiFile.ReadChunks(midi);
            var tempo = MidiChartReader.TempoOf(midi, baseFile.Division, [.. chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.ReadEvents(midi, c))]);
            if (!tempo.SameAs(baseFile.Tempo))
                throw new ChartFormatException("The base chart keeps tempo changes in a part that isn't being kept, so its tempo map can't be.");

            var reads = NoteCheck.ReadEveryWay(midi, readOptions);
            var done = new HashSet<ChartInstrument>();
            bool fixedAny = false;
            warnings.Clear();

            foreach (var track in placed.Where(t => MidiLayout.TrackNames.ContainsKey(t.Name)).GroupBy(t => MidiLayout.TrackNames[t.Name]).Select(Read))
            {
                // Only the track the reader takes of each instrument needs checking.
                var instrument = MidiLayout.TrackNames[track.Name];
                if (!done.Add(instrument))
                    continue;

                if (!docs.TryGetValue(track.Source, out var sourceDoc))
                    docs[track.Source] = sourceDoc = ChartReader.Read(track.Source.Midi, ChartFiles.MidiName, track.Source.Options);

                // Where a note is, as song time in its source chart: the merged chart's time, less the delay difference.
                TimeSpan TimeOf(long tick) =>
                    TimeSpan.FromSeconds(Math.Max(0, baseFile.Tempo.SecondsAt(tick) + (baseChart.Delay - track.Source.Delay).TotalSeconds));

                foreach (var difficulty in sourceDoc.DifficultiesOf(instrument))
                {
                    var key = new ChartTrackKey(instrument, difficulty);
                    var expected = sourceDoc.Tracks[key].Notes
                        .Select(n => n with { Tick = track.Map(n.Tick), Length = n.Length > 0 ? track.Map(n.Tick + n.Length) - track.Map(n.Tick) : 0 })
                        .ToList();
                    var result = NoteCheck.Compare(expected, reads, key);
                    if (result.HopoFixes.Count > 0 && pass == 0)
                    {
                        track.Messages.AddRange(NoteCheck.ForceMarkers(ChartInstruments.KindOf(instrument), difficulty, result.HopoFixes));
                        fixedAny = true;
                    }

                    if (NoteCheck.WarningOf(result, pass, track.Name.ToUpperInvariant(), difficulty, TimeOf) is { } warning)
                        warnings.Add(warning);
                }
            }

            if (!fixedAny)
                return new MidiMergeResult(midi, [.. warnings], cutoff);
            midi = Build(baseFile.Division, structural, placed);
        }
    }

    /// <summary>The track of an instrument YARG reads: the last <c>PART GUITAR</c> or <c>PART DRUMS</c>, otherwise the first.</summary>
    private static PlacedTrack Read(IEnumerable<PlacedTrack> tracks) =>
        tracks.LastOrDefault(t => MidiLayout.OverridingTrackNames.Contains(t.Name)) ?? tracks.First();

    /// <summary>
    /// Puts a source's star power on 116, as the merged song.ini has no <c>multiplier_note</c>: from 103, where its
    /// song.ini had it there, leaving out what was on 116, which wasn't star power. Only for the tracks
    /// <see cref="MidiLayout.TrackNames"/> lists: vocals, harmonies and the pro tracks keep theirs on 116 whatever it says.
    /// </summary>
    private static void MoveStarPower(List<MidiMessage> messages, int starPowerPitch)
    {
        if (starPowerPitch == MidiLayout.StarPowerPitch)
            return;

        static bool IsNote(MidiMessage m, int pitch) => m.Bytes.Length >= 3 && (m.Status & 0xF0) is 0x80 or 0x90 && m.Bytes[1] == pitch;
        messages.RemoveAll(m => IsNote(m, MidiLayout.StarPowerPitch));
        for (int i = 0; i < messages.Count; i++)
        {
            if (IsNote(messages[i], starPowerPitch))
                messages[i] = messages[i] with { Bytes = [messages[i].Bytes[0], MidiLayout.StarPowerPitch, .. messages[i].Bytes[2..]] };
        }
    }

    /// <summary>
    /// The gems of the track <paramref name="name"/> of <paramref name="source"/>, if it's a fret
    /// track, as its messages placed by <see cref="Place"/> have them, and the ones its source reads as sustains.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="SustainCutoff.Held"/> indexes are into the <see cref="Place"/>d message list.
    /// They are computed on an identity-placed list but applied to the map-placed <see cref="PlacedTrack.Messages"/>;
    /// that is only valid because <see cref="Place"/> preserves event order and count for any map.
    /// </remarks>
    private static TrackGems GemsOf(string name, MidiChart source, ParsedFile file, List<MidiEvent> events)
    {
        if (!MidiLayout.TrackNames.TryGetValue(name, out var instrument))
            return TrackGems.None;
        var kind = ChartInstruments.KindOf(instrument);
        if (kind == ChartInstrumentKind.Drums)
            return TrackGems.None;

        // Placed where they are, the messages are those Place gives in the same order, with their own ticks.
        // Do not filter or reorder here: the Held indexes below must stay aligned with Place(..., map) in Merge.
        var original = Place(source.Midi, events, tick => tick);
        bool opens = original.Any(m => m.Bytes is [0xFF, 0x01 or (>= 0x04 and <= 0x09), ..] && ChartTextEvents.Normalize(System.Text.Encoding.Latin1.GetString(m.Bytes.AsSpan(3))) == "ENHANCED_OPENS");
        var notes = SustainCutoff.NotesOf(original, SustainCutoff.GemPitches(kind, opens));
        long cutoff = SustainCutoff.Of(file.Division, source.Options.SustainCutoff);
        return new TrackGems(notes, notes.Where(n => original[n.Off].Tick - original[n.On].Tick >= cutoff).ToHashSet());
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
        [.. events
            .Where(e => !IsTempoOrEnd(midi, e))
            .Select(e => MidiMessage.Of(midi, e) with { Tick = IsName(midi, e) ? 0 : map(e.Tick) })];

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
                return new ParsedTrack(c, events, MidiChartReader.NameOf(midi, events));
            })
            .ToList();
        return new ParsedFile(division, tracks, MidiChartReader.TempoOf(midi, division, [.. tracks.Select(t => t.Events)]));
    }

    private sealed record ParsedTrack(MidiChunk Chunk, List<MidiEvent> Events, string? Name);

    private sealed record ParsedFile(int Division, List<ParsedTrack> Tracks, TempoMap Tempo);

    private sealed record PlacedTrack(string Name, List<MidiMessage> Messages, MidiChart Source, Func<long, long> Map, TrackGems Gems);

    /// <summary>A placed track's gems, and the ones that are sustains.</summary>
    private sealed record TrackGems(IReadOnlyList<SustainCutoff.Held> Notes, IReadOnlySet<SustainCutoff.Held> Sustains)
    {
        public static TrackGems None { get; } = new([], new HashSet<SustainCutoff.Held>());
    }
}
