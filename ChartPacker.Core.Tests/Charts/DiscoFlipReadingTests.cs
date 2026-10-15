using System.Globalization;
using System.Text;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charts;

/// <summary>
/// Disco flip as the games read it (charter-spec C6 item 7): the same layouts of notes and mix events read from a
/// <c>notes.mid</c>, from a <c>notes.chart</c>, through the model's game view (<see cref="GameView.Project(EditableChart, ChartReadOptions)"/>),
/// and through <see cref="GameDiscoFlips"/>, which the editor draws by, all flip the same notes.
/// </summary>
/// <remarks>
/// Notes are written <c>D@tick:pad</c>, D one of E, M, H and X (Easy to Expert), the pad 0 the kick to 5, or <c>2x</c> for a
/// 2× kick. Mixes are <c>D@tick:d</c>, turning disco flip on, or <c>D@tick:-</c>, turning it off. The flipped notes are
/// <c>D@tick</c>, of the red and yellow notes.
/// </remarks>
[TestClass]
public sealed class DiscoFlipReadingTests
{
    private const string Pro = "Pro", FourLane = "FourLane", FiveLane = "FiveLane";

    [TestMethod]
    [DataRow("A flip on at a difficulty's last note carries into the next one up, until a mix there", Pro,
        "E@480:1 M@0:1 M@480:2 H@0:2 X@0:1", "E@0:d E@1920:- X@0:-", "E@480 M@0 M@480 H@0")]
    [DataRow("A mix after a difficulty's last note isn't read, so it doesn't carry", Pro,
        "M@0:1 H@0:1 H@600:2", "M@480:d", "")]
    [DataRow("A mix on a tick with no note takes effect from the next note", Pro,
        "X@0:1 X@480:1 X@960:2 X@1440:1", "X@240:d X@720:- X@1200:d", "X@480 X@1440")]
    [DataRow("4-lane drums never flip", FourLane,
        "X@480:1 X@480:2", "X@0:d X@960:-", "")]
    [DataRow("5-lane drums never flip", FiveLane,
        "X@480:1 X@480:2 X@480:5", "X@0:d X@960:-", "")]
    [DataRow("An Expert 2x kick is played, and isn't flipped", Pro,
        "X@0:2x X@0:1 X@480:2", "X@0:d", "X@0 X@480")]
    [DataRow("A 2x kick off Expert isn't played, but as a difficulty's last note it reads the mixes up to it", Pro,
        "H@0:1 H@960:2x X@0:1", "H@0:d H@960:-", "H@0")]
    public void EveryReadingFlipsTheSameNotes(string why, string layout, string notes, string mixes, string flipped)
    {
        var parsedNotes = ParseNotes(notes);
        var parsedMixes = ParseMixes(mixes);
        var flips = flipped.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Place).ToHashSet();
        var options = OptionsOf(layout);
        var readings = new List<(string Source, ChartDocument Document)>
        {
            (".chart", ChartReader.Read(ChartFileOf(parsedNotes, parsedMixes), "notes.chart", options)),
        };

        // Only a .chart has a 2x kick off Expert: MIDI's pitch and the model's 2x kick are Expert's.
        var chart = parsedNotes.Any(n => n.DoubleKick && n.Difficulty != ChartDifficulty.Expert) ? null : ModelOf(layout, parsedNotes, parsedMixes);
        if (chart is not null)
        {
            readings.Add((".mid", ChartReader.Read(MidiOf(parsedNotes, parsedMixes), "notes.mid", options)));
            readings.Add(("game view", GameView.Project(chart, options)));
        }

        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            var expected = Expected(parsedNotes, flips, difficulty, layout == Pro);
            foreach (var (source, document) in readings)
            {
                var key = new ChartTrackKey(ChartInstrument.Drums, difficulty);
                var read = document.Tracks.TryGetValue(key, out var track) ? Shape(track.Notes) : [];
                Assert.AreSequenceEqual(expected, read, $"{why}: {difficulty} read from the {source}.");
            }
        }

        if (chart is null)
            return;
        var game = GameView.DiscoFlips(chart, options);
        Assert.AreEqual(layout == Pro, game.IsProDrums);
        foreach (var note in parsedNotes.Where(n => n.Pad is 1 or 2))
            Assert.AreEqual(flips.Contains((note.Difficulty, note.Tick)), game.IsFlipped(note.Difficulty, note.Tick), $"{why}: {note.Difficulty} at {note.Tick}.");
    }

    // The notes a difficulty is read as, by tick and lane: a pad flipped on Pro drums swaps red and yellow.
    private static List<(long Tick, int Lane, bool DoubleKick)> Expected(
        List<(ChartDifficulty Difficulty, long Tick, int Pad, bool DoubleKick)> notes,
        HashSet<(ChartDifficulty, long)> flipped,
        ChartDifficulty difficulty,
        bool pro)
    {
        var expected = new List<(long, int, bool)>();
        foreach (var note in notes.Where(n => n.Difficulty == difficulty))
        {
            if (note.DoubleKick)
            {
                if (difficulty == ChartDifficulty.Expert)
                    expected.Add((note.Tick, ChartNote.OpenLane, true));
                continue;
            }

            bool flip = pro && note.Pad is 1 or 2 && flipped.Contains((difficulty, note.Tick));
            int lane = note.Pad == 0 ? ChartNote.OpenLane : flip ? (note.Pad == 1 ? 1 : 0) : note.Pad - 1;
            expected.Add((note.Tick, lane, false));
        }

        return [.. expected.Order()];
    }

    private static List<(long Tick, int Lane, bool DoubleKick)> Shape(IEnumerable<ChartNote> notes) =>
        [.. notes.Select(n => (n.Tick, n.Lane, (n.Flags & NoteFlags.DoubleKick) != 0)).Order()];

    private static ChartReadOptions OptionsOf(string layout) => layout switch
    {
        Pro => ChartReadOptions.From([new("pro_drums", "True"), new("five_lane_drums", "False")]),
        FourLane => ChartReadOptions.From([new("pro_drums", "False"), new("five_lane_drums", "False")]),
        _ => ChartReadOptions.From([new("five_lane_drums", "True")]),
    };

    private static EditableChart ModelOf(string layout, List<(ChartDifficulty Difficulty, long Tick, int Pad, bool DoubleKick)> notes, List<(ChartDifficulty Difficulty, long Tick, bool On)> mixes)
    {
        var variant = layout switch
        {
            Pro => DrumsVariant.Pro,
            FourLane => DrumsVariant.FourLane,
            _ => DrumsVariant.FiveLane,
        };
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Drums }, variant));
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        foreach (var note in notes)
            drums[note.Difficulty].Notes.Add(new EditableNote(note.Tick, EditableNote.PlainLength, note.Pad, DoubleKick: note.DoubleKick));
        foreach (var mix in mixes)
            drums[mix.Difficulty].Mixes.Add(new DrumMix(mix.Tick, 0, mix.On ? DrumMix.DiscoFlipSetting : string.Empty));
        return chart;
    }

    private static byte[] ChartFileOf(List<(ChartDifficulty Difficulty, long Tick, int Pad, bool DoubleKick)> notes, List<(ChartDifficulty Difficulty, long Tick, bool On)> mixes)
    {
        var text = new StringBuilder("[Song]\n{\n  Resolution = 480\n}\n[SyncTrack]\n{\n  0 = B 120000\n}\n");
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            var lines = mixes.Where(m => m.Difficulty == difficulty)
                .Select(m => (m.Tick, Line: Invariant($"{m.Tick} = E mix {(int)difficulty} drums0{(m.On ? "d" : "")}")))
                .Concat(notes.Where(n => n.Difficulty == difficulty).Select(n => (n.Tick, Line: Invariant($"{n.Tick} = N {(n.DoubleKick ? 32 : n.Pad)} 0"))))
                .OrderBy(l => l.Tick)
                .ToList();
            if (lines.Count == 0)
                continue;
            text.Append(CultureInfo.InvariantCulture, $"[{difficulty}Drums]\n{{\n");
            foreach (var (_, line) in lines)
                text.Append("  ").Append(line).Append('\n');
            text.Append("}\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static byte[] MidiOf(List<(ChartDifficulty Difficulty, long Tick, int Pad, bool DoubleKick)> notes, List<(ChartDifficulty Difficulty, long Tick, bool On)> mixes)
    {
        var midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS");
        foreach (var mix in mixes)
            midi.Text(mix.Tick, Invariant($"[mix {(int)mix.Difficulty} drums0{(mix.On ? "d" : "")}]"));
        foreach (var note in notes)
            midi.Note(note.Tick, note.DoubleKick ? 95 : 60 + (12 * (int)note.Difficulty) + note.Pad);
        return midi.Build();
    }

    private static List<(ChartDifficulty Difficulty, long Tick, int Pad, bool DoubleKick)> ParseNotes(string notes) =>
    [
        .. notes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n =>
        {
            var (difficulty, tick) = Place(n[..n.IndexOf(':')]);
            string pad = n[(n.IndexOf(':') + 1)..];
            return pad == "2x" ? (difficulty, tick, 0, true) : (difficulty, tick, int.Parse(pad, CultureInfo.InvariantCulture), false);
        }),
    ];

    private static List<(ChartDifficulty Difficulty, long Tick, bool On)> ParseMixes(string mixes) =>
    [
        .. mixes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(m =>
        {
            var (difficulty, tick) = Place(m[..m.IndexOf(':')]);
            return (difficulty, tick, m.EndsWith(":d", StringComparison.Ordinal));
        }),
    ];

    // "X@480" as Expert at tick 480.
    private static (ChartDifficulty, long) Place(string text)
    {
        var difficulty = text[0] switch
        {
            'E' => ChartDifficulty.Easy,
            'M' => ChartDifficulty.Medium,
            'H' => ChartDifficulty.Hard,
            _ => ChartDifficulty.Expert,
        };
        return (difficulty, long.Parse(text[2..], CultureInfo.InvariantCulture));
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
