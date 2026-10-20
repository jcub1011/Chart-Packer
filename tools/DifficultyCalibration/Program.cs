using System.Globalization;
using System.Reflection;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charts;
using DifficultyCalibration;

// Measures the difficulty model against the song.ini ratings of a library (see README.md):
//
//   dotnet run -c Release --project tools/DifficultyCalibration -- <folder> [--variant "label|Name=value,..."]...
//       [--fit] [--attributes] [--csv <file>] [--outliers N] [--song <text>] [--depth N]
//
// The songs are read once; each variant is DifficultyParameters.Default with the values named changed, and is scored
// against the reported tiers on the fit and held-out sets (split by artist). --fit refits the rank thresholds on the
// fit set and scores both sets with them. Files are only read.

string? root = null;
int depth = 10;
var variants = new List<(string Label, DifficultyParameters Parameters)> { ("default", DifficultyParameters.Default) };
bool fit = false, attributes = false, brief = false;
string? csv = null, song = null, fitOn = "all";
int outliers = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--variant":
            variants.Add(Variants.Parse(args[++i]));
            break;
        case "--fit":
            fit = true;
            break;
        case "--fit-on":
            fitOn = args[++i];
            break;
        case "--brief":
            brief = true;
            break;
        case "--attributes":
            attributes = true;
            break;
        case "--csv":
            csv = args[++i];
            break;
        case "--outliers":
            outliers = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--song":
            song = args[++i];
            break;
        case "--depth":
            depth = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            root = args[i];
            break;
    }
}

if (root is null)
{
    Console.Error.WriteLine("Pass the library folder.");
    return 1;
}

var parts = await Library.LoadAsync(root, depth);
foreach (var group in parts.GroupBy(p => p.Official ? "official" : "community"))
    Console.WriteLine($"{group.Key}: {group.Count()} parts, {group.Count(p => p.HeldOut)} held out");
Console.WriteLine($"official charters: {string.Join(", ", parts.Where(p => p.Official).GroupBy(p => p.Charter).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} ({g.Count()})"))}");
Console.WriteLine();

List<Rated> last = [];
foreach (var (label, parameters) in variants)
{
    var rated = Evaluate(parts, parameters);
    last = rated;
    if (brief)
    {
        Console.WriteLine($"{label,-28} {Brief(rated)}");
        continue;
    }

    Console.WriteLine($"== {label}");
    Report(rated, attributes);
    if (fit)
    {
        var fitSet = rated.Where(r => !r.Part.HeldOut && (fitOn != "official" || r.Part.Official)).ToList();
        Console.WriteLine($"-- fit on {fitOn}: median score and count by tier {Fitting.Medians(fitSet)}");
        foreach (var (name, thresholds) in new[] { ("midpoints", Fitting.Midpoints(fitSet)), ("least error", Fitting.Thresholds(fitSet)) })
        {
            var refit = parameters with { RankThresholds = thresholds };
            Console.WriteLine($"-- {name} thresholds {string.Join("/", thresholds.Select(t => t.ToString("0.0", CultureInfo.InvariantCulture)))}, soft cap {refit.SoftCap:0.0}");
            Report([.. rated.Select(r => r with { Rank = RankCurve.Continuous(r.Score, refit) })], false);
        }

        foreach (var instrument in new[] { ChartInstrument.Guitar, ChartInstrument.Bass })
            Console.WriteLine($"-- {instrument} alone, midpoints {string.Join("/", Fitting.Midpoints([.. fitSet.Where(r => r.Part.Instrument == instrument)]).Select(t => t.ToString("0.0", CultureInfo.InvariantCulture)))}");
    }

    Console.WriteLine();
}

if (csv is not null)
    Output.Csv(csv, last);
if (outliers > 0 || song is not null)
    Output.Diagnose(last, variants[^1].Parameters, outliers, song);
return 0;

static List<Rated> Evaluate(List<Part> parts, DifficultyParameters parameters)
{
    var rated = new Rated[parts.Count];
    var weights = new[] { parameters.StrumWeight, parameters.NoteWeight, parameters.RhythmWeight, parameters.PatternWeight, parameters.EnduranceWeight };
    Parallel.For(0, parts.Count, i =>
    {
        var part = parts[i];
        var profile = DifficultyAnalyzer.Analyze(Output.NotesOf(part, parameters), parameters);
        var a = profile.ChartAttributes;
        var dominant = DifficultyAttributes.All.MaxBy(k => weights[(int)k] * Math.Pow(Math.Max(0, a[k]), parameters.PowerMean));
        rated[i] = new Rated(part, profile.ChartScore, profile.Rank, a, dominant);
    });
    return [.. rated];
}

// Spearman of guitar and bass, fit and held out, and the official charts on their own.
static string Brief(List<Rated> rated)
{
    string Rho(Func<Rated, bool> pick)
    {
        var set = rated.Where(pick).ToList();
        return Metrics.Spearman([.. set.Select(r => r.Rank)], [.. set.Select(r => (double)r.Part.Reported)]).ToString("0.000", CultureInfo.InvariantCulture);
    }

    return $"G fit {Rho(r => r.Part.Instrument == ChartInstrument.Guitar && !r.Part.HeldOut)} held {Rho(r => r.Part.Instrument == ChartInstrument.Guitar && r.Part.HeldOut)} off {Rho(r => r.Part.Instrument == ChartInstrument.Guitar && r.Part.Official)}"
        + $" | B fit {Rho(r => r.Part.Instrument == ChartInstrument.Bass && !r.Part.HeldOut)} held {Rho(r => r.Part.Instrument == ChartInstrument.Bass && r.Part.HeldOut)} off {Rho(r => r.Part.Instrument == ChartInstrument.Bass && r.Part.Official)}"
        + $" | top S{rated.Count(r => r.Dominant == DifficultyAttribute.Strum)} N{rated.Count(r => r.Dominant == DifficultyAttribute.Note)} R{rated.Count(r => r.Dominant == DifficultyAttribute.Rhythm)} P{rated.Count(r => r.Dominant == DifficultyAttribute.Pattern)} E{rated.Count(r => r.Dominant == DifficultyAttribute.Endurance)}";
}

static void Report(List<Rated> rated, bool attributes)
{
    foreach (var instrument in new[] { ChartInstrument.Guitar, ChartInstrument.Bass })
    {
        var of = rated.Where(r => r.Part.Instrument == instrument).ToList();
        Console.WriteLine($"  {instrument}");
        foreach (var (name, pick) in new (string, Func<Rated, bool>)[] { ("all", _ => true), ("official", r => r.Part.Official), ("community", r => !r.Part.Official) })
        {
            var set = of.Where(pick).ToList();
            Console.WriteLine($"    {name,-9} fit {Metrics.Line([.. set.Where(r => !r.Part.HeldOut)])}");
            Console.WriteLine($"    {"",-9} held {Metrics.Line([.. set.Where(r => r.Part.HeldOut)])}");
        }

        Console.WriteLine($"    tiers   {Metrics.Tiers(of)}");
        Console.WriteLine($"    top     {Metrics.Dominance(of)}");
        if (attributes)
        {
            foreach (string line in Metrics.Attributes(of))
                Console.WriteLine(line);
        }
    }

    var others = rated.Where(r => r.Part.Instrument is not (ChartInstrument.Guitar or ChartInstrument.Bass)).ToList();
    if (others.Count > 0)
        Console.WriteLine($"  Rhythm/Coop all {Metrics.Line(others)}");
}

/// <summary>Parameter sets from text: <c>label|Name=value,Name=value</c>, thresholds as <c>a/b/c</c>.</summary>
internal static class Variants
{
    public static (string Label, DifficultyParameters Parameters) Parse(string text)
    {
        string[] split = text.Split('|', 2);
        string label = split[0];
        var parameters = With(DifficultyParameters.Default, split.Length > 1 ? split[1] : split[0]);
        return (label, parameters);
    }

    public static DifficultyParameters With(DifficultyParameters basis, string assignments)
    {
        var copy = (DifficultyParameters)typeof(DifficultyParameters).GetMethod("<Clone>$")!.Invoke(basis, null)!;
        foreach (string assignment in assignments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = assignment.Split('=', 2);
            var property = typeof(DifficultyParameters).GetProperty(pair[0], BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"No parameter {pair[0]}.");
            object value = property.PropertyType == typeof(int) ? int.Parse(pair[1], CultureInfo.InvariantCulture)
                : property.PropertyType == typeof(double) ? double.Parse(pair[1], CultureInfo.InvariantCulture)
                : (object)pair[1].Split('/').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToList();
            property.SetValue(copy, value);
        }

        return copy;
    }
}
