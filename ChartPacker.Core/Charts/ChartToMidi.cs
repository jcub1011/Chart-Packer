using System.Numerics;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charts;

/// <summary>A <c>.chart</c> converted to a <c>notes.mid</c>.</summary>
/// <param name="Midi">The MIDI file.</param>
/// <param name="Offset">The <c>.chart</c>'s <c>[Song]</c> <c>Offset</c>, which a MIDI file can't carry; song.ini's <c>delay</c> takes it over.</param>
/// <param name="ProDrums">The drums are pro drums, so song.ini needs <c>pro_drums = True</c> for their cymbals to read the same.</param>
/// <param name="Warnings">Tracks that didn't come out exactly as the <c>.chart</c> has them.</param>
/// <param name="SustainCutoff">
/// The <c>sustain_cutoff_threshold</c> song.ini needs for the sustains to read as sustains, when the default
/// a MIDI file has, a third of a beat, would make some of them notes.
/// </param>
public sealed record ChartConversion(byte[] Midi, TimeSpan? Offset, bool ProDrums, IReadOnlyList<ChartMergeWarning> Warnings, long? SustainCutoff = null);

/// <summary>
/// Converts a <c>.chart</c> to a MIDI file in the Rock Band and Clone Hero layout, so that charts of
/// either format can be combined.
/// </summary>
/// <remarks>
/// <para>The resolution becomes the MIDI division, so ticks stay as they are. <c>[SyncTrack]</c> becomes
/// the tempo track, <c>[Events]</c> the <c>EVENTS</c> track (lyrics and phrases a <c>PART VOCALS</c> track),
/// and each instrument's sections one <c>PART</c> track.</para>
/// <para>A MIDI track keeps its difficulties apart only by pitch, so taps become per-difficulty Phase
/// Shift sysex events and opens <c>ENHANCED_OPENS</c> notes. Star power, solos and drum fills are
/// shared by every difficulty, and come from the hardest difficulty that has them; so do toms, from
/// the hardest difficulty with a note of that colour at the tick. A difficulty with no cymbal markers
/// at all was never charted for pro drums, so it takes the cymbals it's given without a warning.</para>
/// <para>A MIDI note held for less than a third of a beat isn't a sustain, so where a sustain is
/// shorter the conversion gives the cutoff song.ini needs, and every note is written clear of it.
/// A HOPO is worked out differently in MIDI, and differently by each game, so wherever a chord would
/// read differently a force marker makes it what the <c>.chart</c> says; the result is read back and
/// compared with the <c>.chart</c>.</para>
/// </remarks>
public static class ChartToMidi
{
    private const int FaceOffPitch = 105;
    private const int DrumFillPitch = 120;
    private const int RollPitch = 126;
    private const int TrillPitch = 127;
    private const int VocalPhrasePitch = 105;

    private static readonly ChartDifficulty[] HardestFirst = [ChartDifficulty.Expert, ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy];

    /// <summary>The MIDI track name of each instrument.</summary>
    internal static IReadOnlyDictionary<ChartInstrument, string> TrackNames { get; } = new Dictionary<ChartInstrument, string>
    {
        [ChartInstrument.Guitar] = "PART GUITAR",
        [ChartInstrument.Bass] = "PART BASS",
        [ChartInstrument.Drums] = "PART DRUMS",
        [ChartInstrument.Keys] = "PART KEYS",
        [ChartInstrument.Rhythm] = "PART RHYTHM",
        [ChartInstrument.GuitarCoop] = "PART GUITAR COOP",
        [ChartInstrument.GhlGuitar] = "PART GUITAR GHL",
        [ChartInstrument.GhlBass] = "PART BASS GHL",
    };

    /// <summary>Converts the <c>.chart</c> <paramref name="chartBytes"/>.</summary>
    /// <param name="chartBytes">The <c>.chart</c>.</param>
    /// <param name="options">The song's settings; <c>pro_drums</c> makes its drums pro drums.</param>
    /// <exception cref="ChartFormatException">The chart is malformed, or can't be written as MIDI.</exception>
    public static ChartConversion Convert(byte[] chartBytes, ChartReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chartBytes);
        options ??= ChartReadOptions.Default;

        var before = ChartReader.Read(chartBytes, ChartFiles.ChartName, options);
        if (before.Tempo.Resolution > short.MaxValue)
            throw new ChartFormatException("The chart's resolution is too fine for a MIDI file.");

        var sections = ChartTextReader.Sections(ChartTextReader.Decode(chartBytes));
        var byInstrument = new Dictionary<ChartInstrument, Dictionary<ChartDifficulty, List<string>>>();
        foreach (var (name, lines) in sections)
        {
            if (ChartTextReader.TrackOf(name) is not { } key)
                continue;
            if (!byInstrument.TryGetValue(key.Instrument, out var difficulties))
                byInstrument[key.Instrument] = difficulties = [];
            if (!difficulties.TryGetValue(key.Difficulty, out var body))
                difficulties[key.Difficulty] = body = [];
            body.AddRange(lines);
        }

        bool pro = options.ProDrums || (byInstrument.TryGetValue(ChartInstrument.Drums, out var drumSections)
            && drumSections.Values.SelectMany(l => l).Select(ChartTextReader.ParseEvent).Any(e => e is { Type: "N", Args: [>= 66 and <= 68, ..] }));

        var tracks = new List<(string Name, List<MidiMessage> Messages)> { (string.Empty, Conductor(before.Tempo)) };
        var globalEvents = sections.Where(s => s.Name.Equals("Events", StringComparison.OrdinalIgnoreCase)).SelectMany(s => s.Lines).ToList();
        if (Events(globalEvents) is { } events)
            tracks.Add(("EVENTS", events));
        if (Vocals(globalEvents, before.Tempo.Resolution) is { } vocals)
            tracks.Add(("PART VOCALS", vocals));

        long? shortest = null;
        PartTrack? drums = null;
        foreach (var instrument in Enum.GetValues<ChartInstrument>())
        {
            if (!byInstrument.TryGetValue(instrument, out var difficulties))
                continue;
            var part = Part(instrument, difficulties, before.IsFiveLaneDrums, pro, options);
            tracks.Add((TrackNames[instrument], part.Messages));
            if (part.ShortestSustain is { } ticks && !(shortest <= ticks))
                shortest = ticks;
            if (instrument == ChartInstrument.Drums)
                drums = part;
        }

        long? cutoff = SustainCutoff.For(before.Tempo.Resolution, shortest);
        var readOptions = new ChartReadOptions(pro, cutoff, options.HopoThreshold);
        var expected = before.Tracks.ToDictionary(p => p.Key, p => Expected(p.Key, p.Value.Notes, drums));
        TimeSpan TimeOf(long tick) => TimeSpan.FromSeconds(before.Tempo.SecondsAt(tick));

        byte[] midi = Build(before.Tempo.Resolution, tracks);
        var warnings = new List<ChartMergeWarning>();
        for (int pass = 0; ; pass++)
        {
            var reads = NoteCheck.ReadEveryWay(midi, readOptions);
            bool fixedAny = false;
            warnings.Clear();
            foreach (var (key, notes) in expected)
            {
                var result = NoteCheck.Compare(notes, reads, key);
                string name = TrackNames[key.Instrument];
                if (result.HopoFixes.Count > 0 && pass == 0)
                {
                    tracks.First(t => t.Name == name).Messages.AddRange(NoteCheck.ForceMarkers(ChartInstruments.KindOf(key.Instrument), key.Difficulty, result.HopoFixes));
                    fixedAny = true;
                }

                if (NoteCheck.WarningOf(result, pass, name, key.Difficulty, TimeOf) is { } warning)
                    warnings.Add(warning);
            }

            if (!fixedAny)
                break;
            midi = Build(before.Tempo.Resolution, tracks);
        }

        return new ChartConversion(midi, before.Offset, pro, warnings, cutoff);
    }

    /// <summary>
    /// The notes <paramref name="key"/> should read as: the <c>.chart</c>'s, except that drums never
    /// charted for pro drums, with no cymbal markers, take the cymbals the hardest difficulty gives them.
    /// </summary>
    private static IReadOnlyList<ChartNote> Expected(ChartTrackKey key, IReadOnlyList<ChartNote> notes, PartTrack? drums)
    {
        if (key.Instrument != ChartInstrument.Drums || drums is null || !drums.Unmarked.Contains(key.Difficulty))
            return notes;
        return
        [
            .. notes.Select(n => drums.Cymbals.TryGetValue((n.Lane, n.Tick), out bool cymbal) && cymbal ? n with { Flags = n.Flags | NoteFlags.Cymbal } : n),
        ];
    }

    private static byte[] Build(int resolution, List<(string Name, List<MidiMessage> Messages)> tracks) =>
        MidiWriter.WriteFile(resolution, tracks.Select(t => MidiWriter.Track(t.Messages)).ToList());

    /// <summary>The tempo track: <paramref name="tempo"/>'s time signatures and tempo changes.</summary>
    internal static List<MidiMessage> Conductor(TempoMap tempo)
    {
        var messages = new List<MidiMessage>();
        foreach (var signature in tempo.TimeSignatures)
            messages.Add(MidiMessage.Meta(signature.Tick, 0x58, [(byte)signature.Numerator, (byte)BitOperations.Log2((uint)signature.Denominator), 24, 8]));
        foreach (var change in tempo.Tempos)
        {
            long us = Math.Clamp(change.MicrosecondsPerQuarter, 1, 0xFFFFFF);
            messages.Add(MidiMessage.Meta(change.Tick, 0x51, [(byte)(us >> 16), (byte)(us >> 8), (byte)us]));
        }

        return messages;
    }

    /// <summary>The global events other than lyrics and phrases, as <c>[text]</c>; sections as <c>[section name]</c>.</summary>
    private static List<MidiMessage>? Events(List<string> lines)
    {
        var messages = new List<MidiMessage> { MidiMessage.Text(0, 0x03, "EVENTS") };
        foreach (string line in lines)
        {
            if (VocalReader.TextEvent(line) is not var (tick, text) || VocalReader.IsChartVocalEvent(text))
                continue;
            messages.Add(MidiMessage.Text(tick, 0x01, text.StartsWith('[') ? text : $"[{text}]"));
        }

        return messages.Count > 1 ? messages : null;
    }

    /// <summary>The lyrics, as lyric events inside phrase markers; <see langword="null"/> when there are none.</summary>
    private static List<MidiMessage>? Vocals(List<string> lines, int resolution)
    {
        if (VocalReader.ChartVocals(lines, resolution) is not var (lyrics, phrases))
            return null;

        // The phrases first, so at a tick one phrase ends, the next starts, then its first lyric is sung.
        var messages = new List<MidiMessage> { MidiMessage.Text(0, 0x03, VocalReader.VocalsTrackName) };
        foreach (var phrase in phrases)
        {
            messages.Add(MidiMessage.NoteOn(phrase.Tick, VocalPhrasePitch));
            messages.Add(MidiMessage.NoteOff(phrase.Tick + phrase.Length, VocalPhrasePitch));
        }

        foreach (var lyric in lyrics)
            messages.Add(MidiMessage.Text(lyric.Tick, 0x05, lyric.Text));

        return messages;
    }

    /// <summary>An instrument's track.</summary>
    /// <param name="ShortestSustain">The shortest sustain written, if there's one.</param>
    /// <param name="Cymbals">Whether each yellow, blue and green drum note, by lane and tick, is a cymbal, as every difficulty shares it.</param>
    /// <param name="Unmarked">The drum difficulties without a cymbal marker.</param>
    private sealed record PartTrack(List<MidiMessage> Messages, long? ShortestSustain, IReadOnlyDictionary<(int Lane, long Tick), bool> Cymbals, IReadOnlySet<ChartDifficulty> Unmarked);

    private static PartTrack Part(ChartInstrument instrument, Dictionary<ChartDifficulty, List<string>> difficulties, bool fiveLane, bool pro, ChartReadOptions options)
    {
        var kind = ChartInstruments.KindOf(instrument);
        var notes = new Notes();
        var messages = new List<MidiMessage> { MidiMessage.Text(0, 0x03, TrackNames[instrument]) };
        bool opens = false, dynamics = false;

        // Markers every difficulty shares, from the hardest difficulty that has any of each.
        var shared = new Dictionary<int, List<(long Start, long End)>>();
        void Shared(ChartDifficulty difficulty, int pitch, long start, long end, Dictionary<int, ChartDifficulty> from)
        {
            if (from.TryGetValue(pitch, out var owner) && owner != difficulty)
                return;
            from[pitch] = difficulty;
            if (!shared.TryGetValue(pitch, out var spans))
                shared[pitch] = spans = [];
            spans.Add((start, Math.Max(start + 1, end)));
        }

        var sharedFrom = new Dictionary<int, ChartDifficulty>();

        // Toms are shared by every difficulty too: the hardest with a note of the colour at the tick decides.
        var cymbals = new Dictionary<(int Lane, long Tick), bool>();
        var unmarked = new HashSet<ChartDifficulty>();
        foreach (var difficulty in HardestFirst)
        {
            if (!difficulties.TryGetValue(difficulty, out var lines))
                continue;

            int b = MidiLayout.BaseOf(kind, difficulty);
            var events = lines.Select(ChartTextReader.ParseEvent).OfType<ChartTextReader.ChartEvent>().ToList();
            var markers = events.Where(e => e.Type == "N" && e.Args.Length >= 1).GroupBy(e => e.Tick).ToDictionary(g => g.Key, g => g.Select(e => e.Args[0]).ToHashSet());
            var taps = new SortedSet<long>();
            if (kind == ChartInstrumentKind.Drums && !markers.Values.Any(at => at.Overlaps([66, 67, 68])))
                unmarked.Add(difficulty);

            foreach (var e in events)
            {
                long length = e.Args.Length >= 2 ? Math.Max(0, e.Args[1]) : 0;
                long held = e.Args.Length >= 2 ? ChartTextReader.SustainOf(e.Args[1], options) : 0;
                if (e.Type == "S" && e.Args.Length >= 1)
                {
                    int? pitch = e.Args[0] switch
                    {
                        2 => MidiLayout.StarPowerPitch,
                        0 => FaceOffPitch,
                        1 => FaceOffPitch + 1,
                        64 when kind == ChartInstrumentKind.Drums => DrumFillPitch,
                        65 => RollPitch,
                        66 => TrillPitch,
                        _ => null,
                    };
                    if (pitch == DrumFillPitch)
                    {
                        for (int lane = 0; lane < 5; lane++)
                            Shared(difficulty, DrumFillPitch + lane, e.Tick, e.Tick + length, sharedFrom);
                    }
                    else if (pitch is { } p)
                    {
                        Shared(difficulty, p, e.Tick, e.Tick + length, sharedFrom);
                    }

                    continue;
                }

                if (e.Type != "N" || e.Args.Length < 1)
                    continue;

                long n = e.Args[0];
                switch (kind)
                {
                    case ChartInstrumentKind.Drums:
                    {
                        int velocity = 100;
                        if (n is >= 0 and <= 5 && markers[e.Tick] is var at)
                        {
                            if (at.Contains(33 + n))
                                velocity = 127;
                            else if (at.Contains(39 + n))
                                velocity = 1;
                        }

                        dynamics |= velocity != 100;
                        if (n is >= 0 and <= 4 || (n == 5 && fiveLane))
                            notes.Add(b + (int)n, e.Tick, e.Tick + 1, velocity);
                        else if (n == 32 && difficulty == ChartDifficulty.Expert)
                            notes.Add(MidiLayout.DoubleKickPitch, e.Tick, e.Tick + 1, 100);

                        // Yellow, blue and green are toms unless a cymbal marker says otherwise.
                        if (pro && !fiveLane && n is >= 2 and <= 4)
                            cymbals.TryAdd(((int)n - 1, e.Tick), markers[e.Tick].Contains(64 + n));
                        break;
                    }

                    case ChartInstrumentKind.SixFret:
                    {
                        int? pitch = n switch
                        {
                            >= 0 and <= 4 => b + 1 + (int)n,
                            8 => b + 6,
                            7 => b,
                            _ => null,
                        };
                        if (pitch is { } p)
                            notes.AddGem(p, e.Tick, held);
                        else if (n == 6)
                            taps.Add(e.Tick);
                        break;
                    }

                    default:
                    {
                        if (n is >= 0 and <= 4)
                        {
                            notes.AddGem(b + (int)n, e.Tick, held);
                        }
                        else if (n == 7)
                        {
                            notes.AddGem(b - 1, e.Tick, held);
                            opens = true;
                        }
                        else if (n == 6)
                        {
                            taps.Add(e.Tick);
                        }

                        break;
                    }
                }
            }

            foreach (string line in lines)
            {
                if (VocalReader.TextEvent(line) is not var (tick, text))
                    continue;
                if (text.Equals("solo", StringComparison.OrdinalIgnoreCase))
                    Shared(difficulty, MidiLayout.SoloPitch, tick, tick, sharedFrom);
                else if (text.Equals("soloend", StringComparison.OrdinalIgnoreCase) && shared.TryGetValue(MidiLayout.SoloPitch, out var solos) && solos.Count > 0 && sharedFrom[MidiLayout.SoloPitch] == difficulty)
                    solos[^1] = (solos[^1].Start, Math.Max(solos[^1].Start + 1, tick));
            }

            foreach (long tick in taps)
            {
                messages.Add(MidiMessage.PhaseShift(tick, (int)difficulty, MidiLayout.SysexTap, on: true));
                messages.Add(MidiMessage.PhaseShift(tick + 1, (int)difficulty, MidiLayout.SysexTap, on: false));
            }
        }

        foreach (var (pitch, spans) in shared)
        {
            foreach (var (start, end) in spans)
                notes.Add(pitch, start, end, 100);
        }

        foreach (var ((lane, tick), cymbal) in cymbals)
        {
            if (!cymbal)
                notes.Add(MidiLayout.FirstTomPitch + lane - 1, tick, tick + 1, 100);
        }

        if (opens)
            messages.Add(MidiMessage.Text(0, 0x01, "[ENHANCED_OPENS]"));
        if (dynamics)
            messages.Add(MidiMessage.Text(0, 0x01, "[ENABLE_CHART_DYNAMICS]"));
        var (written, shortest) = notes.Messages();
        messages.AddRange(written);
        return new PartTrack(messages, shortest, cymbals, unmarked);
    }

    /// <summary>
    /// Notes by pitch; one that runs into the next of its pitch is cut short there. A fret note without
    /// a sustain is a tick long, and a sustain held for at least <see cref="SustainCutoff.ShortestSustain"/>.
    /// </summary>
    private sealed class Notes
    {
        private readonly Dictionary<int, SortedDictionary<long, (long End, int Velocity, bool Sustain)>> _byPitch = [];

        /// <summary>A note held from <paramref name="start"/> to <paramref name="end"/>; of two at a tick, the longer.</summary>
        public void Add(int pitch, long start, long end, int velocity)
        {
            var notes = Of(pitch);
            end = Math.Max(start + 1, end);
            if (!notes.TryGetValue(start, out var existing) || existing.End < end)
                notes[start] = (end, velocity, false);
        }

        /// <summary>A fret note held for <paramref name="length"/> ticks, none for 0; of two at a tick, the last, as the chart is read.</summary>
        public void AddGem(int pitch, long start, long length) =>
            Of(pitch)[start] = (length > 0 ? start + length : start + 1, 100, length > 0);

        /// <summary>The notes, and how long the shortest sustain among them is held.</summary>
        public (List<MidiMessage> Messages, long? ShortestSustain) Messages()
        {
            var messages = new List<MidiMessage>();
            long? shortest = null;
            foreach (var (pitch, notes) in _byPitch.OrderBy(p => p.Key))
            {
                var list = notes.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    var (start, (end, velocity, sustain)) = (list[i].Key, list[i].Value);
                    long next = i + 1 < list.Count ? list[i + 1].Key : long.MaxValue;
                    end = Math.Min(end, next);
                    if (sustain)
                    {
                        end = Math.Min(Math.Max(end, start + SustainCutoff.ShortestSustain), next);
                        if (!(shortest <= end - start))
                            shortest = end - start;
                    }

                    messages.Add(MidiMessage.NoteOn(start, pitch, velocity));
                    messages.Add(MidiMessage.NoteOff(end, pitch));
                }
            }

            return (messages, shortest);
        }

        private SortedDictionary<long, (long End, int Velocity, bool Sustain)> Of(int pitch)
        {
            if (!_byPitch.TryGetValue(pitch, out var notes))
                _byPitch[pitch] = notes = [];
            return notes;
        }
    }
}
