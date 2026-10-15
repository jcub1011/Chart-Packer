namespace ChartShared.Core.Charts;

/// <summary>Where a MIDI chart keeps each instrument and difficulty, in the Rock Band and Clone Hero layout.</summary>
internal static class MidiLayout
{
    public const int SoloPitch = 103;
    public const int TapPitch = 104;
    public const int StarPowerPitch = 116;
    public const int DoubleKickPitch = 95;

    /// <summary>A drum pad note's velocity that makes it an accent, on a track with <c>[ENABLE_CHART_DYNAMICS]</c>.</summary>
    public const int AccentVelocity = 127;

    /// <summary>A drum pad note's velocity that makes it a ghost, on a track with <c>[ENABLE_CHART_DYNAMICS]</c>.</summary>
    public const int GhostVelocity = 1;

    /// <summary>The velocity a note is written with when nothing rides on it.</summary>
    public const int NormalVelocity = 100;

    /// <summary>The pro-drums tom markers for yellow, blue and green.</summary>
    public const int FirstTomPitch = 110;

    /// <summary>Player 1's face-off phrases; player 2's are the pitch above.</summary>
    public const int FaceOffPitch = 105;

    /// <summary>The first of the five drum fill markers, 120 to 124, one for each lane.</summary>
    public const int DrumFillPitch = 120;

    /// <summary>The drums' kick lane marker.</summary>
    public const int KickLanePitch = 125;

    /// <summary>A roll lane: a drum roll, or a fret tremolo.</summary>
    public const int RollPitch = 126;

    /// <summary>A trill lane: a two-lane drum roll, or a fret trill.</summary>
    public const int TrillPitch = 127;

    // Phase Shift's sysex event: 'P' 'S' 0 0, then the difficulty, the kind and on or off.
    public const int SysexOpen = 1;
    public const int SysexTap = 4;
    public const int SysexAllDifficulties = 0xFF;

    /// <summary>The instrument of each track YARG reads, by its exact name.</summary>
    public static IReadOnlyDictionary<string, ChartInstrument> TrackNames { get; } = new Dictionary<string, ChartInstrument>(StringComparer.Ordinal)
    {
        ["PART GUITAR"] = ChartInstrument.Guitar,
        ["T1 GEMS"] = ChartInstrument.Guitar,
        ["PART BASS"] = ChartInstrument.Bass,
        ["PART RHYTHM"] = ChartInstrument.Rhythm,
        ["PART KEYS"] = ChartInstrument.Keys,
        ["PART DRUMS"] = ChartInstrument.Drums,
        ["PART REAL_DRUMS_PS"] = ChartInstrument.Drums,
        ["PART GUITAR COOP"] = ChartInstrument.GuitarCoop,
        ["PART GUITAR GHL"] = ChartInstrument.GhlGuitar,
        ["PART BASS GHL"] = ChartInstrument.GhlBass,
        ["PART RHYTHM GHL"] = ChartInstrument.GhlRhythm,
        ["PART GUITAR COOP GHL"] = ChartInstrument.GhlCoop,
    };

    /// <summary>The track names that replace an earlier track of their instrument, where any other is skipped.</summary>
    public static IReadOnlySet<string> OverridingTrackNames { get; } = new HashSet<string>(StringComparer.Ordinal) { "PART GUITAR", "PART DRUMS" };

    /// <summary>
    /// The first pitch of a difficulty: five-fret green and the drum kick are 60, 72, 84 and 96;
    /// six-fret's open note is 58, 70, 82 and 94.
    /// </summary>
    public static int BaseOf(ChartInstrumentKind kind, ChartDifficulty difficulty) =>
        (kind == ChartInstrumentKind.SixFret ? 58 : 60) + (12 * (int)difficulty);

    /// <summary>The difficulty whose notes (and forcing markers) use <paramref name="pitch"/>, if one does.</summary>
    public static ChartDifficulty? DifficultyOf(ChartInstrumentKind kind, int pitch)
    {
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            int b = BaseOf(kind, difficulty);
            var (low, high) = kind switch
            {
                // Open notes (with ENHANCED_OPENS) sit below green; force HOPO and strum above orange.
                ChartInstrumentKind.FiveFret => (b - 1, b + ForceStrumOffset(kind)),
                ChartInstrumentKind.Drums => (difficulty == ChartDifficulty.Expert ? DoubleKickPitch : b, b + 5),
                _ => (b, b + ForceStrumOffset(kind)),
            };
            if (pitch >= low && pitch <= high)
                return difficulty;
        }

        return null;
    }

    /// <summary>
    /// The lane of a fret note <paramref name="offset"/> pitches above its difficulty's <see cref="BaseOf"/>, if
    /// it's a note: six-fret's open note, then white 1-3 and black 1-3; five-fret's green to orange, and the
    /// open note below green when <paramref name="enhancedOpens"/>.
    /// </summary>
    public static int? FretLaneOf(ChartInstrumentKind kind, int offset, bool enhancedOpens) =>
        kind == ChartInstrumentKind.SixFret
            ? offset switch { 0 => ChartNote.OpenLane, >= 1 and <= 6 => offset - 1, _ => null }
            : offset switch { >= 0 and <= 4 => offset, -1 when enhancedOpens => ChartNote.OpenLane, _ => null };

    /// <summary>
    /// The pad of a drum note <paramref name="offset"/> pitches above its difficulty's <see cref="BaseOf"/>, if
    /// it's a note: 0 the kick to 5 the fifth pad, as <see cref="DrumGem.Pad"/> has them, and -1, the pitch below
    /// the kick, its Expert+ double kick.
    /// </summary>
    public static int? DrumPadOf(int offset) => offset is >= -1 and <= 5 ? offset : null;

    /// <summary>
    /// A drum note's accent or ghost by its note-on velocity, as YARG reads them once <c>ENABLE_CHART_DYNAMICS</c> is on:
    /// on a pad, exactly 127 is an accent and exactly 1 a ghost; any other velocity, and any kick, a normal note.
    /// </summary>
    /// <param name="pad">As <see cref="DrumPadOf"/> gives it: 0 the kick, -1 the double kick.</param>
    public static NoteFlags DrumDynamicsOf(int pad, int velocity) => pad <= 0 ? NoteFlags.None : velocity switch
    {
        AccentVelocity => NoteFlags.Accent,
        GhostVelocity => NoteFlags.Ghost,
        _ => NoteFlags.None,
    };

    /// <summary>The velocity a drum note is written with on a track with <c>ENABLE_CHART_DYNAMICS</c>, as <see cref="DrumDynamicsOf"/> reads it back.</summary>
    /// <param name="dynamics"><see cref="NoteFlags.Accent"/>, <see cref="NoteFlags.Ghost"/> or neither.</param>
    public static int DrumVelocityOf(NoteFlags dynamics) =>
        (dynamics & NoteFlags.Accent) != 0 ? AccentVelocity : (dynamics & NoteFlags.Ghost) != 0 ? GhostVelocity : NormalVelocity;

    /// <summary>How far a fret difficulty's force-HOPO marker is above its <see cref="BaseOf"/>: just above orange, or black 3.</summary>
    public static int ForceHopoOffset(ChartInstrumentKind kind) => kind == ChartInstrumentKind.SixFret ? 7 : 5;

    /// <summary>How far a fret difficulty's force-strum marker is above its <see cref="BaseOf"/>: the pitch above force HOPO.</summary>
    public static int ForceStrumOffset(ChartInstrumentKind kind) => ForceHopoOffset(kind) + 1;

    /// <summary>The difficulty a Phase Shift sysex event is for, if it's one for a single difficulty.</summary>
    public static ChartDifficulty? DifficultyOfSysex(ReadOnlySpan<byte> data) =>
        data.Length >= 7 && data[0] == 0x50 && data[1] == 0x53 && data[2] == 0 && data[3] == 0 && data[4] <= 3
            ? (ChartDifficulty)data[4]
            : null;
}

/// <summary>A tempo or time-signature event of a sync track that YARG reads, at the tick YARG reads it at.</summary>
/// <param name="Index">Its index among the track's events.</param>
/// <param name="Tick">Its tick as YARG counts it: the track's first event's delta later than written.</param>
/// <param name="IsTempo">A tempo event, rather than a time signature.</param>
/// <param name="MicrosecondsPerQuarter">A tempo's beat, as written.</param>
/// <param name="Numerator">A time signature's beats per measure, as written.</param>
/// <param name="DenominatorPower">A time signature's note value, as the power of two written.</param>
/// <param name="ClocksPerClick">A time signature's MIDI clocks per metronome click.</param>
/// <param name="ThirtySecondsPerQuarter">A time signature's thirty-second notes per MIDI quarter note.</param>
internal readonly record struct MidiSyncEvent(
    int Index,
    long Tick,
    bool IsTempo,
    int MicrosecondsPerQuarter = 0,
    int Numerator = 0,
    int DenominatorPower = 0,
    byte ClocksPerClick = 0,
    byte ThirtySecondsPerQuarter = 0);

/// <summary>The track YARG reads an instrument from.</summary>
/// <param name="Chunk">Its index among the file's chunks after the header, the sync track being 0.</param>
/// <param name="Name">Its name, as <see cref="MidiChartReader.NameOf(byte[], List{MidiEvent})"/> takes it.</param>
/// <param name="Events">Its events.</param>
internal readonly record struct MidiPartTrack(int Chunk, string Name, List<MidiEvent> Events);

/// <summary>
/// Reads a <c>notes.mid</c> as YARG does (<c>MidReader</c> in YARG.Core): the tempo map, and the tracks named in
/// <see cref="MidiLayout.TrackNames"/>. Everything else (vocals, venue, events, pro keys) is skipped.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The tempo map is the first track's. As YARG counts it, every tempo and time signature there is the
/// first event's delta later than it's written.</item>
/// <item>A track is named by a track-name event at tick 0, and its first event is never read. Of two tracks of an
/// instrument the first is kept, except that a later <c>PART GUITAR</c> or <c>PART DRUMS</c> replaces it.</item>
/// <item>A note-on pairs with the next note-off of its pitch and channel; a second note-on while one is held is
/// ignored, and a note never let go is lost. Notes are taken as they're let go, and the first of two on a tick
/// and fret or pad is kept.</item>
/// <item>On a drums track, a pad note let go after an <c>[ENABLE_CHART_DYNAMICS]</c> event is an accent at velocity
/// 127 and a ghost at velocity 1. Without the event every note is a normal note, whatever its velocity.</item>
/// </list>
/// </remarks>
internal static class MidiChartReader
{
    /// <exception cref="ChartFormatException">The file isn't a MIDI file YARG can read, or is malformed.</exception>
    public static ChartDocument Read(byte[] bytes, ChartReadOptions options)
    {
        var (resolution, chunks) = ChunksOf(bytes);
        var tempo = TempoOf(bytes, resolution, [MidiFile.ReadEvents(bytes, chunks[0])]);
        var parts = PartTracksOf(bytes, chunks);

        var read = new List<(ChartInstrument, IReadOnlyDictionary<ChartDifficulty, List<ChartNote>>, IReadOnlyList<StarPowerPhrase>)>(parts.Count);
        DrumsLayout? drumsLayout = null;
        foreach (var (instrument, part) in parts)
        {
            var events = part.Events;
            var kind = ChartInstruments.KindOf(instrument);
            var track = MidiTrackNotes.Of(bytes, events, kind == ChartInstrumentKind.FiveFret);
            var starPower = GameNotes.StarPower(track.Spans(options.MidiStarPowerPitch));
            if (kind == ChartInstrumentKind.Drums)
            {
                var layout = DrumsLayoutOf(bytes, events, options);
                drumsLayout = layout;
                read.Add((instrument, Drums(track, layout, starPower), starPower));
                continue;
            }

            var notes = new Dictionary<ChartDifficulty, List<ChartNote>>();
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
                notes[difficulty] = Frets(track, kind, difficulty, resolution, options, starPower);
            read.Add((instrument, notes, starPower));
        }

        return GameNotes.Document(tempo, read, drumsLayout);
    }

    /// <summary>The file's division, and its chunks after the header, the sync track first, as YARG takes them.</summary>
    /// <exception cref="ChartFormatException">The file isn't a MIDI file, or doesn't start with a track.</exception>
    internal static (int Resolution, List<MidiChunk> Chunks) ChunksOf(byte[] bytes)
    {
        var (_, resolution, all) = MidiFile.ReadChunks(bytes);
        var chunks = all.Skip(1).ToList();
        if (chunks.Count == 0 || chunks[0].Id != "MTrk")
            throw new ChartFormatException("The MIDI file doesn't start with a track.");
        return (resolution, chunks);
    }

    /// <summary>
    /// The track YARG reads each instrument from: of the tracks after the sync track named in
    /// <see cref="MidiLayout.TrackNames"/>, the first of each instrument, except that a later one named in
    /// <see cref="MidiLayout.OverridingTrackNames"/> replaces it.
    /// </summary>
    /// <remarks>
    /// Every track is read, so a malformed one fails the file, but only the events of the tracks taken are kept:
    /// vocals, venue and the like are often most of a file.
    /// </remarks>
    /// <param name="chunks">The file's chunks after its header, the sync track first, as <see cref="ChunksOf"/> gives them.</param>
    /// <exception cref="ChartFormatException">A track is malformed.</exception>
    internal static Dictionary<ChartInstrument, MidiPartTrack> PartTracksOf(byte[] bytes, List<MidiChunk> chunks)
    {
        var parts = new Dictionary<ChartInstrument, MidiPartTrack>();
        for (int i = 1; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (chunk.Id != "MTrk")
                continue;
            if (NameOf(bytes, chunk) is { } name
                && MidiLayout.TrackNames.TryGetValue(name, out var instrument)
                && (!parts.ContainsKey(instrument) || MidiLayout.OverridingTrackNames.Contains(name)))
            {
                parts[instrument] = new MidiPartTrack(i, name, MidiFile.ReadEvents(bytes, chunk));
            }
            else
            {
                MidiFile.Check(bytes, chunk);
            }
        }

        return parts;
    }

    /// <summary>
    /// The tempo map, from the tempo and time-signature events of the first of <paramref name="tracks"/>, YARG's
    /// sync track, each the first event's delta later than written, as YARG counts them.
    /// </summary>
    internal static TempoMap TempoOf(byte[] bytes, int resolution, List<List<MidiEvent>> tracks)
    {
        var tempos = new List<TempoChange>();
        var signatures = new List<TimeSignature>();
        foreach (var e in SyncEventsOf(bytes, tracks.Count > 0 ? tracks[0] : []))
        {
            if (e.IsTempo)
                tempos.Add(new TempoChange(e.Tick, e.MicrosecondsPerQuarter));
            else
                signatures.Add(new TimeSignature(e.Tick, e.Numerator, 1 << e.DenominatorPower));
        }

        return new TempoMap(resolution, tempos.OrderBy(t => t.Tick), signatures.OrderBy(s => s.Tick));
    }

    /// <summary>
    /// The tempo and time-signature events YARG reads from the sync track <paramref name="sync"/>, in order, each the
    /// first event's delta later than written, as YARG counts them: tempos 3 bytes long, and time signatures of at
    /// least 2 bytes whose denominator is below 2^7.
    /// </summary>
    internal static List<MidiSyncEvent> SyncEventsOf(byte[] bytes, List<MidiEvent> sync)
    {
        var read = new List<MidiSyncEvent>();
        long shift = sync.Count > 0 ? sync[0].Tick : 0;
        for (int index = 0; index < sync.Count; index++)
        {
            var e = sync[index];
            if (!e.IsMeta)
                continue;
            var (type, start, length) = MidiFile.MetaOf(bytes, e);
            if (type == 0x51 && length == 3)
            {
                read.Add(new MidiSyncEvent(index, e.Tick + shift, IsTempo: true, MicrosecondsPerQuarter: (bytes[start] << 16) | (bytes[start + 1] << 8) | bytes[start + 2]));
            }
            else if (type == 0x58 && length >= 2 && bytes[start + 1] < 7)
            {
                // The metronome bytes, which the games ignore, are MIDI's usual 24 and 8 when the event leaves them out.
                read.Add(new MidiSyncEvent(
                    index,
                    e.Tick + shift,
                    IsTempo: false,
                    Numerator: bytes[start],
                    DenominatorPower: bytes[start + 1],
                    ClocksPerClick: length >= 3 ? bytes[start + 2] : (byte)24,
                    ThirtySecondsPerQuarter: length >= 4 ? bytes[start + 3] : (byte)8));
            }
        }

        return read;
    }

    /// <summary>
    /// A track's name as YARG takes it: of its track-name events at tick 0, the first YARG recognizes, or the first.
    /// </summary>
    internal static string? NameOf(byte[] bytes, List<MidiEvent> events)
    {
        string? first = null;
        foreach (var e in events)
        {
            if (e.Tick != 0)
                break;
            if (MidiFile.TextOf(bytes, e, 0x03) is not { } name)
                continue;
            if (IsRecognized(name))
                return name;
            first ??= name;
        }

        return first;
    }

    /// <summary>The name of the track <paramref name="chunk"/>, as <see cref="NameOf(byte[], List{MidiEvent})"/> takes it, reading only its events at tick 0.</summary>
    /// <exception cref="ChartFormatException">The track is malformed among them.</exception>
    internal static string? NameOf(byte[] bytes, MidiChunk chunk)
    {
        string? first = null;
        var reader = new MidiFile.EventReader(chunk);
        while (reader.Next(bytes) is { } e)
        {
            if (e.Tick != 0)
                break;
            if (MidiFile.TextOf(bytes, e, 0x03) is not { } name)
                continue;
            if (IsRecognized(name))
                return name;
            first ??= name;
        }

        return first;
    }

    private static bool IsRecognized(string name) =>
        name is "BEAT" or "EVENTS" or "VENUE" or "PART REAL_KEYS_X" or "PART REAL_KEYS_H" or "PART REAL_KEYS_M" or "PART REAL_KEYS_E" or "PART VOCALS"
            or "PART REAL_GUITAR" or "PART REAL_GUITAR_22" or "PART REAL_BASS" or "PART REAL_BASS_22" or "PART ELITE_DRUMS"
            or "HARM1" or "HARM2" or "HARM3" or "PART HARM1" or "PART HARM2" or "PART HARM3"
        || MidiLayout.TrackNames.ContainsKey(name);

    /// <summary>
    /// The drums' layout (see <see cref="DrumsLayouts"/>), from song.ini and the track's events in order: a
    /// fifth-pad note-on (65, 77, 89 or 101), or any tom marker event.
    /// </summary>
    internal static DrumsLayout DrumsLayoutOf(byte[] bytes, List<MidiEvent> events, ChartReadOptions options)
    {
        var layouts = new DrumsLayouts(options, midi: true);
        foreach (var e in events)
        {
            if (e.Kind is not (0x80 or 0x90))
                continue;
            int pitch = bytes[e.DataStart];
            bool on = e.Kind == 0x90 && bytes[e.DataStart + 1] > 0;
            if (pitch is 65 or 77 or 89 or 101 && on)
                layouts.FifthPad();
            else if (pitch is >= MidiLayout.FirstTomPitch and <= MidiLayout.FirstTomPitch + 2)
                layouts.ProMarker();
        }

        return layouts.Layout;
    }

    private static List<ChartNote> Frets(MidiTrackNotes track, ChartInstrumentKind kind, ChartDifficulty difficulty, int resolution, ChartReadOptions options, IReadOnlyList<StarPowerPhrase> starPower)
    {
        int b = MidiLayout.BaseOf(kind, difficulty);

        // The frets in the order they're let go, each as long as it's held.
        var gems = new List<Gem>();
        foreach (var note in track.Notes)
        {
            if (MidiLayout.FretLaneOf(kind, note.Pitch - b, note.EnhancedOpens) is { } fret)
                gems.Add(new Gem(note.Start, note.End - note.Start, fret));
        }

        int forceHopo = b + MidiLayout.ForceHopoOffset(kind);
        int forceStrum = b + MidiLayout.ForceStrumOffset(kind);
        return GameNotes.Frets(
            gems,
            sysexOpens: track.SysexFor(difficulty, MidiLayout.SysexOpen),
            forceHopo: track.Spans(forceHopo),
            forceStrum: track.Spans(forceStrum),
            taps: [.. track.Spans(MidiLayout.TapPitch), .. track.SysexFor(difficulty, MidiLayout.SysexTap)],
            cutoff: SustainCutoff.Of(resolution, options.SustainCutoff),
            hopoThreshold: options.MidiHopoTicks ?? options.MidiHopoThresholds(resolution)[0],
            starPower);
    }

    private static Dictionary<ChartDifficulty, List<ChartNote>> Drums(MidiTrackNotes track, DrumsLayout layout, IReadOnlyList<StarPowerPhrase> starPower)
    {
        var hits = new Dictionary<ChartDifficulty, List<DrumHit>>();
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            int b = MidiLayout.BaseOf(ChartInstrumentKind.Drums, difficulty);
            var list = new List<DrumHit>();
            foreach (var note in track.Notes)
            {
                // YARG reads accents and ghosts on the pads alone: never the kick or the Expert+ double kick.
                if (MidiLayout.DrumPadOf(note.Pitch - b) is { } pad)
                    list.Add(new DrumHit(note.Start, pad, track.HasChartDynamics(note) ? MidiLayout.DrumDynamicsOf(pad, note.Velocity) : NoteFlags.None));
            }

            hits[difficulty] = list;
        }

        // The tom markers over yellow, blue and the fourth pad, found once rather than for every note.
        IReadOnlyList<TickSpan>[] toms = [.. Enumerable.Range(MidiLayout.FirstTomPitch, 3).Select(track.Spans)];
        return GameNotes.Drums(hits, toms, [.. track.Texts.Select(t => (t.Tick, t.Text))], starPower, layout);
    }
}
