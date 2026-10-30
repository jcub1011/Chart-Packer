namespace ChartShared.Core.Settings;

/// <summary>A node in the settings tree, such as Output › Pack &amp; Unpack.</summary>
public sealed class SettingCategory
{
    public SettingCategory(string id, string label, SettingCategory? parent = null, string? iconKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Id = id;
        Label = label;
        Parent = parent;
        IconKey = iconKey;
        Path = parent is null ? [label] : [.. parent.Path, label];
    }

    /// <summary>Unique within a <see cref="SettingsCatalog"/>.</summary>
    public string Id { get; }

    public string Label { get; }

    /// <summary>The category this one sits under, or <see langword="null"/> at the top level.</summary>
    public SettingCategory? Parent { get; }

    /// <summary>
    /// The resource key of the line icon shown beside the category in the Settings window's list, or <see langword="null"/>
    /// for none. Only top-level categories are listed there, so it's only worth giving one to those.
    /// </summary>
    public string? IconKey { get; }

    /// <summary>The labels from the top-level category down to this one.</summary>
    public IReadOnlyList<string> Path { get; }
}
