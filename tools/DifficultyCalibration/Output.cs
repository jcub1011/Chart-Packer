using System.Globalization;
using ChartShared.Core.Charting.Difficulty;

namespace DifficultyCalibration;

/// <summary>Per-part output: the CSV, and a look inside the parts the model disagrees with most.</summary>
internal static class Output
{
    /// <summary>The note events <paramref name="parameters"/> reads <paramref name="part"/> as.</summary>
    public static IReadOnlyList<DifficultyNote> NotesOf(Part part, DifficultyParameters parameters) =>
        DifficultyNotes.Of(part.Track, part.Tempo, part.EndTick, parameters, part.FreeLanes);

    public static void Csv(string path, IReadOnlyList<Rated> rated)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("song,artist,charter,official,heldout,instrument,key,difficulty,reported,rank,score,strum,note,rhythm,pattern,endurance,dominant");
        foreach (var r in rated)
        {
            var p = r.Part;
            var a = r.Attributes;
            writer.WriteLine(string.Join(",",
                Field(p.Song), Field(p.Artist), Field(p.Charter), p.Official, p.HeldOut, p.Instrument, p.Key, p.Difficulty, p.Reported,
                F(r.Rank), F(r.Score), F(a.Strum), F(a.Note), F(a.Rhythm), F(a.Pattern), F(a.Endurance), r.Dominant));
        }
    }

    /// <summary>
    /// The <paramref name="count"/> parts furthest from their reported tier, and those whose song has
    /// <paramref name="song"/> in its path: the shortest intervals, and where and why the hardest windows are hard.
    /// </summary>
    public static void Diagnose(IReadOnlyList<Rated> rated, DifficultyParameters parameters, int count, string? song)
    {
        var picked = rated.OrderByDescending(r => Math.Abs(r.Rank - r.Part.Reported)).Take(count)
            .Concat(song is null ? [] : rated.Where(r => r.Part.Song.Contains(song, StringComparison.OrdinalIgnoreCase)))
            .Distinct();
        foreach (var r in picked)
        {
            var p = r.Part;
            var notes = NotesOf(p, parameters);
            var intervals = notes.Zip(notes.Skip(1), (a, b) => b.TimeMs - a.TimeMs).ToList();
            Console.WriteLine($"## {Path.GetFileName(p.Song)} [{p.Name} {p.Difficulty}] reported {p.Reported}, rank {r.Rank:0.0}, score {r.Score:0.0}, top {NameOf(r.Dominant, p.Model)}");
            Console.WriteLine($"   attributes {Describe(r.Attributes, p.Model)}");
            Console.WriteLine($"   {notes.Count} notes; shortest interval {(intervals.Count == 0 ? 0 : intervals.Min()):0.0} ms; under 5 ms {intervals.Count(i => i < 5)}, 10 ms {intervals.Count(i => i < 10)}, 20 ms {intervals.Count(i => i < 20)}, 40 ms {intervals.Count(i => i < 40)}");

            var profile = DifficultyAnalyzer.Analyze(notes, parameters);
            foreach (var span in profile.Contributors().Take(3))
            {
                var at = profile.EnvelopeAt(span.ToMs);
                Console.WriteLine($"   window {span.FromMs / 1000:0.00} s: D {at.Overall:0} {Describe(at.Attributes, p.Model)}");
            }

            if (profile.Contributors().FirstOrDefault() is { } top)
            {
                double previous = double.NaN;
                foreach (var note in notes.Where(n => n.TimeMs >= top.FromMs - 600 && n.TimeMs < top.ToMs))
                {
                    string lanes = LanesOf(note.Lanes, p.Model);
                    Console.WriteLine($"     {note.TimeMs / 1000,8:0.000} s  beat {note.Beat,8:0.000}  +{note.TimeMs - previous,6:0.0} ms  {lanes} {note.Kind}{(note.Held > 0 ? $" held {note.Held}" : "")}");
                    previous = note.TimeMs;
                }
            }

            Console.WriteLine();
        }
    }

    private static string Describe(DifficultyAttributes a, DifficultyInstrument model) =>
        string.Join(" ", DifficultyAttributes.All.Select(k => $"{NameOf(k, model)} {a[k]:0}"));

    /// <summary>An attribute's name for <paramref name="model"/>: drums' first two are the kick and the hands.</summary>
    public static string NameOf(DifficultyAttribute attribute, DifficultyInstrument model) => (attribute, model.IsDrums()) switch
    {
        (DifficultyAttribute.Strum, true) => "kick",
        (DifficultyAttribute.Note, true) => "hands",
        _ => attribute.ToString().ToLowerInvariant(),
    };

    // A note's lanes as letters, each lane in its place: GRYBO for five frets, the white then the black frets for six,
    // and for drums the pads (a 5-lane chart's fifth as 5, Pro cymbals in lower case) and the kick.
    private static string LanesOf(byte lanes, DifficultyInstrument model)
    {
        string names = model switch
        {
            DifficultyInstrument.SixFret => "WWWBBB",
            DifficultyInstrument.Drums => "RYBG5..K",
            DifficultyInstrument.ProDrums => "RYBGybgK",
            _ => "GRYBO",
        };
        return string.Concat(Enumerable.Range(0, names.Length).Select(b => (lanes & (1 << b)) != 0 ? names[b] : '-'));
    }

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Field(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
