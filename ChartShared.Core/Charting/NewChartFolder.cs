using System.Globalization;
using System.Text;
using ChartShared.Core.Resources;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Charting;

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

    /// <summary>
    /// The folder name a new chart starts with: <c>&lt;artist&gt; - &lt;name&gt;</c>, or whichever of the two isn't
    /// empty, with each character a path can't hold replaced by <c>_</c>, runs of dots made one and the trailing dots
    /// and spaces taken off. Empty when both are.
    /// </summary>
    public static string NameFor(string artist, string name)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(name);
        string[] parts = [.. new[] { artist.Trim(), name.Trim() }.Where(static p => p.Length > 0)];
        var folder = new StringBuilder(string.Join(" - ", parts));
        for (int i = 0; i < folder.Length; i++)
        {
            char c = folder[i];
            if (c < 0x20 || c == 0x7F || FilenameRules.ReservedChars.Contains(c))
                folder[i] = '_';
        }

        string text = folder.ToString();
        while (text.Contains("..", StringComparison.Ordinal))
            text = text.Replace("..", ".", StringComparison.Ordinal);
        return text.TrimEnd('.', ' ');
    }
}
