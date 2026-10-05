using System.Globalization;
using ChartPacker.Core.Resources;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Charting;

/// <summary>Why a new chart can't be created where it's asked to be.</summary>
public enum NewChartFolderProblemKind
{
    /// <summary>No parent folder is chosen.</summary>
    NoParent,

    /// <summary>The parent folder doesn't exist.</summary>
    ParentMissing,

    /// <summary>The folder name breaks the <c>.sng</c> filename rules; the problem's reason says how.</summary>
    InvalidName,

    /// <summary>A file or folder already has the name. It's never used or overwritten.</summary>
    Exists,
}

/// <summary>Why a new chart can't be created where it's asked to be.</summary>
/// <param name="Kind">What's wrong.</param>
/// <param name="Reason">For <see cref="NewChartFolderProblemKind.InvalidName"/>, a short user-facing explanation.</param>
public sealed record NewChartFolderProblem(NewChartFolderProblemKind Kind, string? Reason = null);

/// <summary>Where a new chart's folder goes (charter-spec C1, Create).</summary>
public static class NewChartFolder
{
    /// <summary>
    /// What stops the new chart's folder being made as <paramref name="name"/> in <paramref name="parent"/>,
    /// or <see langword="null"/> when nothing does. The name is checked against the <c>.sng</c>
    /// filename rules, as one segment, since packing names the <c>.sng</c> after it.
    /// </summary>
    public static NewChartFolderProblem? ProblemOf(string? parent, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (string.IsNullOrWhiteSpace(parent))
            return new NewChartFolderProblem(NewChartFolderProblemKind.NoParent);

        if (name.Contains('/', StringComparison.Ordinal))
            return new NewChartFolderProblem(NewChartFolderProblemKind.InvalidName,
                string.Format(CultureInfo.CurrentCulture, FindingMessages.ReasonReservedCharacterFormat, '/'));
        if (FilenameRules.FindProblem(name) is { } problem)
            return new NewChartFolderProblem(NewChartFolderProblemKind.InvalidName, problem.Reason);

        if (!Directory.Exists(parent))
            return new NewChartFolderProblem(NewChartFolderProblemKind.ParentMissing);

        string target = Path.Combine(parent, name);
        if (Directory.Exists(target) || File.Exists(target))
            return new NewChartFolderProblem(NewChartFolderProblemKind.Exists);

        return null;
    }
}
