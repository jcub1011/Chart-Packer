using System.Globalization;
using ChartShared.Core.Charting.Difficulty;

namespace DifficultyCalibration;

/// <summary>Per-part output: the CSV, and a look inside the parts the model disagrees with most.</summary>
internal static class Output
{
    /// <summary>The note events <paramref name="parameters"/> reads <paramref name="part"/> as.</summary>
    public static IReadOnlyList<DifficultyNote> NotesOf(Part part, DifficultyParameters parameters) =>
        DifficultyNotes.Of(part.Track, part.Tempo, part.EndTick, parameters);

    public static void Csv(string path, IReadOnlyList<Rated> rated)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("song,artist,charter,official,heldout,instrument,difficulty,reported,rank,score,strum,note,rhythm,pattern,endurance,dominant");
        foreach (var r in rated)
        {
            var p = r.Part;
            var a = r.Attributes;
            writer.WriteLine(string.Join(",",
                Field(p.Song), Field(p.Artist), Field(p.Charter), p.Official, p.HeldOut, p.Instrument, p.Difficulty, p.Reported,
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
            Console.WriteLine($"## {Path.GetFileName(p.Song)} [{p.Instrument} {p.Difficulty}] reported {p.Reported}, rank {r.Rank:0.0}, score {r.Score:0.0}, top {r.Dominant}");
            Console.WriteLine($"   attributes {Describe(r.Attributes)}");
            Console.WriteLine($"   {notes.Count} notes; shortest interval {(intervals.Count == 0 ? 0 : intervals.Min()):0.0} ms; under 5 ms {intervals.Count(i => i < 5)}, 10 ms {intervals.Count(i => i < 10)}, 20 ms {intervals.Count(i => i < 20)}, 40 ms {intervals.Count(i => i < 40)}");

            var profile = DifficultyAnalyzer.Analyze(notes, parameters);
            foreach (var span in profile.Contributors().Take(3))
            {
                var at = profile.EnvelopeAt(span.ToMs);
                Console.WriteLine($"   window {span.FromMs / 1000:0.00} s: D {at.Overall:0} {Describe(at.Attributes)}");
            }

            if (profile.Contributors().FirstOrDefault() is { } top)
            {
                double previous = double.NaN;
                foreach (var note in notes.Where(n => n.TimeMs >= top.FromMs - 600 && n.TimeMs < top.ToMs))
                {
                    var lanes = string.Concat(Enumerable.Range(0, 5).Select(l => (note.Lanes & (1 << l)) != 0 ? "GRYBO"[l] : '-'));
                    Console.WriteLine($"     {note.TimeMs / 1000,8:0.000} s  beat {note.Beat,8:0.000}  +{note.TimeMs - previous,6:0.0} ms  {lanes} {note.Kind}{(note.Held > 0 ? $" held {note.Held}" : "")}");
                    previous = note.TimeMs;
                }
            }

            Console.WriteLine();
        }
    }

    private static string Describe(DifficultyAttributes a) =>
        $"strum {a.Strum:0} note {a.Note:0} rhythm {a.Rhythm:0} pattern {a.Pattern:0} endurance {a.Endurance:0}";

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Field(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
