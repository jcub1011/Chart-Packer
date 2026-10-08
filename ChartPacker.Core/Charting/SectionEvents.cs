using System.Globalization;

namespace ChartPacker.Core.Charting;

/// <summary>
/// The practice sections of <c>EVENTS</c> (charter-spec C8 item 3): <c>[section &lt;name&gt;]</c>, or Rock Band's
/// <c>[prc_&lt;name&gt;]</c>, with underscores for spaces.
/// </summary>
public static class SectionEvents
{
    private const string SectionPrefix = "section ";
    private const string PracticePrefix = "prc_";

    /// <summary>The names <b>Insert section</b> offers, in the order a song usually has them.</summary>
    public static IReadOnlyList<string> CommonNames { get; } =
    [
        "Intro", "Verse 1", "Pre-Chorus 1", "Chorus 1", "Verse 2", "Pre-Chorus 2", "Chorus 2", "Bridge", "Solo",
        "Breakdown", "Chorus 3", "Outro",
    ];

    /// <summary>Whether <paramref name="text"/> is a section event, as the games read one.</summary>
    public static bool IsSection(string text) => NameOf(text) is not null;

    /// <summary>The name of the section <paramref name="text"/> is (<c>[section Verse 1]</c> or <c>[prc_verse_1]</c>), or null when it isn't one.</summary>
    public static string? NameOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = Inner(text);
        if (trimmed.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[SectionPrefix.Length..].Trim();
        if (trimmed.StartsWith(PracticePrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[PracticePrefix.Length..].Replace('_', ' ');
        return null;
    }

    /// <summary>
    /// The text of a section named <paramref name="name"/>: written as <paramref name="styleOf"/> is, a
    /// <c>[prc_…]</c> or a <c>[section …]</c> event, or as <c>[section …]</c> when it's null, as a renamed section keeps
    /// its style.
    /// </summary>
    public static string TextFor(string name, string? styleOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string trimmed = name.Trim();
        return styleOf is not null && Inner(styleOf).StartsWith(PracticePrefix, StringComparison.OrdinalIgnoreCase)
            ? "[" + PracticePrefix + trimmed.Replace(' ', '_') + "]"
            : "[" + SectionPrefix + trimmed + "]";
    }

    /// <summary>
    /// <paramref name="name"/>, or when a section of <paramref name="chart"/> already has it, the name with the lowest
    /// number from 2 that none has ("Section 2"), as <b>Insert section</b>'s Custom… names a new one to rename.
    /// </summary>
    public static string FreeName(EditableChart chart, string name)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in chart.Events)
        {
            if (NameOf(e.Text) is { } existing)
                taken.Add(existing);
        }

        string trimmed = name.Trim();
        if (!taken.Contains(trimmed))
            return trimmed;
        for (int n = 2; ; n++)
        {
            string numbered = string.Create(CultureInfo.InvariantCulture, $"{trimmed} {n}");
            if (!taken.Contains(numbered))
                return numbered;
        }
    }

    /// <summary>The sections of <paramref name="chart"/>'s <c>EVENTS</c>, in order.</summary>
    public static IReadOnlyList<TextEvent> All(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return [.. chart.Events.Where(static e => IsSection(e.Text))];
    }

    private static string Inner(string text) => text.Trim().TrimStart('[').TrimEnd(']');
}
