using System.Numerics;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Charts;

/// <summary>A <c>.chart</c> converted to a <c>notes.mid</c>.</summary>
/// <param name="Midi">The MIDI file.</param>
/// <param name="Offset">The <c>.chart</c>'s <c>[Song]</c> <c>Offset</c>, which a MIDI file can't carry; song.ini's <c>delay</c> takes it over.</param>
/// <param name="Options">
/// The song.ini settings the MIDI file reads the same as the <c>.chart</c> with: the song's own, with the
/// <c>pro_drums</c> and <c>five_lane_drums</c> that keep its drums laid out as they were, and the
/// <c>sustain_cutoff_threshold</c> that keeps its sustains sustains.
/// </param>
/// <param name="Warnings">Tracks that didn't come out exactly as the <c>.chart</c> has them.</param>
public sealed record ChartConversion(byte[] Midi, TimeSpan? Offset, ChartReadOptions Options, IReadOnlyList<ChartMergeWarning> Warnings)
{
    /// <summary>The <c>pro_drums</c> the MIDI file needs: a MIDI chart's drums are pro unless it's false.</summary>
    public bool? ProDrums => Options.ProDrums;

    /// <summary>
    /// The <c>sustain_cutoff_threshold</c> song.ini needs for the sustains to read as sustains, when the default
    /// a MIDI file has, a third of a beat, would make some of them notes.
    /// </summary>
    public long? SustainCutoff => Options.SustainCutoff;
}

/// <summary>
/// Converts a <c>.chart</c> to a MIDI file in the Rock Band and Clone Hero layout, so that charts of
/// either format can be combined. Both are read as YARG reads them.
/// </summary>
/// <remarks>
/// <para>The resolution becomes the MIDI division, so ticks stay as they are. <c>[SyncTrack]</c> becomes
/// the tempo track, <c>[Events]</c> the <c>EVENTS</c> track (lyrics and phrases a <c>PART VOCALS</c> track),
/// and each instrument's sections one <c>PART</c> track.</para>
/// <para>A MIDI track keeps its difficulties apart only by pitch, so taps become per-difficulty Phase
/// Shift sysex events and opens <c>ENHANCED_OPENS</c> notes. Star power, solos, face-offs and drum fills are
/// shared by every difficulty, and come from the hardest difficulty that has them; so do toms, from
/// the hardest difficulty with a note on the pad at the tick. A difficulty with no cymbal markers
/// at all was never charted for pro drums, so it takes the cymbals it's given without a warning. Drum
/// lanes are Expert's, and a lane an easier difficulty has too is marked as down to it.</para>
/// <para>A MIDI note held for less than a third of a beat isn't a sustain, so where a sustain is
/// shorter the conversion gives the cutoff song.ini needs, and every note is written clear of it.
/// A HOPO is worked out differently in MIDI, and differently by each game, so wherever a chord would
/// read differently a force marker makes it what the <c>.chart</c> says; the result is read back and
/// compared with the <c>.chart</c>.</para>
/// </remarks>
public static class ChartToMidi
{
    private const int VocalPhrasePitch = 105;

    // A note no longer than this, so its end is never further from the last event than a MIDI delta can say.
    private const long LongestNote = 0x0FFFFFF;

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
        [ChartInstrument.GhlRhythm] = "PART RHYTHM GHL",
        [ChartInstrument.GhlCoop] = "PART GUITAR COOP GHL",
    };

    /// <summary>Converts the <c>.chart</c> <paramref name="chartBytes"/>.</summary>
    /// <param name="chartBytes">The <c>.chart</c>.</param>
    /// <param name="options">The song's settings, as <see cref="ChartReadOptions.From"/> reads them.</param>
    /// <exception cref="ChartFormatException">The chart is malformed, or can't be written as MIDI.</exception>
    public static ChartConversion Convert(byte[] chartBytes, ChartReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chartBytes);
        options ??= ChartReadOptions.Default;

        var before = ChartReader.Read(chartBytes, ChartFiles.ChartName, options);
        if (before.Tempo.Resolution > short.MaxValue)
            throw new ChartFormatException("The chart's resolution is too fine for a MIDI file.");

        var sections = ChartTextReader.Sections(ChartTextReader.Decode(chartBytes));
        var byInstrument = new Dictionary<ChartInstrument, Dictionary<ChartDifficulty, List<ChartTextReader.TrackEvent>>>();
        foreach (var (name, lines) in sections.Skip(2))
        {
            if (ChartTextReader.TrackOf(name) is not { } key)
                continue;
            if (!byInstrument.TryGetValue(key.Instrument, out var difficulties))
                byInstrument[key.Instrument] = difficulties = [];
            difficulties[key.Difficulty] = [.. (difficulties.GetValueOrDefault(key.Difficulty) ?? []).Concat(ChartTextReader.TrackEvents(lines))];
        }

        var layout = ChartTextReader.DrumsLayoutOf(sections, options);
        var midiOptions = options;
        if (byInstrument.TryGetValue(ChartInstrument.Drums, out var drumEvents))
        {
            // A MIDI chart's drums are pro unless song.ini says not, and a fifth-pad note can make them five-lane.
            bool fifthPad = drumEvents.Values.Any(l => l.Any(e => e is { Type: 'N', Number: 5 }));
            midiOptions = options with
            {
                ProDrums = layout switch { DrumsLayout.Pro => true, DrumsLayout.FourLane => false, _ => options.ProDrums },
                FiveLaneDrums = layout == DrumsLayout.FiveLane ? true : fifthPad ? false : options.FiveLaneDrums,
            };
        }

        var tracks = new List<(string Name, List<MidiMessage> Messages)> { (string.Empty, Conductor(before.Tempo)) };
        var globalEvents = sections.Where(s => s.Name == "Events").SelectMany(s => s.Lines).ToList();
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
            var part = Part(instrument, difficulties, layout, options);
            tracks.Add((TrackNames[instrument], part.Messages));
            if (part.ShortestSustain is { } ticks && !(shortest <= ticks))
                shortest = ticks;
            if (instrument == ChartInstrument.Drums)
                drums = part;
        }

        // The notes are written for this cutoff, not the song's own, which reads a .chart differently.
        midiOptions = midiOptions with { SustainCutoff = SustainCutoff.For(before.Tempo.Resolution, shortest) };

        var expected = before.Tracks.ToDictionary(p => p.Key, p => (IReadOnlyList<ChartNote>)p.Value.Notes);
        if (drums is not null && drumEvents is not null)
        {
            // Drums never charted for pro drums take the cymbals the hardest difficulty gives them.
            var unmarked = ChartTextReader.Drums(drumEvents, layout, (d, tick, pad) => drums.Unmarked.Contains(d) && drums.Cymbals.TryGetValue((pad, tick), out bool cymbal) ? cymbal : null);
            foreach (var (difficulty, track) in unmarked)
                expected[new ChartTrackKey(ChartInstrument.Drums, difficulty)] = track.Notes;
        }

        TimeSpan TimeOf(long tick) => TimeSpan.FromSeconds(before.Tempo.SecondsAt(tick));

        byte[] midi = Build(before.Tempo.Resolution, tracks);
        var warnings = new List<ChartMergeWarning>();
        for (int pass = 0; ; pass++)
        {
            var reads = NoteCheck.ReadEveryWay(midi, midiOptions);
            bool fixedAny = false;
            warnings.Clear();
            foreach (var (key, notes) in expected)
            {
                // The .chart's notes are read without their accents and ghosts, which the MIDI read back has.
                var result = NoteCheck.Compare(notes, reads, key, NoteFlags.Accent | NoteFlags.Ghost);
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

        return new ChartConversion(midi, before.Offset, midiOptions, warnings);
    }

    private static byte[] Build(int resolution, List<(string Name, List<MidiMessage> Messages)> tracks) =>
        MidiWriter.WriteFile(resolution, [.. tracks.Select(t => MidiWriter.Track(t.Messages))]);

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
    /// <param name="Cymbals">Whether each yellow, blue and fourth pad, by pad and tick, is a cymbal, as every difficulty shares it.</param>
    /// <param name="Unmarked">The drum difficulties without a cymbal marker.</param>
    private sealed record PartTrack(List<MidiMessage> Messages, long? ShortestSustain, IReadOnlyDictionary<(int Pad, long Tick), bool> Cymbals, IReadOnlySet<ChartDifficulty> Unmarked);

    private static PartTrack Part(ChartInstrument instrument, Dictionary<ChartDifficulty, List<ChartTextReader.TrackEvent>> difficulties, DrumsLayout layout, ChartReadOptions options)
    {
        var kind = ChartInstruments.KindOf(instrument);
        var notes = new Notes();
        var messages = new List<MidiMessage> { MidiMessage.Text(0, 0x03, TrackNames[instrument]) };
        bool opens = false, dynamics = false;

        // Markers every difficulty shares, from the hardest difficulty that has any of each.
        var shared = new Dictionary<int, List<(long Start, long End)>>();
        var sharedFrom = new Dictionary<int, ChartDifficulty>();
        void Shared(ChartDifficulty difficulty, int pitch, long start, long end)
        {
            if (sharedFrom.TryGetValue(pitch, out var owner) && owner != difficulty)
                return;
            sharedFrom[pitch] = difficulty;
            if (!shared.TryGetValue(pitch, out var spans))
                shared[pitch] = spans = [];
            spans.Add((start, Math.Max(start + 1, end)));
        }

        // Drum lanes, with the difficulties that have each.
        var lanes = new Dictionary<(int Pitch, long Tick), (long Length, HashSet<ChartDifficulty> Difficulties)>();

        // Toms are shared by every difficulty too: the hardest with a note on the pad at the tick decides.
        var cymbals = new Dictionary<(int Pad, long Tick), bool>();
        var unmarked = new HashSet<ChartDifficulty>();
        foreach (var difficulty in HardestFirst)
        {
            if (!difficulties.TryGetValue(difficulty, out var events))
                continue;

            int b = MidiLayout.BaseOf(kind, difficulty);
            var markers = events.Where(e => e.Type == 'N').GroupBy(e => e.Tick).ToDictionary(g => g.Key, g => g.Select(e => e.Number).ToHashSet());
            var taps = new SortedSet<long>();
            var solos = new Stack<long>();
            if (kind == ChartInstrumentKind.Drums && !markers.Values.Any(at => at.Overlaps([66, 67, 68])))
                unmarked.Add(difficulty);

            foreach (var e in events)
            {
                switch (e.Type)
                {
                    case 'S':
                        Phrase(e, difficulty);
                        continue;

                    case 'E':
                        // A solo ends on the tick of its soloend, which MIDI's solo note must take in; drum mixes go as they are.
                        if (e.Text == "solo")
                            solos.Push(e.Tick);
                        else if (e.Text == "soloend" && solos.TryPop(out long start))
                            Shared(difficulty, MidiLayout.SoloPitch, start, e.Tick + 1);
                        else if (kind == ChartInstrumentKind.Drums && ChartTextEvents.TryParseMix(e.Text, out var mixFor, out _) && mixFor == difficulty)
                            messages.Add(MidiMessage.Text(e.Tick, 0x01, $"[{e.Text}]"));
                        continue;

                    case not 'N':
                        continue;
                }

                int n = e.Number;
                long held = Math.Min(ChartTextReader.SustainOf(e.Length, options), LongestNote);
                switch (kind)
                {
                    case ChartInstrumentKind.Drums:
                    {
                        // Accents and ghosts are for pads, not the kick; an accent wins.
                        int velocity = 100;
                        if (n is >= 1 and <= 5 && markers[e.Tick] is var at)
                        {
                            if (at.Contains(33 + n))
                                velocity = 127;
                            else if (at.Contains(39 + n))
                                velocity = 1;
                        }

                        dynamics |= velocity != 100;
                        if (n is >= 0 and <= 5)
                            notes.Add(b + n, e.Tick, e.Tick + 1, velocity);
                        else if (n == 32 && difficulty == ChartDifficulty.Expert)
                            notes.Add(MidiLayout.DoubleKickPitch, e.Tick, e.Tick + 1, 100);

                        // Yellow, blue and the fourth pad are toms unless a cymbal marker says otherwise.
                        if (layout == DrumsLayout.Pro && n is >= 2 and <= 4)
                            cymbals.TryAdd((n, e.Tick), markers[e.Tick].Contains(64 + n));
                        break;
                    }

                    case ChartInstrumentKind.SixFret:
                    {
                        int? pitch = n switch
                        {
                            >= 0 and <= 4 => b + 1 + n,
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
                            notes.AddGem(b + n, e.Tick, held);
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

            foreach (long tick in taps)
            {
                messages.Add(MidiMessage.PhaseShift(tick, (int)difficulty, MidiLayout.SysexTap, on: true));
                messages.Add(MidiMessage.PhaseShift(tick + 1, (int)difficulty, MidiLayout.SysexTap, on: false));
            }
        }

        foreach (var (pitch, spans) in shared)
        {
            foreach (var (start, end) in spans)
                notes.Add(pitch, start, Math.Min(end, start + LongestNote), 100);
        }

        // A lane's velocity takes it down to its easiest difficulty: 21-30 Easy, 31-40 Medium, 41-50 Hard; otherwise Expert alone.
        foreach (var ((pitch, tick), (length, on)) in lanes)
        {
            int velocity = on.Min() switch { ChartDifficulty.Easy => 25, ChartDifficulty.Medium => 35, ChartDifficulty.Hard => 45, _ => 100 };
            notes.Add(pitch, tick, tick + Math.Min(length, LongestNote), velocity);
        }

        foreach (var ((pad, tick), cymbal) in cymbals)
        {
            if (!cymbal)
                notes.Add(MidiLayout.FirstTomPitch + pad - 2, tick, tick + 1, 100);
        }

        if (opens)
            messages.Add(MidiMessage.Text(0, 0x01, "[ENHANCED_OPENS]"));
        if (dynamics)
            messages.Add(MidiMessage.Text(0, 0x01, "[ENABLE_CHART_DYNAMICS]"));
        var (written, shortest) = notes.Messages();
        messages.AddRange(written);
        return new PartTrack(messages, shortest, cymbals, unmarked);

        void Phrase(ChartTextReader.TrackEvent e, ChartDifficulty difficulty)
        {
            // A .chart's guitar has star power and face-offs; its drums fills and lanes as well.
            bool drums = kind == ChartInstrumentKind.Drums;
            switch (e.Number)
            {
                case 2:
                    Shared(difficulty, MidiLayout.StarPowerPitch, e.Tick, e.Tick + e.Length);
                    break;
                case 0 or 1:
                    Shared(difficulty, MidiLayout.FaceOffPitch + e.Number, e.Tick, e.Tick + e.Length);
                    break;
                case 64 when drums:
                    for (int lane = 0; lane < 5; lane++)
                        Shared(difficulty, MidiLayout.DrumFillPitch + lane, e.Tick, e.Tick + e.Length);
                    break;
                case 65 or 66 or 67 when drums:
                    var key = (e.Number switch { 65 => MidiLayout.RollPitch, 66 => MidiLayout.TrillPitch, _ => MidiLayout.KickLanePitch }, e.Tick);
                    if (!lanes.TryGetValue(key, out var lane0))
                        lanes[key] = lane0 = (e.Length, []);
                    lane0.Difficulties.Add(difficulty);
                    lanes[key] = (Math.Max(lane0.Length, e.Length), lane0.Difficulties);
                    break;
            }
        }
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

        /// <summary>
        /// The notes, and how long the shortest sustain among them is held. The double kick's come after the kick's,
        /// so where both are on a tick YARG, which keeps the first let go, keeps the kick.
        /// </summary>
        public (List<MidiMessage> Messages, long? ShortestSustain) Messages()
        {
            var messages = new List<MidiMessage>();
            long? shortest = null;
            foreach (var (pitch, notes) in _byPitch.OrderBy(p => p.Key == MidiLayout.DoubleKickPitch ? MidiLayout.DoubleKickPitch + 1.5 : p.Key))
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
                        if (end - start >= SustainCutoff.ShortestSustain && !(shortest <= end - start))
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
