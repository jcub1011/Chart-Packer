namespace ChartShared.Core.Generation;

/// <summary>
/// How generator models are named in storage (issue #154): <c>{GeneratorType}{ModelName}.{MODEL_EXT}</c>,
/// where the extension belongs to the kind, so each kind can keep its own file type.
/// </summary>
public static class GeneratorModelNaming
{
    /// <summary>The most characters a model name may hold; the kind prefix and extension add a little more.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The file extension a kind's models are stored with, without the dot. Placeholder kinds use <c>json</c>.</summary>
    public static string GetExtension(GeneratorType type) => type switch
    {
        GeneratorType.Difficulty => "json",
        _ => "json",
    };

    /// <summary>Every extension a stored model file may have, without the dot, for file pickers.</summary>
    public static IReadOnlySet<string> KnownExtensions { get; } =
        Enum.GetValues<GeneratorType>().Select(GetExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file name <paramref name="id"/> is stored under.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/>'s name isn't a valid model name.</exception>
    public static string FileNameFor(GeneratorModelId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return $"{id.Type}{ValidateName(id.Name)}.{GetExtension(id.Type)}";
    }

    /// <summary>
    /// <paramref name="name"/> without leading and trailing whitespace, invalid file-name characters
    /// and control characters, cut to <see cref="MaxNameLength"/>; empty when nothing's left.
    /// </summary>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var kept = name.Trim().Where(c => !invalid.Contains(c) && !char.IsControl(c));
        string cleaned = string.Concat(kept);
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] : cleaned;
    }

    /// <summary>
    /// <paramref name="name"/> as a model is stored under: sanitized, and non-empty.
    /// </summary>
    /// <exception cref="ArgumentException">The name is blank or sanitizes to nothing.</exception>
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("The model name is blank.", nameof(name));

        string cleaned = SanitizeName(name);
        if (string.IsNullOrEmpty(cleaned))
            throw new ArgumentException("The model name has nothing a file can be named.", nameof(name));

        return cleaned;
    }

    /// <summary>
    /// Reads <paramref name="fileName"/> (with or without its folder) as a stored model, or false
    /// when it isn't one: the extension must be its kind's, and the name after the kind prefix must
    /// be a valid model name. The longest kind prefix wins.
    /// </summary>
    public static bool TryParseFileName(string fileName, out GeneratorModelId? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        string name = Path.GetFileName(fileName);
        string extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension))
            return false;

        string stem = Path.GetFileNameWithoutExtension(name);
        foreach (var type in Enum.GetValues<GeneratorType>().OrderByDescending(t => t.ToString().Length))
        {
            string prefix = type.ToString();
            if (!stem.StartsWith(prefix, StringComparison.Ordinal)
                || !string.Equals(extension.TrimStart('.'), GetExtension(type), StringComparison.OrdinalIgnoreCase))
                continue;

            string rest = stem[prefix.Length..];
            if (!string.IsNullOrEmpty(rest) && SanitizeName(rest) == rest)
            {
                id = new GeneratorModelId(type, rest);
                return true;
            }
        }

        return false;
    }
}
