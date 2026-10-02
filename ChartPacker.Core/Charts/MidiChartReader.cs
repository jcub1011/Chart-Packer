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

    public static IReadOnlyDictionary<string, ChartInstrument> TrackNames { get; } = new Dictionary<string, ChartInstrument>(StringComparer.OrdinalIgnoreCase)
    {
        ["PART GUITAR"] = ChartInstrument.Guitar,
        ["T1 GEMS"] = ChartInstrument.Guitar,
        ["PART BASS"] = ChartInstrument.Bass,
        ["PART RHYTHM"] = ChartInstrument.Rhythm,
        ["PART KEYS"] = ChartInstrument.Keys,
        ["PART DRUMS"] = ChartInstrument.Drums,
        ["PART GUITAR COOP"] = ChartInstrument.GuitarCoop,
        ["PART GUITAR GHL"] = ChartInstrument.GhlGuitar,
        ["PART BASS GHL"] = ChartInstrument.GhlBass,
    };

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
/// Reads a <c>notes.mid</c>: the tempo map, and the tracks named in <see cref="MidiLayout.TrackNames"/>.
/// Everything else (vocals, venue, events, pro keys) is skipped.
/// </summary>
internal static class MidiChartReader
{
    // Rock Band's default: a note is a natural HOPO within 170/480 of a beat of the one before.
    private const double HopoBeats = 170.0 / 480;

    /// <summary>
    /// The natural-HOPO thresholds a MIDI chart of <paramref name="resolution"/> can be played with, the one
    /// it's read with first: Rock Band's default and YARG's, a third of a beat and a tick; or song.ini's
    /// <c>hopo_frequency</c> with YARG's extra tick, and without it.
    /// </summary>
    internal static long[] HopoThresholdsOf(ChartReadOptions options, int resolution) =>
        options.HopoThreshold is { } ticks
            ? [ticks + 1, ticks]
            : [(long)(resolution * HopoBeats), (resolution / 3) + 1];

    /// <exception cref="ChartFormatException">The file isn't a MIDI file, or is malformed.</exception>
    public static ChartDocument Read(byte[] bytes, ChartReadOptions options)
    {
        var (_, resolution, chunks) = MidiFile.ReadChunks(bytes);
        var tracks = chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.ReadEvents(bytes, c)).ToList();
        var tempo = TempoOf(bytes, resolution, tracks);

        var parts = new Dictionary<ChartInstrument, TrackSpans>();
        foreach (var events in tracks)
        {
            if (MidiFile.NameOf(bytes, events) is { } name && MidiLayout.TrackNames.TryGetValue(name, out var instrument) && !parts.ContainsKey(instrument))
                parts[instrument] = TrackSpans.Of(bytes, events);
        }

        bool fiveLane = parts.TryGetValue(ChartInstrument.Drums, out var drums)
            && Enum.GetValues<ChartDifficulty>().Any(d => drums.Notes.ContainsKey(MidiLayout.BaseOf(ChartInstrumentKind.Drums, d) + 5));

        var result = new Dictionary<ChartTrackKey, NoteTrack>();
        foreach (var (instrument, spans) in parts)
        {
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                var track = Build(instrument, difficulty, spans, resolution, fiveLane, options);
                if (track.Notes.Count > 0)
                    result[new ChartTrackKey(instrument, difficulty)] = track;
            }
        }

        return new ChartDocument(tempo, result, null, fiveLane);
    }

    /// <summary>The tempo map, from the tempo and time-signature events of every track.</summary>
    internal static TempoMap TempoOf(byte[] bytes, int resolution, List<List<MidiEvent>> tracks)
    {
        var tempos = new List<TempoChange>();
        var signatures = new List<TimeSignature>();
        foreach (var e in tracks.SelectMany(t => t).Where(e => e.IsMeta))
        {
            var (type, start, length) = MidiFile.MetaOf(bytes, e);
            if (type == 0x51 && length == 3)
                tempos.Add(new TempoChange(e.Tick, (bytes[start] << 16) | (bytes[start + 1] << 8) | bytes[start + 2]));
            else if (type == 0x58 && length >= 2 && bytes[start + 1] < 7)
                signatures.Add(new TimeSignature(e.Tick, bytes[start], 1 << bytes[start + 1]));
        }

        return new TempoMap(resolution, tempos.OrderBy(t => t.Tick), signatures.OrderBy(s => s.Tick));
    }

    private static NoteTrack Build(ChartInstrument instrument, ChartDifficulty difficulty, TrackSpans spans, int resolution, bool fiveLane, ChartReadOptions options)
    {
        var kind = ChartInstruments.KindOf(instrument);
        int b = MidiLayout.BaseOf(kind, difficulty);
        // As YARG reads it, a note held for less than the cutoff is a note, not a sustain.
        long cutoff = SustainCutoff.Of(resolution, options.SustainCutoff);
        long LengthOf(Span span) => span.Length >= cutoff ? span.Length : 0;

        var starPower = spans.Of(MidiLayout.StarPowerPitch).Select(s => new StarPowerPhrase(s.Start, s.Length)).ToList();
        var sysexOpen = spans.SysexFor(difficulty, MidiLayout.SysexOpen);
        var sysexTap = spans.SysexFor(difficulty, MidiLayout.SysexTap);

        FretMarks MarksAt(long tick, int hopoPitch) => new(
            Toggle: false,
            ForceHopo: Span.Covers(spans.Of(hopoPitch), tick),
            ForceStrum: Span.Covers(spans.Of(hopoPitch + 1), tick),
            Tap: Span.Covers(spans.Of(MidiLayout.TapPitch), tick) || Span.Covers(sysexTap, tick));

        long hopoThreshold = options.MidiHopoTicks ?? HopoThresholdsOf(options, resolution)[0];
        switch (kind)
        {
            case ChartInstrumentKind.Drums:
            {
                bool pro = options.ProDrums || Enumerable.Range(MidiLayout.FirstTomPitch, 3).Any(p => spans.Notes.ContainsKey(p));
                var gems = new List<(Gem, NoteFlags)>();
                foreach (var s in spans.Of(b))
                    gems.Add((new Gem(s.Start, 0, ChartNote.OpenLane), NoteFlags.None));
                if (difficulty == ChartDifficulty.Expert)
                {
                    foreach (var s in spans.Of(MidiLayout.DoubleKickPitch))
                        gems.Add((new Gem(s.Start, 0, ChartNote.OpenLane), NoteFlags.DoubleKick));
                }

                for (int lane = 0; lane < (fiveLane ? 5 : 4); lane++)
                {
                    foreach (var s in spans.Of(b + 1 + lane))
                    {
                        // Yellow, blue and green are cymbals unless a tom marker covers them.
                        bool cymbal = pro && !fiveLane && lane >= 1 && !Span.Covers(spans.Of(MidiLayout.FirstTomPitch + lane - 1), s.Start);
                        gems.Add((new Gem(s.Start, 0, lane), cymbal ? NoteFlags.Cymbal : NoteFlags.None));
                    }
                }

                return new NoteTrack(NoteBuilder.Drums(gems, starPower), starPower);
            }

            case ChartInstrumentKind.SixFret:
            {
                var gems = spans.Of(b).Select(s => new Gem(s.Start, LengthOf(s), ChartNote.OpenLane)).ToList();
                for (int lane = 0; lane < 6; lane++)
                    gems.AddRange(spans.Of(b + 1 + lane).Select(s => new Gem(s.Start, LengthOf(s), lane)));
                return new NoteTrack(NoteBuilder.Frets(gems, hopoThreshold, t => MarksAt(t, b + 7), starPower), starPower);
            }

            default:
            {
                var gems = new List<Gem>();
                for (int lane = 0; lane < 5; lane++)
                {
                    // Phase Shift marks an open note as a green note inside an open sysex.
                    gems.AddRange(spans.Of(b + lane).Select(s => new Gem(
                        s.Start, LengthOf(s), lane == 0 && Span.Covers(sysexOpen, s.Start) ? ChartNote.OpenLane : lane)));
                }

                if (spans.HasEnhancedOpens)
                    gems.AddRange(spans.Of(b - 1).Select(s => new Gem(s.Start, LengthOf(s), ChartNote.OpenLane)));
                return new NoteTrack(NoteBuilder.Frets(gems, hopoThreshold, t => MarksAt(t, b + 5), starPower), starPower);
            }
        }
    }

    /// <summary>A note held from <see cref="Start"/> to <see cref="End"/>.</summary>
    private readonly record struct Span(long Start, long End)
    {
        public long Length => End - Start;

        public static bool Covers(List<Span> spans, long tick)
        {
            foreach (var span in spans)
            {
                if (span.Start > tick)
                    return false;
                if (tick < span.End || tick == span.Start)
                    return true;
            }

            return false;
        }
    }

    /// <summary>A track's notes as spans by pitch, and its Phase Shift sysex spans, all in order.</summary>
    private sealed class TrackSpans
    {
        private static readonly List<Span> None = [];

        public Dictionary<int, List<Span>> Notes { get; } = [];

        public Dictionary<(int Difficulty, int Type), List<Span>> Sysex { get; } = [];

        public bool HasEnhancedOpens { get; private set; }

        public List<Span> Of(int pitch) => Notes.GetValueOrDefault(pitch) ?? None;

        public List<Span> SysexFor(ChartDifficulty difficulty, int type) =>
        [
            .. (Sysex.GetValueOrDefault(((int)difficulty, type)) ?? None)
                .Concat(Sysex.GetValueOrDefault((MidiLayout.SysexAllDifficulties, type)) ?? None)
                .OrderBy(s => s.Start),
        ];

        public static TrackSpans Of(byte[] bytes, List<MidiEvent> events)
        {
            var spans = new TrackSpans();
            var held = new Dictionary<int, Queue<long>>();
            var heldSysex = new Dictionary<(int, int), long>();

            foreach (var e in events)
            {
                if (e.Kind is 0x80 or 0x90)
                {
                    int pitch = bytes[e.DataStart];
                    bool on = e.Kind == 0x90 && bytes[e.DataStart + 1] > 0;
                    if (!held.TryGetValue(pitch, out var starts))
                        held[pitch] = starts = new Queue<long>();

                    if (on)
                    {
                        starts.Enqueue(e.Tick);
                    }
                    else if (starts.TryDequeue(out long start))
                    {
                        if (!spans.Notes.TryGetValue(pitch, out var list))
                            spans.Notes[pitch] = list = [];
                        list.Add(new Span(start, e.Tick));
                    }
                }
                else if (e.IsSysex)
                {
                    var data = MidiFile.SysexOf(bytes, e);
                    if (data.Length < 7 || data[0] != 0x50 || data[1] != 0x53 || data[2] != 0 || data[3] != 0)
                        continue;

                    var key = ((int)data[4], (int)data[5]);
                    if (data[6] != 0)
                    {
                        heldSysex.TryAdd(key, e.Tick);
                    }
                    else if (heldSysex.Remove(key, out long start))
                    {
                        if (!spans.Sysex.TryGetValue(key, out var list))
                            spans.Sysex[key] = list = [];
                        list.Add(new Span(start, e.Tick));
                    }
                }
                else if (MidiFile.TextOf(bytes, e, 0x01) is { } text && text.Contains("ENHANCED_OPENS", StringComparison.OrdinalIgnoreCase))
                {
                    spans.HasEnhancedOpens = true;
                }
            }

            // A note never let go is a note without a sustain.
            foreach (var (pitch, starts) in held)
            {
                if (starts.Count == 0)
                    continue;
                if (!spans.Notes.TryGetValue(pitch, out var list))
                    spans.Notes[pitch] = list = [];
                list.AddRange(starts.Select(s => new Span(s, s)));
            }

            foreach (var list in spans.Notes.Values)
                list.Sort((x, y) => x.Start.CompareTo(y.Start));
            foreach (var list in spans.Sysex.Values)
                list.Sort((x, y) => x.Start.CompareTo(y.Start));
            return spans;
        }
    }
}
