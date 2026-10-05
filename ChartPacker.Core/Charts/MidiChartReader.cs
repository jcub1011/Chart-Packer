using System.Runtime.InteropServices;

namespace ChartPacker.Core.Charts;

/// <summary>Where a MIDI chart keeps each instrument and difficulty, in the Rock Band and Clone Hero layout.</summary>
internal static class MidiLayout
{
    public const int SoloPitch = 103;
    public const int TapPitch = 104;
    public const int StarPowerPitch = 116;
    public const int DoubleKickPitch = 95;

    /// <summary>The pro-drums tom markers for yellow, blue and green.</summary>
    public const int FirstTomPitch = 110;

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
                ChartInstrumentKind.FiveFret => (b - 1, b + 6),
                ChartInstrumentKind.Drums => (difficulty == ChartDifficulty.Expert ? DoubleKickPitch : b, b + 5),
                _ => (b, b + 8),
            };
            if (pitch >= low && pitch <= high)
                return difficulty;
        }

        return null;
    }

    /// <summary>The difficulty a Phase Shift sysex event is for, if it's one for a single difficulty.</summary>
    public static ChartDifficulty? DifficultyOfSysex(ReadOnlySpan<byte> data) =>
        data.Length >= 7 && data[0] == 0x50 && data[1] == 0x53 && data[2] == 0 && data[3] == 0 && data[4] <= 3
            ? (ChartDifficulty)data[4]
            : null;
}

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
/// </list>
/// </remarks>
internal static class MidiChartReader
{
    /// <exception cref="ChartFormatException">The file isn't a MIDI file YARG can read, or is malformed.</exception>
    public static ChartDocument Read(byte[] bytes, ChartReadOptions options)
    {
        var (_, resolution, all) = MidiFile.ReadChunks(bytes);
        var chunks = all.Skip(1).ToList();
        if (chunks.Count == 0 || chunks[0].Id != "MTrk")
            throw new ChartFormatException("The MIDI file doesn't start with a track.");

        var tempo = TempoOf(bytes, resolution, [MidiFile.ReadEvents(bytes, chunks[0])]);

        // Every track is read, so a malformed one fails the file, but only the events of the tracks taken are kept:
        // vocals, venue and the like are often most of a file.
        var parts = new Dictionary<ChartInstrument, List<MidiEvent>>();
        foreach (var chunk in chunks.Skip(1))
        {
            if (chunk.Id != "MTrk")
                continue;
            if (NameOf(bytes, chunk) is { } name
                && MidiLayout.TrackNames.TryGetValue(name, out var instrument)
                && (!parts.ContainsKey(instrument) || MidiLayout.OverridingTrackNames.Contains(name)))
            {
                parts[instrument] = MidiFile.ReadEvents(bytes, chunk);
            }
            else
            {
                MidiFile.Check(bytes, chunk);
            }
        }

        var result = new Dictionary<ChartTrackKey, NoteTrack>();
        bool fiveLane = false, pro = false;
        foreach (var (instrument, events) in parts)
        {
            var track = TrackData.Of(bytes, events, ChartInstruments.KindOf(instrument) == ChartInstrumentKind.FiveFret);
            var kind = ChartInstruments.KindOf(instrument);
            var starPower = track.Phrases(options.MidiStarPowerPitch);
            if (kind == ChartInstrumentKind.Drums)
            {
                var layout = DrumsLayoutOf(bytes, events, options);
                fiveLane = layout == DrumsLayout.FiveLane;
                pro = layout == DrumsLayout.Pro;
                foreach (var (difficulty, notes) in Drums(track, layout, starPower))
                {
                    if (notes.Count > 0)
                        result[new ChartTrackKey(instrument, difficulty)] = new NoteTrack(notes, starPower);
                }

                continue;
            }

            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                var notes = Frets(track, kind, difficulty, resolution, options, starPower);
                if (notes.Count > 0)
                    result[new ChartTrackKey(instrument, difficulty)] = new NoteTrack(notes, starPower);
            }
        }

        return new ChartDocument(tempo, result, null, fiveLane) { IsProDrums = pro };
    }

    /// <summary>
    /// The tempo map, from the tempo and time-signature events of the first of <paramref name="tracks"/>, YARG's
    /// sync track, each the first event's delta later than written, as YARG counts them.
    /// </summary>
    internal static TempoMap TempoOf(byte[] bytes, int resolution, List<List<MidiEvent>> tracks)
    {
        var tempos = new List<TempoChange>();
        var signatures = new List<TimeSignature>();
        var sync = tracks.Count > 0 ? tracks[0] : [];
        long shift = sync.Count > 0 ? sync[0].Tick : 0;
        foreach (var e in sync.Where(e => e.IsMeta))
        {
            var (type, start, length) = MidiFile.MetaOf(bytes, e);
            if (type == 0x51 && length == 3)
                tempos.Add(new TempoChange(e.Tick + shift, (bytes[start] << 16) | (bytes[start + 1] << 8) | bytes[start + 2]));
            else if (type == 0x58 && length >= 2 && bytes[start + 1] < 7)
                signatures.Add(new TimeSignature(e.Tick + shift, bytes[start], 1 << bytes[start + 1]));
        }

        return new TempoMap(resolution, tempos.OrderBy(t => t.Tick), signatures.OrderBy(s => s.Tick));
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
    private static string? NameOf(byte[] bytes, MidiChunk chunk)
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
    private static DrumsLayout DrumsLayoutOf(byte[] bytes, List<MidiEvent> events, ChartReadOptions options)
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

    private static List<ChartNote> Frets(TrackData track, ChartInstrumentKind kind, ChartDifficulty difficulty, int resolution, ChartReadOptions options, IReadOnlyList<StarPowerPhrase> starPower)
    {
        int b = MidiLayout.BaseOf(kind, difficulty);
        long cutoff = SustainCutoff.Of(resolution, options.SustainCutoff);
        long LengthOf(Held note) => note.End - note.Start >= cutoff ? note.End - note.Start : 0;

        // The frets in tick order, those at a tick in the order they're let go, and each tick's first of a fret kept.
        // Six-fret: open, white 1-3, black 1-3.
        var gems = new List<Gem>();
        foreach (var note in track.Notes)
        {
            int offset = note.Pitch - b;
            int? lane = kind == ChartInstrumentKind.SixFret
                ? offset switch { 0 => ChartNote.OpenLane, >= 1 and <= 6 => offset - 1, _ => null }
                : offset switch { >= 0 and <= 4 => offset, -1 when note.EnhancedOpens => ChartNote.OpenLane, _ => null };
            if (lane is { } fret)
                gems.Add(new Gem(note.Start, LengthOf(note), fret));
        }

        TickOrder.Sort(gems, static g => g.Tick);
        TickOrder.KeepFirstOfEach(gems, static g => g.Tick, static g => g.Lane);

        // Phase Shift open phrases make every note in them open.
        var opens = track.SysexFor(difficulty, MidiLayout.SysexOpen);
        if (opens.Count > 0)
        {
            var items = CollectionsMarshal.AsSpan(gems);
            for (int i = 0; i < items.Length; i++)
            {
                if (Covered(opens, items[i].Tick))
                    items[i] = items[i] with { Lane = ChartNote.OpenLane };
            }
        }

        int forceHopo = kind == ChartInstrumentKind.SixFret ? b + 7 : b + 5;
        var hopo = track.Spans(forceHopo);
        var strum = track.Spans(forceHopo + 1);
        var taps = track.Spans(MidiLayout.TapPitch).Concat(track.SysexFor(difficulty, MidiLayout.SysexTap)).ToList();
        var marks = new Func<long, FretMarks>(tick => new FretMarks(false, Covered(hopo, tick), Covered(strum, tick), Covered(taps, tick)));

        long threshold = options.MidiHopoTicks ?? options.MidiHopoThresholds(resolution)[0];
        return NoteBuilder.Frets(gems, threshold, marks, starPower, midiChordRule: true);
    }

    private static Dictionary<ChartDifficulty, List<ChartNote>> Drums(TrackData track, DrumsLayout layout, IReadOnlyList<StarPowerPhrase> starPower)
    {
        var gems = new Dictionary<ChartDifficulty, List<DrumGem>>();
        var mixes = new Dictionary<ChartDifficulty, List<(long, bool)>>();

        // The tom markers over yellow, blue and the fourth pad, found once rather than for every note.
        List<Held>[] toms = [.. Enumerable.Range(MidiLayout.FirstTomPitch, 3).Select(track.Spans)];
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            int b = MidiLayout.BaseOf(ChartInstrumentKind.Drums, difficulty);
            var list = new List<DrumGem>();
            foreach (var note in track.Notes)
            {
                // The pad below the kick is its Expert+ double kick.
                int pad = note.Pitch - b;
                if (pad is < -1 or > 5)
                    continue;

                // Yellow, blue and the fourth pad are cymbals, each tom marker over one toggling it.
                bool cymbal = pad is >= 2 and <= 4 && CountCovering(toms[pad - 2], note.Start) % 2 == 0;
                list.Add(new DrumGem(note.Start, Math.Max(0, pad), cymbal, DoubleKick: pad == -1));
            }

            // In tick order, those at a tick in the order they're let go, and each tick's first of a pad kept: a
            // double kick is the kick's pad.
            TickOrder.Sort(list, static g => g.Tick);
            TickOrder.KeepFirstOfEach(list, static g => g.Tick, static g => g.Pad);

            gems[difficulty] = list;
            mixes[difficulty] =
            [
                .. track.Texts
                    .Select(t => ChartTextEvents.TryParseMix(t.Text, out var d, out bool flip) && d == difficulty ? (t.Tick, (bool?)flip) : (t.Tick, null))
                    .Where(m => m.Item2 is not null)
                    .Select(m => (m.Tick, m.Item2!.Value)),
            ];
        }

        var starPowerOf = Enum.GetValues<ChartDifficulty>().ToDictionary(d => d, _ => starPower);
        return DrumNotes.Build(gems, mixes, starPowerOf, layout);
    }

    // Loops rather than LINQ: these run for every note, and a lambda capturing the tick would be an allocation each time.
    private static bool Covered(List<Held> spans, long tick)
    {
        foreach (var span in spans)
        {
            if (Covers(span, tick))
                return true;
        }

        return false;
    }

    private static int CountCovering(List<Held> spans, long tick)
    {
        int count = 0;
        foreach (var span in spans)
        {
            if (Covers(span, tick))
                count++;
        }

        return count;
    }

    /// <summary>Whether a marker covers <paramref name="tick"/>: from its start to the tick before its end, or its start if it has no length.</summary>
    private static bool Covers(Held span, long tick) => tick == span.Start || (tick > span.Start && tick < span.End);

    /// <summary>A note, or a Phase Shift sysex phrase, from its start to its end.</summary>
    /// <param name="EnhancedOpens">Whether <c>ENHANCED_OPENS</c> had been read when it was let go.</param>
    private readonly record struct Held(int Pitch, long Start, long End, bool EnhancedOpens = false);

    /// <summary>A track's notes in the order they're let go, its Phase Shift sysex phrases, and its text events.</summary>
    private sealed class TrackData
    {
        public List<Held> Notes { get; private init; } = [];

        public Dictionary<(int Difficulty, int Code), List<Held>> Sysex { get; } = [];

        public List<(long Tick, string Text)> Texts { get; } = [];

        public List<Held> Spans(int pitch) => [.. Notes.Where(n => n.Pitch == pitch)];

        public List<Held> SysexFor(ChartDifficulty difficulty, int code) =>
        [
            .. Sysex.GetValueOrDefault(((int)difficulty, code)) ?? [],
            .. Sysex.GetValueOrDefault((MidiLayout.SysexAllDifficulties, code)) ?? [],
        ];

        /// <summary>The phrases on <paramref name="pitch"/> in order, the first of two that start together kept.</summary>
        public List<StarPowerPhrase> Phrases(int pitch) =>
        [
            .. Spans(pitch).OrderBy(s => s.Start).DistinctBy(s => s.Start).Select(s => new StarPowerPhrase(s.Start, s.End - s.Start)),
        ];

        /// <param name="enhancedOpens">Whether an <c>ENHANCED_OPENS</c> event can turn on open notes: five-fret only.</param>
        public static TrackData Of(byte[] bytes, List<MidiEvent> events, bool enhancedOpens)
        {
            // A note takes a note-on and a note-off.
            var track = new TrackData { Notes = new List<Held>(events.Count / 2) };
            var held = new List<(int Pitch, int Channel, long Start)>();
            var heldSysex = new List<(int Difficulty, int Code, long Start)>();
            bool opensOn = false;

            // YARG skips a track's first event, which is meant to be its name.
            foreach (var e in events.Skip(1))
            {
                if (e.Kind is 0x80 or 0x90)
                {
                    int pitch = bytes[e.DataStart];
                    int channel = e.Status & 0x0F;
                    int at = IndexOfHeld(held, pitch, channel);
                    if (e.Kind == 0x90 && bytes[e.DataStart + 1] > 0)
                    {
                        if (at < 0)
                            held.Add((pitch, channel, e.Tick));
                    }
                    else if (at >= 0)
                    {
                        track.Notes.Add(new Held(pitch, held[at].Start, e.Tick, opensOn));
                        held.RemoveAt(at);
                    }
                }
                else if (e.IsSysex)
                {
                    var data = MidiFile.SysexOf(bytes, e);
                    if (data.Length != 8 || data[0] != 0x50 || data[1] != 0x53 || data[2] != 0 || data[3] != 0 || data[6] > 1)
                        continue;

                    var key = ((int)data[4], (int)data[5]);
                    int at = IndexOfHeldSysex(heldSysex, key.Item1, key.Item2);
                    if (data[6] == 1)
                    {
                        if (at < 0)
                            heldSysex.Add((key.Item1, key.Item2, e.Tick));
                    }
                    else if (at >= 0)
                    {
                        if (!track.Sysex.TryGetValue(key, out var list))
                            track.Sysex[key] = list = [];
                        list.Add(new Held(0, heldSysex[at].Start, e.Tick));
                        heldSysex.RemoveAt(at);
                    }
                }
                else if (TextOf(bytes, e) is { } text)
                {
                    string normalized = ChartTextEvents.Normalize(text);
                    if (normalized == "ENHANCED_OPENS" && enhancedOpens)
                        opensOn = true;
                    else
                        track.Texts.Add((e.Tick, normalized));
                }
            }

            return track;
        }

        // Loops rather than FindIndex, whose lambda would capture the pitch: an allocation for every note event.
        private static int IndexOfHeld(List<(int Pitch, int Channel, long Start)> held, int pitch, int channel)
        {
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i].Pitch == pitch && held[i].Channel == channel)
                    return i;
            }

            return -1;
        }

        private static int IndexOfHeldSysex(List<(int Difficulty, int Code, long Start)> held, int difficulty, int code)
        {
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i].Difficulty == difficulty && held[i].Code == code)
                    return i;
            }

            return -1;
        }

        /// <summary>A text event's text: any text type but the copyright and the track name.</summary>
        private static string? TextOf(byte[] bytes, MidiEvent e)
        {
            if (!e.IsMeta)
                return null;
            var (type, start, length) = MidiFile.MetaOf(bytes, e);
            return type is 0x01 or (>= 0x04 and <= 0x09) ? MidiFile.DecodeText(bytes.AsSpan(start, length)) : null;
        }
    }
}
