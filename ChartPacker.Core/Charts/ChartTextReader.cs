using System.Globalization;
using System.Text;

namespace ChartPacker.Core.Charts;

/// <summary>
/// Reads a <c>.chart</c>: the <c>[Song]</c> resolution and offset, the <c>[SyncTrack]</c> tempo map,
/// and the note sections of the instruments in <see cref="ChartInstrument"/>. Anything else is skipped.
/// </summary>
internal static class ChartTextReader
{
    private const int DefaultResolution = 192;

    // Moonscraper and Clone Hero's default: a note is a natural HOPO within 65/192 of a beat of the one before.
    private const double HopoBeats = 65.0 / 192;

    /// <summary>The instrument part of each note section's name, e.g. <c>ExpertDoubleBass</c>.</summary>
    internal static IReadOnlyDictionary<string, ChartInstrument> SectionInstruments { get; } = new Dictionary<string, ChartInstrument>(StringComparer.OrdinalIgnoreCase)
    {
        ["Single"] = ChartInstrument.Guitar,
        ["DoubleGuitar"] = ChartInstrument.GuitarCoop,
        ["DoubleBass"] = ChartInstrument.Bass,
        ["DoubleRhythm"] = ChartInstrument.Rhythm,
        ["Keyboard"] = ChartInstrument.Keys,
        ["Drums"] = ChartInstrument.Drums,
        ["GHLGuitar"] = ChartInstrument.GhlGuitar,
        ["GHLBass"] = ChartInstrument.GhlBass,
    };

    /// <summary>The name of the note section for <paramref name="key"/>, e.g. <c>ExpertSingle</c>.</summary>
    internal static string SectionName(ChartTrackKey key) =>
        key.Difficulty + SectionInstruments.First(p => p.Value == key.Instrument).Key;

    /// <summary>The track a note section's name is for, if it's one that's read.</summary>
    internal static ChartTrackKey? TrackOf(string sectionName)
    {
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            string prefix = difficulty.ToString();
            if (sectionName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && SectionInstruments.TryGetValue(sectionName[prefix.Length..], out var instrument))
                return new ChartTrackKey(instrument, difficulty);
        }

        return null;
    }

    /// <exception cref="ChartFormatException">The file isn't a <c>.chart</c>.</exception>
    public static ChartDocument Read(byte[] bytes)
    {
        var sections = Sections(Decode(bytes));
        if (sections.Count == 0)
            throw new ChartFormatException("The chart has no sections.");

        int resolution = DefaultResolution;
        TimeSpan? offset = null;
        foreach (var (key, value) in Pairs(sections, "Song"))
        {
            if (key.Equals("Resolution", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) && r > 0)
                resolution = r;
            else if (key.Equals("Offset", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && Math.Abs(seconds) < 86_400)
                offset = TimeSpan.FromSeconds(seconds);
        }

        var tempos = new List<TempoChange>();
        var signatures = new List<TimeSignature>();
        foreach (var e in Events(sections, "SyncTrack"))
        {
            if (e.Type == "B" && e.Args.Length >= 1 && e.Args[0] > 0)
                tempos.Add(new TempoChange(e.Tick, 60_000_000_000L / e.Args[0]));
            else if (e.Type == "TS" && e.Args.Length >= 1 && e.Args[0] > 0 && (e.Args.Length < 2 || e.Args[1] is >= 0 and < 7))
                signatures.Add(new TimeSignature(e.Tick, (int)e.Args[0], 1 << (int)(e.Args.Length >= 2 ? e.Args[1] : 2)));
        }

        var tempo = new TempoMap(resolution, tempos, signatures);
        var trackEvents = new Dictionary<ChartTrackKey, List<ChartEvent>>();
        foreach (var (name, lines) in sections)
        {
            if (TrackOf(name) is not { } key)
                continue;
            if (!trackEvents.TryGetValue(key, out var events))
                trackEvents[key] = events = [];
            events.AddRange(lines.Select(ParseEvent).OfType<ChartEvent>());
        }

        bool fiveLane = trackEvents
            .Where(p => p.Key.Instrument == ChartInstrument.Drums)
            .Any(p => p.Value.Any(e => e.Type == "N" && e.Args.Length >= 1 && e.Args[0] == 5));

        long hopoThreshold = (long)(resolution * HopoBeats);
        var tracks = new Dictionary<ChartTrackKey, NoteTrack>();
        foreach (var (key, events) in trackEvents)
        {
            var track = BuildTrack(key.Instrument, events, hopoThreshold, fiveLane);
            if (track.Notes.Count > 0)
                tracks[key] = track;
        }

        return new ChartDocument(tempo, tracks, offset, fiveLane);
    }

    private static NoteTrack BuildTrack(ChartInstrument instrument, List<ChartEvent> events, long hopoThreshold, bool fiveLane)
    {
        var starPower = events
            .Where(e => e.Type == "S" && e.Args.Length >= 1 && e.Args[0] == 2)
            .Select(e => new StarPowerPhrase(e.Tick, e.Args.Length >= 2 ? Math.Max(0, e.Args[1]) : 0))
            .OrderBy(p => p.Tick)
            .ToList();

        var notes = events.Where(e => e.Type == "N" && e.Args.Length >= 1).ToList();
        var markers = notes.GroupBy(e => e.Tick).ToDictionary(g => g.Key, g => g.Select(e => e.Args[0]).ToHashSet());
        long LengthOf(ChartEvent e) => e.Args.Length >= 2 ? Math.Max(0, e.Args[1]) : 0;

        switch (ChartInstruments.KindOf(instrument))
        {
            case ChartInstrumentKind.Drums:
            {
                var gems = new List<(Gem, NoteFlags)>();
                foreach (var e in notes)
                {
                    int lane = e.Args[0] switch
                    {
                        0 or 32 => ChartNote.OpenLane,
                        >= 1 and <= 4 => (int)e.Args[0] - 1,
                        5 when fiveLane => 4,
                        _ => int.MinValue,
                    };
                    if (lane == int.MinValue)
                        continue;

                    var flags = e.Args[0] == 32 ? NoteFlags.DoubleKick : NoteFlags.None;

                    // Pro drums' cymbal markers 66-68 are for yellow, blue and green.
                    if (!fiveLane && lane is >= 1 and <= 3 && markers[e.Tick].Contains(65 + lane))
                        flags |= NoteFlags.Cymbal;
                    gems.Add((new Gem(e.Tick, 0, lane), flags));
                }

                return new NoteTrack(NoteBuilder.Drums(gems, starPower), starPower);
            }

            case ChartInstrumentKind.SixFret:
            {
                var gems = notes
                    .Select(e => (Event: e, Lane: e.Args[0] switch
                    {
                        // White 1-3, then black 1-2; black 3 comes last.
                        >= 0 and <= 4 => (int)e.Args[0],
                        8 => 5,
                        7 => ChartNote.OpenLane,
                        _ => int.MinValue,
                    }))
                    .Where(x => x.Lane != int.MinValue)
                    .Select(x => new Gem(x.Event.Tick, LengthOf(x.Event), x.Lane));
                return new NoteTrack(NoteBuilder.Frets(gems, hopoThreshold, FretMarksAt(markers), starPower), starPower);
            }

            default:
            {
                var gems = notes
                    .Where(e => e.Args[0] is (>= 0 and <= 4) or 7)
                    .Select(e => new Gem(e.Tick, LengthOf(e), e.Args[0] == 7 ? ChartNote.OpenLane : (int)e.Args[0]));
                return new NoteTrack(NoteBuilder.Frets(gems, hopoThreshold, FretMarksAt(markers), starPower), starPower);
            }
        }
    }

    private static Func<long, FretMarks> FretMarksAt(Dictionary<long, HashSet<long>> markers) => tick =>
    {
        var at = markers.GetValueOrDefault(tick);
        return at is null ? default : new FretMarks(Toggle: at.Contains(5), ForceHopo: false, ForceStrum: false, Tap: at.Contains(6));
    };

    /// <summary>A line <c>tick = TYPE a b</c> of a section, with its numeric arguments.</summary>
    internal readonly record struct ChartEvent(long Tick, string Type, long[] Args);

    internal static ChartEvent? ParseEvent(string line)
    {
        int equals = line.IndexOf('=');
        if (equals < 0 || !long.TryParse(line.AsSpan(0, equals).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long tick))
            return null;

        var parts = line[(equals + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var args = new List<long>(2);
        foreach (string part in parts.Skip(1))
        {
            if (!long.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                break;
            args.Add(value);
        }

        return new ChartEvent(tick, parts[0].ToUpperInvariant(), [.. args]);
    }

    private static IEnumerable<ChartEvent> Events(List<(string Name, List<string> Lines)> sections, string name) =>
        sections.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).SelectMany(s => s.Lines).Select(ParseEvent).OfType<ChartEvent>();

    private static IEnumerable<(string Key, string Value)> Pairs(List<(string Name, List<string> Lines)> sections, string name)
    {
        foreach (string line in sections.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).SelectMany(s => s.Lines))
        {
            int equals = line.IndexOf('=');
            if (equals > 0)
                yield return (line[..equals].Trim(), line[(equals + 1)..].Trim().Trim('"'));
        }
    }

    /// <summary>The <c>[Name]</c> sections and the lines between their braces.</summary>
    internal static List<(string Name, List<string> Lines)> Sections(string text)
    {
        var sections = new List<(string, List<string>)>();
        List<string>? current = null;
        bool inBody = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (!inBody && line.StartsWith('[') && line.EndsWith(']'))
            {
                current = [];
                sections.Add((line[1..^1].Trim(), current));
            }
            else if (line == "{" && current is not null)
            {
                inBody = true;
            }
            else if (line == "}")
            {
                inBody = false;
                current = null;
            }
            else if (inBody)
            {
                current!.Add(line);
            }
        }

        return sections;
    }

    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
