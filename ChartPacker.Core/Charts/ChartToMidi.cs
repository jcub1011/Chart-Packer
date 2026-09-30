using System.Numerics;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charts;

/// <summary>A <c>.chart</c> converted to a <c>notes.mid</c>.</summary>
/// <param name="Midi">The MIDI file.</param>
/// <param name="Offset">The <c>.chart</c>'s <c>[Song]</c> <c>Offset</c>, which a MIDI file can't carry; song.ini's <c>delay</c> takes it over.</param>
/// <param name="ProDrums">The drums are pro drums, so song.ini needs <c>pro_drums = True</c> for their cymbals to read the same.</param>
/// <param name="Warnings">Tracks that didn't come out exactly as the <c>.chart</c> has them.</param>
public sealed record ChartConversion(byte[] Midi, TimeSpan? Offset, bool ProDrums, IReadOnlyList<ChartMergeWarning> Warnings);

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
/// shared by every difficulty, and come from the hardest difficulty that has them. A HOPO is
/// worked out differently in MIDI, so wherever a chord would read differently a force marker makes
/// it what the <c>.chart</c> says; the result is read back and compared with the <c>.chart</c>.</para>
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

        foreach (var instrument in Enum.GetValues<ChartInstrument>())
        {
            if (byInstrument.TryGetValue(instrument, out var difficulties))
                tracks.Add((TrackNames[instrument], Part(instrument, difficulties, before.IsFiveLaneDrums, pro)));
        }

        var readOptions = new ChartReadOptions(pro);
        byte[] midi = Build(before.Tempo.Resolution, tracks);
        var warnings = new List<ChartMergeWarning>();
        for (int pass = 0; ; pass++)
        {
            var after = ChartReader.Read(midi, ChartFiles.MidiName, readOptions);
            bool fixedAny = false;
            warnings.Clear();
            foreach (var (key, track) in before.Tracks)
            {
                var result = NoteCheck.Compare(track.Notes, after.Tracks.GetValueOrDefault(key)?.Notes);
                string name = TrackNames[key.Instrument];
                if (result.HopoFixes.Count > 0 && pass == 0)
                {
                    tracks.First(t => t.Name == name).Messages.AddRange(NoteCheck.ForceMarkers(ChartInstruments.KindOf(key.Instrument), key.Difficulty, result.HopoFixes));
                    fixedAny = true;
                }

                // Force markers that didn't take (another marker overrules them) leave the HOPOs changed.
                var issue = result.Issue;
                if (result.HopoFixes.Count > 0 && pass > 0 && !(issue >= ChartMergeIssue.MarkingsChanged))
                    issue = ChartMergeIssue.MarkingsChanged;
                if (issue is { } found)
                    warnings.Add(new ChartMergeWarning(found, name, key.Difficulty));
            }

            if (!fixedAny)
                break;
            midi = Build(before.Tempo.Resolution, tracks);
        }

        return new ChartConversion(midi, before.Offset, pro, warnings);
    }

    private static byte[] Build(int resolution, List<(string Name, List<MidiMessage> Messages)> tracks) =>
        MidiWriter.WriteFile(resolution, tracks.Select(t => MidiWriter.Track(t.Messages)).ToList());

    private static List<MidiMessage> Conductor(TempoMap tempo)
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

        var messages = new List<MidiMessage> { MidiMessage.Text(0, 0x03, VocalReader.VocalsTrackName) };
        foreach (var lyric in lyrics)
            messages.Add(MidiMessage.Text(lyric.Tick, 0x05, lyric.Text));
        foreach (var phrase in phrases)
        {
            messages.Add(MidiMessage.NoteOn(phrase.Tick, VocalPhrasePitch));
            messages.Add(MidiMessage.NoteOff(phrase.Tick + phrase.Length, VocalPhrasePitch));
        }

        return messages;
    }

    private static List<MidiMessage> Part(ChartInstrument instrument, Dictionary<ChartDifficulty, List<string>> difficulties, bool fiveLane, bool pro)
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
        var toms = new HashSet<(int Pitch, long Tick)>();
        foreach (var difficulty in HardestFirst)
        {
            if (!difficulties.TryGetValue(difficulty, out var lines))
                continue;

            int b = MidiLayout.BaseOf(kind, difficulty);
            var events = lines.Select(ChartTextReader.ParseEvent).OfType<ChartTextReader.ChartEvent>().ToList();
            var markers = events.Where(e => e.Type == "N" && e.Args.Length >= 1).GroupBy(e => e.Tick).ToDictionary(g => g.Key, g => g.Select(e => e.Args[0]).ToHashSet());
            var taps = new SortedSet<long>();

            foreach (var e in events)
            {
                long length = e.Args.Length >= 2 ? Math.Max(0, e.Args[1]) : 0;
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
                        if (pro && !fiveLane && n is >= 2 and <= 4 && !markers[e.Tick].Contains(64 + n))
                            toms.Add((MidiLayout.FirstTomPitch + (int)n - 2, e.Tick));
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
                            notes.Add(p, e.Tick, e.Tick + length, 100);
                        else if (n == 6)
                            taps.Add(e.Tick);
                        break;
                    }

                    default:
                    {
                        if (n is >= 0 and <= 4)
                        {
                            notes.Add(b + (int)n, e.Tick, e.Tick + length, 100);
                        }
                        else if (n == 7)
                        {
                            notes.Add(b - 1, e.Tick, e.Tick + length, 100);
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

        foreach (var (pitch, tick) in toms)
            notes.Add(pitch, tick, tick + 1, 100);

        if (opens)
            messages.Add(MidiMessage.Text(0, 0x01, "[ENHANCED_OPENS]"));
        if (dynamics)
            messages.Add(MidiMessage.Text(0, 0x01, "[ENABLE_CHART_DYNAMICS]"));
        messages.AddRange(notes.Messages());
        return messages;
    }

    /// <summary>Notes by pitch; one that runs into the next of its pitch is cut short there.</summary>
    private sealed class Notes
    {
        private readonly Dictionary<int, SortedDictionary<long, (long End, int Velocity)>> _byPitch = [];

        public void Add(int pitch, long start, long end, int velocity)
        {
            if (!_byPitch.TryGetValue(pitch, out var notes))
                _byPitch[pitch] = notes = [];
            end = Math.Max(start + 1, end);
            if (!notes.TryGetValue(start, out var existing) || existing.End < end)
                notes[start] = (end, velocity);
        }

        public IEnumerable<MidiMessage> Messages()
        {
            foreach (var (pitch, notes) in _byPitch.OrderBy(p => p.Key))
            {
                var list = notes.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    var (start, (end, velocity)) = (list[i].Key, list[i].Value);
                    if (i + 1 < list.Count)
                        end = Math.Min(end, list[i + 1].Key);
                    yield return MidiMessage.NoteOn(start, pitch, velocity);
                    yield return MidiMessage.NoteOff(end, pitch);
                }
            }
        }
    }
}
