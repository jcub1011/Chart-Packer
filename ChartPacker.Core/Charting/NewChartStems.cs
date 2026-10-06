using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charting;

/// <summary>What stops a new chart's audio files being used as they're assigned.</summary>
public enum NewChartStemProblemKind
{
    /// <summary>A file has no stem.</summary>
    Unassigned,

    /// <summary>Two or more files have the same stem.</summary>
    Shared,
}

/// <summary>A problem with the stems a new chart's audio files are assigned.</summary>
/// <param name="Kind">What's wrong.</param>
/// <param name="Indexes">The files it's about, in order: one unassigned file, or every file sharing <paramref name="Stem"/>.</param>
/// <param name="Stem">The shared stem; <see langword="null"/> for an unassigned file.</param>
public sealed record NewChartStemProblem(NewChartStemProblemKind Kind, IReadOnlyList<int> Indexes, string? Stem);

/// <summary>Assigns a new chart's audio files their stems (charter-spec C1, Audio).</summary>
public static class NewChartStems
{
    /// <summary>The stem a file whose name isn't a stem gets, if nothing else has it.</summary>
    public const string Fallback = "song";

    /// <summary>
    /// The stems for files added to a new chart: each one's own name's, as <see cref="AudioStems.StemOf"/>
    /// reads it, e.g. <c>Drums.ogg</c> is <c>drums</c>. The first file whose name isn't a stem gets
    /// <see cref="Fallback"/> if neither <paramref name="taken"/> nor another file has it; any other
    /// is left unassigned (<see langword="null"/>).
    /// </summary>
    /// <param name="taken">The stems of the files already added.</param>
    /// <param name="paths">The files being added; only their names count.</param>
    /// <remarks>A file named after a stem that's already taken still gets it, so the clash is shown.</remarks>
    public static string?[] Assign(IEnumerable<string?> taken, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentNullException.ThrowIfNull(paths);

        var stems = paths.Select(p => AudioStems.StemOf(Path.GetFileName(p))).ToArray();
        bool fallbackTaken = taken.Concat(stems).Any(s => Fallback.Equals(s, StringComparison.OrdinalIgnoreCase));
        int unnamed = Array.IndexOf(stems, null);
        if (!fallbackTaken && unnamed >= 0)
            stems[unnamed] = Fallback;
        return stems;
    }

    /// <summary>What's wrong with <paramref name="stems"/>, a new chart's files' stems in order: unassigned files first, then shared stems.</summary>
    public static IReadOnlyList<NewChartStemProblem> Problems(IReadOnlyList<string?> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);

        var problems = new List<NewChartStemProblem>();
        for (int i = 0; i < stems.Count; i++)
        {
            if (stems[i] is null)
                problems.Add(new NewChartStemProblem(NewChartStemProblemKind.Unassigned, [i], null));
        }

        var shared = stems
            .Select((stem, index) => (Stem: stem, Index: index))
            .Where(s => s.Stem is not null)
            .GroupBy(s => s.Stem!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in shared)
            problems.Add(new NewChartStemProblem(NewChartStemProblemKind.Shared, [.. group.Select(s => s.Index)], group.Key));

        return problems;
    }
}
