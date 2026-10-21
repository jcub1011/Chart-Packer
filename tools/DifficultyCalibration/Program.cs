using System.Globalization;
using System.Reflection;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charts;
using DifficultyCalibration;

// Measures the difficulty model against the song.ini ratings of a library (see README.md):
//
//   dotnet run -c Release --project tools/DifficultyCalibration -- <folder> [--model <model>] [--variant "label|Name=value,..."]...
//       [--fit] [--attributes] [--csv <file>] [--outliers N] [--song <text>] [--depth N]
//
// The songs are read once, and the parts the model rates kept: fivefret (the default), keys, sixfret, drums or
// prodrums. Each variant is the model's DifficultyParameters.For with the values named changed, and is scored against
// the reported tiers on the fit and held-out sets (split by artist). --fit refits the rank thresholds on the fit set and
// scores both sets with them. Files are only read.

string? root = null;
int depth = 10;
var model = DifficultyInstrument.FiveFret;
var variantTexts = new List<string>();
bool fit = false, attributes = false, brief = false;
string? csv = null, song = null, fitOn = "all";
int outliers = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model":
            model = Enum.Parse<DifficultyInstrument>(args[++i], ignoreCase: true);
            break;
        case "--variant":
            variantTexts.Add(args[++i]);
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

var basis = DifficultyParameters.For(model);
var variants = new List<(string Label, DifficultyParameters Parameters)> { ("default", basis) };
variants.AddRange(variantTexts.Select(t => Variants.Parse(basis, t)));

var parts = (await Library.LoadAsync(root, depth)).Where(p => p.Model == model).ToList();
Console.WriteLine($"model {model}: {string.Join(", ", parts.GroupBy(p => p.Name).Select(g => $"{g.Key} {g.Count()}"))}");
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

        foreach (string rating in NamesOf(rated))
            Console.WriteLine($"-- {rating} alone, midpoints {string.Join("/", Fitting.Midpoints([.. fitSet.Where(r => r.Part.Name == rating)]).Select(t => t.ToString("0.0", CultureInfo.InvariantCulture)))}");
    }

    // Pro drums' rating should seldom differ from the same chart's 4-lane rating: official charts never rate them apart.
    if (model == DifficultyInstrument.ProDrums)
        Console.WriteLine($"-- against the 4-lane view: {Agreement(rated)}");

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

// The parts' ratings by name, the most rated first; those with too few to say much of last, as "other".
static IEnumerable<string> NamesOf(List<Rated> rated) =>
    rated.GroupBy(r => r.Part.Name).OrderByDescending(g => g.Count()).Select(g => g.Key);

// Spearman of each rating, fit and held out, and the official charts on their own; then the official charts' tier
// accuracy over every rating.
static string Brief(List<Rated> rated)
{
    string Rho(Func<Rated, bool> pick)
    {
        var set = rated.Where(pick).ToList();
        return Metrics.Spearman([.. set.Select(r => r.Rank)], [.. set.Select(r => (double)r.Part.Reported)]).ToString("0.000", CultureInfo.InvariantCulture);
    }

    var official = rated.Where(r => r.Part.Official).ToList();
    string accuracy = official.Count == 0 ? "-"
        : $"exact {official.Count(r => Metrics.TierOf(r.Rank) == r.Part.Reported) / (double)official.Count:P0} pm1 {official.Count(r => Math.Abs(Metrics.TierOf(r.Rank) - r.Part.Reported) <= 1) / (double)official.Count:P0} MAE {official.Average(r => Math.Abs(Metrics.TierOf(r.Rank) - r.Part.Reported)):0.00}";
    return string.Join(" | ", NamesOf(rated).Take(4).Select(n => $"{n} fit {Rho(r => r.Part.Name == n && !r.Part.HeldOut)} held {Rho(r => r.Part.Name == n && r.Part.HeldOut)} off {Rho(r => r.Part.Name == n && r.Part.Official)}"))
        + $" | official {accuracy}"
        + $" | top {string.Join(" ", DifficultyAttributes.All.Select(a => $"{a.ToString()[0]}{rated.Count(r => r.Dominant == a)}"))}";
}

// How often Pro drums' tier is the 4-lane view's of the same chart.
static string Agreement(List<Rated> rated)
{
    var drums = DifficultyParameters.For(DifficultyInstrument.Drums);
    var tiers = rated.AsParallel().Select(r => (Pro: Metrics.TierOf(r.Rank), FourLane: DifficultyAnalyzer.Analyze(Output.NotesOf(r.Part, drums), drums).Tier)).ToList();
    return tiers.Count == 0 ? "-" : $"same tier {tiers.Count(t => t.Pro == t.FourLane) / (double)tiers.Count:P0}, within one {tiers.Count(t => Math.Abs(t.Pro - t.FourLane) <= 1) / (double)tiers.Count:P0}, higher {tiers.Count(t => t.Pro > t.FourLane) / (double)tiers.Count:P0}";
}

static void Report(List<Rated> rated, bool attributes)
{
    foreach (string rating in NamesOf(rated))
    {
        var of = rated.Where(r => r.Part.Name == rating).ToList();
        Console.WriteLine($"  {rating}");
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
}

/// <summary>
/// Parameter sets from text: <c>label|Name=value,Name=value</c>, thresholds as <c>a/b/c</c>. Drums' kick and hands
/// parameters can be named as such: <c>KickWeight</c> is <c>StrumWeight</c>, <c>HandsHalfLife</c> is <c>NoteHalfLife</c>.
/// </summary>
internal static class Variants
{
    public static (string Label, DifficultyParameters Parameters) Parse(DifficultyParameters basis, string text)
    {
        string[] split = text.Split('|', 2);
        string label = split[0];
        var parameters = With(basis, split.Length > 1 ? split[1] : split[0]);
        return (label, parameters);
    }

    public static DifficultyParameters With(DifficultyParameters basis, string assignments)
    {
        var copy = (DifficultyParameters)typeof(DifficultyParameters).GetMethod("<Clone>$")!.Invoke(basis, null)!;
        foreach (string assignment in assignments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = assignment.Split('=', 2);
            string alias = pair[0].StartsWith("Kick", StringComparison.Ordinal) ? "Strum" + pair[0][4..]
                : pair[0].StartsWith("Hands", StringComparison.Ordinal) ? "Note" + pair[0][5..]
                : pair[0];
            var property = typeof(DifficultyParameters).GetProperty(pair[0], BindingFlags.Public | BindingFlags.Instance)
                ?? typeof(DifficultyParameters).GetProperty(alias, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"No parameter {pair[0]}.");
            var type = property.PropertyType;
            object value = type == typeof(int) ? int.Parse(pair[1], CultureInfo.InvariantCulture)
                : type == typeof(double) ? double.Parse(pair[1], CultureInfo.InvariantCulture)
                : type == typeof(bool) ? bool.Parse(pair[1])
                : type.IsEnum ? Enum.Parse(type, pair[1], ignoreCase: true)
                : (object)pair[1].Split('/').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToList();
            property.SetValue(copy, value);
        }

        return copy;
    }
}
