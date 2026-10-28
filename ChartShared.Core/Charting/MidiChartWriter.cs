using System.Globalization;
using System.Numerics;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;
using Serilog;

namespace ChartShared.Core.Charting;

/// <summary>
/// A chart written by <see cref="MidiChartWriter.WriteChecked"/>, and how it read back (charter-spec §4.3). A save goes
/// ahead only when it's <see cref="IsValid"/>.
/// </summary>
/// <param name="Bytes">The <c>notes.mid</c>.</param>
/// <param name="Saved">The chart the bytes hold: the chart written, normalized (<see cref="MidiChartWriter.Normalize"/>).</param>
/// <param name="ModelDifferences">How <see cref="MidiChartLoader"/> read the bytes back other than as <paramref name="Saved"/>.</param>
/// <param name="GameDifferences">
/// How <see cref="ChartReader"/> read them other than as <paramref name="Saved"/>'s game view, at any of the song's
/// HOPO thresholds: each threshold's, without one an earlier threshold found again.
/// </param>
/// <param name="ReloadIssues">
/// What <see cref="MidiChartLoader"/> couldn't take in as written when it read them back. A clean write has none.
/// </param>
public sealed record ChartWriteResult(
    byte[] Bytes,
    EditableChart Saved,
    IReadOnlyList<ModelDifference> ModelDifferences,
    IReadOnlyList<ReadBackDifference> GameDifferences,
    IReadOnlyList<ChartLoadIssue> ReloadIssues)
{
    /// <summary>Whether the bytes read back as <see cref="Saved"/>, both ways, with no issues.</summary>
    public bool IsValid => ModelDifferences.Count == 0 && GameDifferences.Count == 0 && ReloadIssues.Count == 0;
}

/// <summary>
/// Writes an <see cref="EditableChart"/> as a <c>notes.mid</c> (charter-spec §4.3): a format-1 file whose division is
/// the chart's resolution, laid out as §7 and <see cref="MidiLayout"/> say, and read back to check it.
/// </summary>
/// <remarks>
/// <para>The tracks, in order:</para>
/// <list type="number">
/// <item>The tempo track, named after the song: its name first, then at each tick the time signatures (with their
/// metronome bytes), the tempos, the anchor as <c>[chartpacker_anchor &lt;µs&gt;]</c>, and its raw events, each in the
/// model's order.</item>
/// <item>The passthrough tracks that must come before the parts (below).</item>
/// <item>Each part, in <see cref="ChartInstrument"/> order, under its current name (a <c>T1 GEMS</c> is saved as
/// <c>PART GUITAR</c>), its events in <see cref="PartEventOrder"/>. Phrases, tom markers and chord markings are
/// written only where the instrument's layout has a place for them: rolls, trills and tom markers on drums, force and
/// tap markings on fret parts. Anything else is left out, so the read-back check reports it.</item>
/// <item><c>EVENTS</c>: its name, then its text events and raw events.</item>
/// <item><c>BEAT</c>, made from the tempo map: pitch 12 on each measure's downbeat and 13 on the other beats, each a
/// sixteenth note long (a quarter of a beat), cut short where beats come closer, from tick 0 through the last tick of any
/// other track in the file.</item>
/// <item>The other passthrough chunks, byte for byte, in their order.</item>
/// </list>
/// <para>
/// <b>Duplicate tracks.</b> The reader keeps the first track of an instrument, unless a later one is named
/// <c>PART GUITAR</c> or <c>PART DRUMS</c>, which replaces it. So a passthrough track the reader would take for a part
/// the chart has goes where the written part still wins: one with one of those names before the parts, right after
/// the tempo track, and another (a second <c>PART BASS</c>, say, or a second <c>EVENTS</c>) after them. The passthrough
/// tracks keep their order, the ones before the last that must come first going first with it, except that one that
/// must come after the parts can't come before them (<see cref="Normalize"/>).
/// </para>
/// <para>
/// Text is written with <see cref="MidiFile.EncodeText"/>: Latin-1 when every character is in it, otherwise UTF-8.
/// <see cref="MidiFile.DecodeText"/> reads both back to the same text.
/// </para>
/// </remarks>
public static class MidiChartWriter
{
    // Within PartEventOrder.Text: the part's settings, then its text events, then its raw events.
    private const int SettingKey = 0;
    private const int TextKey = 1;
    private const int RawKey = 2;

    private const int DownbeatPitch = 12;
    private const int BeatPitch = 13;

    private const string EnhancedOpensText = "ENHANCED_OPENS";
    private const string ChartDynamicsText = "ENABLE_CHART_DYNAMICS";

    // How many differences the error logged names.
    private const int DifferencesNamed = 5;

    private static ILogger Logger => Log.ForContext(typeof(MidiChartWriter));

    /// <summary>
    /// A copy of <paramref name="chart"/> as a save holds it: what <see cref="Write"/> writes, and what the bytes read
    /// back as. Normalizing a normalized chart changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>What MIDI can't hold, or the games read otherwise, is made what the file will hold:</para>
    /// <list type="bullet">
    /// <item>A note held into the next note on its pitch (the same difficulty and lane, a kick and a 2x kick being two
    /// pitches) is cut short there, as a second note-on while a note is held is ignored.</item>
    /// <item>A hold shorter than <see cref="EditableNote.MinimumSustainLength"/> is made a plain note.</item>
    /// <item>Of a kick and a 2x kick on one tick, the games keep the one let go first: the shorter, or of two as long the
    /// 2x kick. The other is left out.</item>
    /// <item>Overlapping phrases on the same pitches merge into one, from the first's start to the last end: star power,
    /// solos, rolls and trills each with their own kind, and drum fills with big rock endings, which share pitches 120 to
    /// 124, taking the first's kind. A roll or trill merged reaches down to the easiest difficulty any of them reached,
    /// so no difficulty loses a lane it had. Two that start on one tick overlap, however long they are; one that
    /// starts where another ends doesn't.</item>
    /// <item>Overlapping tom markers on one pad merge the same way. 4-lane drums' tom markers are left out, as the games
    /// would read any as Pro drums (charter-spec C6 item 9).</item>
    /// <item>A chord marking on a tick without a note of its difficulty, which marks nothing, is left out.</item>
    /// <item><c>[ENABLE_CHART_DYNAMICS]</c> is on while a drum note is an accent or a ghost, and off when none is
    /// (charter-spec C6). <c>[ENHANCED_OPENS]</c> is on for five-fret parts that had it or have an open note, which needs
    /// it, and off for the other parts.</item>
    /// <item>A part's text event that the loader takes for a setting or a mix becomes one: <c>[ENHANCED_OPENS]</c> on
    /// five-fret parts, <c>[ENABLE_CHART_DYNAMICS]</c> on drums (which follows the notes), and a mix spelt as
    /// <see cref="DrumMix.TextFor"/> spells one. A text event of a meta type that isn't text (a track name, say) becomes a
    /// raw event.</item>
    /// <item>A chart without a name gets an empty one, which the tempo track is written with, and a chart without drums
    /// has no drum variant.</item>
    /// <item>The passthrough tracks are put in the order they're written in (see <see cref="MidiChartWriter"/>), which is
    /// theirs unless a track that must come after the parts was before one that must come before them.</item>
    /// </list>
    /// <para>
    /// What the author (or a command) got wrong is left as it is, for the read-back check to report: a phrase, tom marker
    /// or chord marking on an instrument with no place for it; a drum fill at or after <c>EVENTS</c>' first
    /// <c>[coda]</c>, or a drums big rock ending before it, which read back as each other; an anchor at a tick without a
    /// tempo marker; a raw event that would read back as something else (a text event, a note the model reads); a time
    /// signature the games can't read; drums without a variant; and a passthrough track the reader takes for a part the chart doesn't have.
    /// </para>
    /// </remarks>
    public static EditableChart Normalize(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var saved = chart.Clone();
        var changes = new Changes();
        saved.Name ??= string.Empty;
        if (saved.PartOf(ChartInstrument.Drums) is null)
            saved.DrumsVariant = null;
        changes.Texts += RawTexts(saved.Events, saved.EventsRaw);
        foreach (var part in saved.Parts)
            NormalizePart(part, saved.DrumsVariant, changes);
        changes.Passthrough = OrderPassthrough(saved);

        if (changes.Any)
        {
            Logger.Debug(
                "Normalizing the chart {Name} cut {Notes} notes, made {Sustains} short sustains plain, left out {Kicks} kicks and {Markings} chord markings, merged {Phrases} phrases and {TomMarkers} tom markers, moved {Texts} text events and reordered the passthrough tracks: {Reordered}",
                saved.Name, changes.Notes, changes.Sustains, changes.Kicks, changes.Markings, changes.Phrases, changes.TomMarkers, changes.Texts, changes.Passthrough);
        }

        return saved;
    }

    /// <summary>
    /// The <c>notes.mid</c> of <paramref name="chart"/>, as it is: the bytes of its <see cref="Normalize"/>d copy, which
    /// is the chart they read back as. The chart itself isn't changed.
    /// </summary>
    /// <exception cref="ChartFormatException">The chart's resolution is too fine for MIDI, or an event is too late.</exception>
    public static byte[] Write(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return WriteSaved(Normalize(chart));
    }

    /// <summary>
    /// Writes <paramref name="chart"/> as <see cref="Write"/> does, and reads the bytes back both ways (charter-spec
    /// §4.3): with <see cref="MidiChartLoader"/>, which must give the normalized chart with no issues, and with
    /// <see cref="ChartReader"/> at each HOPO threshold <paramref name="after"/> can be played with
    /// (<see cref="ChartReadOptions.MidiHopoThresholds"/>), which must give its <see cref="GameView"/> for each of its
    /// parts. Any difference is logged as an error.
    /// </summary>
    /// <param name="after">
    /// The song.ini settings that apply once the chart is saved, as <see cref="ChartReadOptions.From"/> reads them:
    /// with <c>multiplier_note</c> cleared, as star power is written on 116, and with <c>pro_drums</c> and
    /// <c>five_lane_drums</c> saying the chart's drum variant. <see cref="SettingsAfterSave"/> makes them from the
    /// song's.
    /// </param>
    /// <exception cref="ChartFormatException">The chart can't be written (see <see cref="Write"/>).</exception>
    public static ChartWriteResult WriteChecked(EditableChart chart, ChartReadOptions after)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(after);

        var saved = Normalize(chart);
        byte[] bytes = WriteSaved(saved);
        var reload = MidiChartLoader.Load(bytes, after);
        var model = ChartModelComparer.Differences(saved, reload.Chart);

        var instruments = saved.Parts.Select(p => p.Instrument).ToList();
        var thresholds = after.MidiHopoThresholds(saved.Resolution);
        var game = new List<ReadBackDifference>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (long threshold in thresholds)
        {
            var options = after with { MidiHopoTicks = threshold };
            var read = ChartReader.Read(bytes, ChartFiles.MidiName, options);
            foreach (var difference in ChartReadBack.Compare(GameView.Project(saved, options), read, instruments))
            {
                if (found.Add(KeyOf(difference)))
                    game.Add(difference);
            }
        }

        var result = new ChartWriteResult(bytes, saved, model, game, reload.Issues);
        if (result.IsValid)
        {
            Logger.Debug("The chart {Name} read back as written: {Bytes} bytes, read at {Thresholds} HOPO thresholds", saved.Name, bytes.Length, thresholds.Length);
        }
        else
        {
            string first = string.Join(", ", Named(result).Take(DifferencesNamed));
            Logger.Error(
                "The chart {Name} didn't read back as written: {ModelDifferences} model differences, {GameDifferences} game differences and {ReloadIssues} reload issues: {First}",
                saved.Name, model.Count, game.Count, reload.Issues.Count, first);
        }

        return result;
    }

    /// <summary>
    /// The settings a song's chart is read with once <paramref name="chart"/> is saved, from <paramref name="song"/>'s:
    /// <c>multiplier_note</c> cleared, as star power is written on 116 (charter-spec §7.2), and <c>pro_drums</c> and
    /// <c>five_lane_drums</c> set for the chart's drum variant, when it has one (C17 item 2).
    /// </summary>
    public static ChartReadOptions SettingsAfterSave(EditableChart chart, ChartReadOptions song)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(song);
        var after = song with { MultiplierNote = null };
        return chart.DrumsVariant switch
        {
            DrumsVariant.FourLane => after with { ProDrums = false, FiveLaneDrums = false },
            DrumsVariant.Pro => after with { ProDrums = true, FiveLaneDrums = false },
            DrumsVariant.FiveLane => after with { FiveLaneDrums = true },
            _ => after,
        };
    }

    /// <summary>The bytes of <paramref name="saved"/>, which is normalized.</summary>
    private static byte[] WriteSaved(EditableChart saved)
    {
        if (saved.Resolution > short.MaxValue)
            throw new ChartFormatException("The chart's resolution is too fine for a MIDI file.");

        var tempo = TempoTrack(saved);
        var parts = saved.Parts.Select(PartTrack).ToList();
        var events = EventsTrack(saved);
        long last = Math.Max(tempo.LastTick, events.LastTick);
        foreach (var part in parts)
            last = Math.Max(last, part.LastTick);
        foreach (var track in saved.Passthrough)
            last = Math.Max(last, LastTickOf(track));
        var beat = BeatTrack(saved.TempoMap.ToTempoMap(), last);

        int front = FrontCount(saved);
        var chunks = new List<byte[]>(parts.Count + saved.Passthrough.Count + 3) { tempo.Chunk() };
        chunks.AddRange(saved.Passthrough.Take(front).Select(p => p.Array));
        chunks.AddRange(parts.Select(p => p.Chunk()));
        chunks.Add(events.Chunk());
        chunks.Add(beat.Chunk());
        chunks.AddRange(saved.Passthrough.Skip(front).Select(p => p.Array));

        byte[] bytes = MidiWriter.WriteFile(saved.Resolution, chunks);
        Logger.Debug(
            "Wrote the chart {Name}: {Bytes} bytes, {Parts} parts and {Passthrough} passthrough chunks, {Front} of them before the parts",
            saved.Name, bytes.Length, parts.Count, saved.Passthrough.Count, front);
        return bytes;
    }

    // Writing.

    /// <summary>The tempo track: its name, then at each tick its time signatures, tempos, anchor and raw events.</summary>
    private static TrackEvents TempoTrack(EditableChart chart)
    {
        var track = new TrackEvents(chart.Name ?? string.Empty);
        var map = chart.TempoMap;
        foreach (var signature in map.TimeSignatures)
        {
            byte[] data = [(byte)signature.Numerator, (byte)BitOperations.Log2((uint)signature.Denominator), signature.ClocksPerClick, signature.ThirtySecondsPerQuarter];
            track.Add(signature.Tick, PartEventOrder.Text, 0, MidiMessage.Meta(signature.Tick, 0x58, data).Bytes);
        }

        foreach (var tempo in map.Tempos)
        {
            long us = tempo.MicrosecondsPerQuarter;
            track.Add(tempo.Tick, PartEventOrder.Text, 1, MidiMessage.Meta(tempo.Tick, 0x51, [(byte)(us >> 16), (byte)(us >> 8), (byte)us]).Bytes);
        }

        foreach (var anchor in map.Anchors)
            track.Add(anchor.Tick, PartEventOrder.Text, 2, Text(anchor.Tick, TextEvent.TextType, MidiChartLoader.AnchorTextOf(anchor.AudioMicroseconds)));
        foreach (var raw in chart.TempoTrackRaw)
            track.Add(raw.Tick, PartEventOrder.Text, 3, raw.Array);
        return track;
    }

    /// <summary>A part's track, its events in <see cref="PartEventOrder"/>.</summary>
    private static TrackEvents PartTrack(EditablePart part)
    {
        var track = new TrackEvents(ChartToMidi.TrackNames[part.Instrument]);
        var kind = part.Kind;
        bool drums = kind == ChartInstrumentKind.Drums;
        if (part.EnhancedOpens)
            track.Add(0, PartEventOrder.Text, SettingKey, Text(0, TextEvent.TextType, $"[{EnhancedOpensText}]"));
        if (part.ChartDynamics)
            track.Add(0, PartEventOrder.Text, SettingKey, Text(0, TextEvent.TextType, $"[{ChartDynamicsText}]"));

        foreach (var difficulty in part.Tracks)
        {
            int b = MidiLayout.BaseOf(kind, difficulty.Difficulty);
            foreach (var note in difficulty.Notes)
                track.Note(note.Tick, note.Length, PitchOf(kind, b, note), drums && part.ChartDynamics ? MidiLayout.DrumVelocityOf(note.Dynamics.ToFlags()) : MidiLayout.NormalVelocity);

            if (drums)
            {
                foreach (var mix in difficulty.Mixes)
                    track.Add(mix.Tick, PartEventOrder.Mix, (int)difficulty.Difficulty, Text(mix.Tick, TextEvent.TextType, mix.TextFor(difficulty.Difficulty)));
                continue;
            }

            // A marking covers its chord's tick alone.
            foreach (var chord in difficulty.FlaggedChords)
            {
                if (chord.Flags.HasFlag(ChordFlags.ForceHopo))
                    track.Note(chord.Tick, 1, b + MidiLayout.ForceHopoOffset(kind));
                if (chord.Flags.HasFlag(ChordFlags.ForceStrum))
                    track.Note(chord.Tick, 1, b + MidiLayout.ForceStrumOffset(kind));
                if (chord.Flags.HasFlag(ChordFlags.Tap))
                    track.PhaseShift(chord.Tick, (int)difficulty.Difficulty, MidiLayout.SysexTap);
            }
        }

        foreach (var phrase in part.Phrases)
        {
            switch (phrase.Kind)
            {
                case PhraseKind.StarPower:
                    track.Note(phrase.Tick, phrase.Length, MidiLayout.StarPowerPitch);
                    break;
                case PhraseKind.Solo:
                    track.Note(phrase.Tick, phrase.Length, MidiLayout.SoloPitch);
                    break;
                case PhraseKind.DrumFill or PhraseKind.BigRockEnding:
                    for (int lane = 0; lane < 5; lane++)
                        track.Note(phrase.Tick, phrase.Length, MidiLayout.DrumFillPitch + lane);
                    break;
                case PhraseKind.Roll or PhraseKind.Trill when drums:
                    track.Note(phrase.Tick, phrase.Length, phrase.Kind == PhraseKind.Roll ? MidiLayout.RollPitch : MidiLayout.TrillPitch, LaneVelocityOf(phrase.LowestDifficulty));
                    break;
            }
        }

        if (drums)
        {
            foreach (var marker in part.TomMarkers)
                track.Note(marker.Tick, marker.Length, MidiLayout.FirstTomPitch + marker.Pad - TomMarker.FirstPad);
        }

        foreach (var e in part.TextEvents)
            track.Add(e.Tick, PartEventOrder.Text, TextKey, Text(e.Tick, e.Type, e.Text));
        foreach (var e in part.RawEvents)
            track.Add(e.Tick, PartEventOrder.Text, RawKey, e.Array);
        return track;
    }

    /// <summary><c>EVENTS</c>: its name, then at each tick its text events and raw events.</summary>
    private static TrackEvents EventsTrack(EditableChart chart)
    {
        var track = new TrackEvents("EVENTS");
        foreach (var e in chart.Events)
            track.Add(e.Tick, PartEventOrder.Text, TextKey, Text(e.Tick, e.Type, e.Text));
        foreach (var e in chart.EventsRaw)
            track.Add(e.Tick, PartEventOrder.Text, RawKey, e.Array);
        return track;
    }

    /// <summary>
    /// <c>BEAT</c>: a note on each beat of <paramref name="map"/> from tick 0 through <paramref name="last"/>, 12 on a
    /// measure's downbeat and 13 on the others, a sixteenth note long or as long as there is to the next beat.
    /// </summary>
    private static TrackEvents BeatTrack(TempoMap map, long last)
    {
        var track = new TrackEvents("BEAT");
        long length = Math.Max(1, map.Resolution / 4);
        var beats = map.BeatLines(0, last).ToList();
        for (int i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            long until = i + 1 < beats.Count ? beats[i + 1].Tick - beat.Tick : length;
            track.Note(beat.Tick, Math.Min(length, until), beat.IsMeasure ? DownbeatPitch : BeatPitch);
        }

        return track;
    }

    /// <summary>A note's pitch: <see cref="MidiLayout.BaseOf"/> <paramref name="b"/> and its lane's offset (charter-spec §7.1).</summary>
    private static int PitchOf(ChartInstrumentKind kind, int b, EditableNote note) => kind switch
    {
        ChartInstrumentKind.Drums => note.DoubleKick ? MidiLayout.DoubleKickPitch : b + note.Lane,
        ChartInstrumentKind.SixFret => note.Lane == ChartNote.OpenLane ? b : b + 1 + note.Lane,
        _ => note.Lane == ChartNote.OpenLane ? b - 1 : b + note.Lane,
    };

    /// <summary>A roll's or trill's velocity, which takes it down to its lowest difficulty (charter-spec §7.2).</summary>
    private static int LaneVelocityOf(ChartDifficulty? lowest) => lowest switch
    {
        ChartDifficulty.Easy => 25,
        ChartDifficulty.Medium => 35,
        ChartDifficulty.Hard => 45,
        _ => MidiLayout.NormalVelocity,
    };

    private static byte[] Text(long tick, byte type, string text) => MidiMessage.Text(tick, type, text).Bytes;

    /// <summary>The tick a passthrough track ends at, for <c>BEAT</c> to reach; 0 for a chunk that isn't a track, or can't be read.</summary>
    private static long LastTickOf(PassthroughTrack track)
    {
        if (!track.IsTrack)
            return 0;
        long last = 0;
        try
        {
            var reader = new MidiFile.EventReader(track.ChunkOf());
            while (reader.Next(track.Array) is { } e)
                last = e.Tick;
        }
        catch (ChartFormatException)
        {
            // Read on load, so this can't happen to a loaded chart's tracks; a track that can't be read has no end.
        }

        return last;
    }

    // Passthrough tracks.

    /// <summary>Where a passthrough track must go against the parts, for the reader to take the parts written.</summary>
    private enum Place
    {
        /// <summary>Anywhere: the reader doesn't take it for one of the chart's parts, or <c>EVENTS</c>.</summary>
        Anywhere,

        /// <summary>
        /// Before the parts: it's named <c>PART GUITAR</c> or <c>PART DRUMS</c>, which replace an earlier track of their
        /// instrument, for a part the chart has.
        /// </summary>
        BeforeParts,

        /// <summary>
        /// After the parts and <c>EVENTS</c>: of two tracks of an instrument the reader keeps the first, and of two
        /// <c>EVENTS</c> the loader does.
        /// </summary>
        AfterParts,
    }

    private static Place PlaceOf(PassthroughTrack track, EditableChart chart)
    {
        string? name = track.ReaderName();
        if (name == "EVENTS")
            return Place.AfterParts;
        if (track.ReadAs() is not { } instrument || chart.PartOf(instrument) is null)
            return Place.Anywhere;
        if (MidiLayout.OverridingTrackNames.Contains(name!))
            return Place.BeforeParts;

        // Written under a name that replaces it, the part wins wherever another track of its instrument is.
        return MidiLayout.OverridingTrackNames.Contains(ChartToMidi.TrackNames[instrument]) ? Place.Anywhere : Place.AfterParts;
    }

    /// <summary>How many of the passthrough tracks, from the first, are written before the parts: through the last that must be.</summary>
    private static int FrontCount(EditableChart chart)
    {
        int front = 0;
        for (int i = 0; i < chart.Passthrough.Count; i++)
        {
            if (PlaceOf(chart.Passthrough[i], chart) == Place.BeforeParts)
                front = i + 1;
        }

        return front;
    }

    /// <summary>
    /// Puts the passthrough tracks in the order they're written in: any that must come after the parts but was before
    /// one that must come before them goes after the latter, with those after it.
    /// </summary>
    /// <returns>Whether the order changed.</returns>
    private static bool OrderPassthrough(EditableChart chart)
    {
        int front = FrontCount(chart);
        var places = chart.Passthrough.Take(front).Select(t => PlaceOf(t, chart)).ToList();
        if (!places.Contains(Place.AfterParts))
            return false;

        var all = chart.Passthrough.ToList();
        var ordered = new List<PassthroughTrack>(all.Count);
        ordered.AddRange(all.Take(front).Where((_, i) => places[i] != Place.AfterParts));
        ordered.AddRange(all.Take(front).Where((_, i) => places[i] == Place.AfterParts));
        ordered.AddRange(all.Skip(front));
        for (int i = 0; i < ordered.Count; i++)
            chart.SetPassthrough(i, ordered[i]);
        return true;
    }

    // Normalizing.

    private static void NormalizePart(EditablePart part, DrumsVariant? variant, Changes changes)
    {
        var kind = part.Kind;
        bool drums = kind == ChartInstrumentKind.Drums;
        bool fiveFret = kind == ChartInstrumentKind.FiveFret;
        changes.Texts += SettingTexts(part, drums, fiveFret);

        bool dynamics = false, opens = false;
        foreach (var track in part.Tracks)
        {
            CutNotes(track, changes);
            if (!drums)
                NormalizeSustains(track, changes);
            if (drums && track.Difficulty == ChartDifficulty.Expert)
                DropDoubledKicks(track, changes);

            // A marking covering no note marks nothing, and the loader leaves it out.
            var unused = track.FlaggedChords.Where(c => track.Notes.At(c.Tick).IsEmpty).Select(c => c.Tick).ToList();
            foreach (long tick in unused)
                track.SetFlags(tick, ChordFlags.None);
            changes.Markings += unused.Count;

            foreach (var note in track.Notes)
            {
                dynamics |= note.Dynamics != NoteDynamics.None;
                opens |= note.Lane == ChartNote.OpenLane;
            }
        }

        changes.Phrases += MergePhrases(part);
        if (drums && variant == DrumsVariant.FourLane)
        {
            // The games would read any tom marker as Pro drums (charter-spec C6 item 9).
            changes.TomMarkers += part.TomMarkers.Count;
            part.TomMarkers.Clear();
        }

        changes.TomMarkers += MergeTomMarkers(part);
        part.ChartDynamics = drums && dynamics;
        part.EnhancedOpens = fiveFret && (part.EnhancedOpens || opens);
    }

    /// <summary>
    /// Takes the part's text events the loader takes for its settings or mixes into them, and those of a type that
    /// isn't text into its raw events, as the loader would.
    /// </summary>
    /// <returns>How many were taken.</returns>
    private static int SettingTexts(EditablePart part, bool drums, bool fiveFret)
    {
        int moved = RawTexts(part.TextEvents, part.RawEvents);
        var kept = new List<TextEvent>(part.TextEvents.Count);
        foreach (var e in part.TextEvents)
        {
            if (MidiChartLoader.IsReadText(e.Type))
            {
                string normalized = ChartTextEvents.Normalize(e.Text);
                if (fiveFret && normalized == EnhancedOpensText)
                {
                    part.EnhancedOpens = true;
                    continue;
                }

                if (drums && normalized == ChartDynamicsText)
                    continue;
                if (drums && DrumMix.TryParse(e.Text, e.Tick, out var difficulty, out var mix))
                {
                    part[difficulty].Mixes.Add(mix);
                    continue;
                }
            }

            kept.Add(e);
        }

        if (kept.Count == part.TextEvents.Count)
            return moved;
        moved += part.TextEvents.Count - kept.Count;
        part.TextEvents.Clear();
        foreach (var e in kept)
            part.TextEvents.Add(e);
        return moved;
    }

    /// <summary>Moves the text events of a meta type that isn't text, which the loader keeps as raw events, into <paramref name="raws"/>.</summary>
    /// <returns>How many were moved.</returns>
    private static int RawTexts(TickList<TextEvent> texts, TickList<RawEvent> raws)
    {
        var moved = texts.Where(e => !MidiChartLoader.IsText(e.Type)).ToList();
        foreach (var e in moved)
        {
            texts.Remove(e);
            raws.Add(new RawEvent(e.Tick, Text(e.Tick, e.Type, e.Text)));
        }

        return moved.Count;
    }

    /// <summary>Cuts each note held into the next note on its pitch short there.</summary>
    private static void CutNotes(EditableTrack track, Changes changes)
    {
        var notes = track.Notes.ToList();
        var last = new Dictionary<(int Lane, bool DoubleKick), int>();
        int cut = 0;
        for (int i = 0; i < notes.Count; i++)
        {
            var key = (notes[i].Lane, notes[i].DoubleKick);
            if (last.TryGetValue(key, out int j) && notes[j].Tick + notes[j].Length > notes[i].Tick)
            {
                notes[j] = notes[j] with { Length = notes[i].Tick - notes[j].Tick };
                cut++;
            }

            last[key] = i;
        }

        if (cut == 0)
            return;
        changes.Notes += cut;
        track.Notes.Clear();
        foreach (var note in notes)
            track.Notes.Add(note);
    }

    /// <summary>Makes each hold under <see cref="EditableNote.MinimumSustainLength"/> a plain note.</summary>
    private static void NormalizeSustains(EditableTrack track, Changes changes)
    {
        var shortNotes = track.Notes
            .Where(static n => n.Length > EditableNote.PlainLength && n.Length < EditableNote.MinimumSustainLength)
            .ToList();
        if (shortNotes.Count == 0)
            return;

        changes.Sustains += shortNotes.Count;
        foreach (var note in shortNotes)
        {
            track.Notes.Remove(note);
            track.Notes.Add(note with { Length = EditableNote.PlainLength });
        }
    }

    /// <summary>Of a kick and a 2x kick on one tick, leaves out the one let go second, which the games drop.</summary>
    private static void DropDoubledKicks(EditableTrack track, Changes changes)
    {
        var dropped = new List<EditableNote>();
        foreach (var doubleKick in track.Notes.Where(n => n.DoubleKick).ToList())
        {
            if (!track.TryGetNote(doubleKick.Tick, 0, doubleKick: false, out var kick))
                continue;

            // Let go first: the shorter, or of two as long the lower pitch, the 2x kick's.
            dropped.Add(doubleKick.Length <= kick.Length ? kick : doubleKick);
        }

        foreach (var note in dropped)
            track.Notes.Remove(note);
        changes.Kicks += dropped.Count;
    }

    /// <summary>Merges the part's overlapping phrases on the same pitches.</summary>
    /// <returns>How many phrases were merged into others.</returns>
    private static int MergePhrases(EditablePart part) => MergeOverlapping(
        part.Phrases,
        static p => p.Kind is PhraseKind.BigRockEnding ? PhraseKind.DrumFill : p.Kind,
        static p => p.Tick,
        static p => p.Length,
        static (c, p) => c with
        {
            Length = Math.Max(c.Tick + c.Length, p.Tick + p.Length) - c.Tick,
            LowestDifficulty = c.LowestDifficulty is { } a && p.LowestDifficulty is { } b ? (a < b ? a : b) : c.LowestDifficulty,
        });

    /// <summary>Merges the part's overlapping tom markers on one pad.</summary>
    /// <returns>How many markers were merged into others.</returns>
    private static int MergeTomMarkers(EditablePart part) => MergeOverlapping(
        part.TomMarkers,
        static m => m.Pad,
        static m => m.Tick,
        static m => m.Length,
        static (c, m) => c with { Length = Math.Max(c.Tick + c.Length, m.Tick + m.Length) - c.Tick });

    /// <summary>
    /// Merges the overlapping markers of <paramref name="markers"/> that share a key, in tick order: each into the one
    /// it overlaps, as <paramref name="merge"/> combines them.
    /// </summary>
    /// <returns>How many markers were merged into others.</returns>
    private static int MergeOverlapping<T, TKey>(TickList<T> markers, Func<T, TKey> keyOf, Func<T, long> tickOf, Func<T, long> lengthOf, Func<T, T, T> merge)
    {
        var all = markers.ToList();
        var merged = new List<T>(all.Count);
        foreach (var group in all.GroupBy(keyOf))
        {
            bool any = false;
            T current = default!;
            foreach (var marker in group)
            {
                if (any && Overlap(tickOf(current), lengthOf(current), tickOf(marker)))
                {
                    current = merge(current, marker);
                    continue;
                }

                if (any)
                    merged.Add(current);
                current = marker;
                any = true;
            }

            if (any)
                merged.Add(current);
        }

        int count = all.Count - merged.Count;
        if (count == 0)
            return 0;
        markers.Clear();
        foreach (var marker in merged)
            markers.Add(marker);
        return count;
    }

    /// <summary>
    /// Whether a marker from <paramref name="tick"/> for <paramref name="length"/> overlaps one starting at
    /// <paramref name="next"/>, no earlier: it starts on the same tick, or before the first ends.
    /// </summary>
    private static bool Overlap(long tick, long length, long next) => next == tick || next < tick + length;

    // Checking.

    /// <summary>What says whether two game differences found at different thresholds are the same one.</summary>
    private static string KeyOf(ReadBackDifference d) =>
        string.Create(CultureInfo.InvariantCulture, $"{d.Kind} {d.Instrument} {d.Difficulty} {d.Flags} {d.Count} {string.Join(",", d.Ticks)}");

    /// <summary>Each difference and issue in a few words, for the log.</summary>
    private static IEnumerable<string> Named(ChartWriteResult result) =>
    [
        .. result.ModelDifferences.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Kind} {d.Part} {d.Difficulty} at {d.Tick}")),
        .. result.GameDifferences.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Kind} {d.Instrument} {d.Difficulty} at {d.Ticks.FirstOrDefault()}")),
        .. result.ReloadIssues.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Kind} {i.Part} {i.Difficulty} at {i.Tick}")),
    ];

    /// <summary>How much normalizing a chart changed, for the log.</summary>
    private sealed class Changes
    {
        public int Notes { get; set; }

        public int Sustains { get; set; }

        public int Kicks { get; set; }

        public int Markings { get; set; }

        public int Phrases { get; set; }

        public int TomMarkers { get; set; }

        public int Texts { get; set; }

        public bool Passthrough { get; set; }

        public bool Any => Notes + Sustains + Kicks + Markings + Phrases + TomMarkers + Texts > 0 || Passthrough;
    }
}
