namespace ChartShared.Core.Generation;

/// <summary>
/// How generator models are named in storage (issue #154): <c>{GeneratorType}\{ModelName}.{MODEL_EXT}</c>,
/// where the type is a subfolder and the extension belongs to the kind, so each kind keeps its own file type.
/// </summary>
public static class GeneratorModelNaming
{
    /// <summary>The most characters a model name may hold; the type folder and extension add a little more.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The file extension a kind's models are stored with, without the dot. Placeholder kinds use <c>json</c>.</summary>
    public static string GetExtension(GeneratorType type) => type switch
    {
        GeneratorType.Difficulty => "json",
        GeneratorType.NeuralDifficulty => "neuralpack",
        _ => "json",
    };

    /// <summary>Every extension a stored model file may have, without the dot, for file pickers.</summary>
    public static IReadOnlySet<string> KnownExtensions { get; } =
        Enum.GetValues<GeneratorType>().Select(GetExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file name <paramref name="id"/> is stored under within its type folder.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/>'s name isn't a valid model name.</exception>
    public static string FileNameFor(GeneratorModelId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return $"{ValidateName(id.Name)}.{GetExtension(id.Type)}";
    }

    /// <summary>The storage-relative path <paramref name="id"/> is stored at: its type folder and file name.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/>'s name isn't a valid model name.</exception>
    public static string RelativePathFor(GeneratorModelId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Path.Combine(id.Type.ToString(), FileNameFor(id));
    }

    /// <summary>The generator type a file extension belongs to, or null when no kind uses it.</summary>
    public static GeneratorType? DefaultTypeForExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        string ext = extension.Trim().TrimStart('.');
        foreach (var type in Enum.GetValues<GeneratorType>())
        {
            if (string.Equals(ext, GetExtension(type), StringComparison.OrdinalIgnoreCase))
                return type;
        }

        return null;
    }

    /// <summary>A storage name suggested for an import source: the file's stem, sanitized.</summary>
    public static string SuggestName(string sourcePath)
    {
        string stem = (Path.GetFileNameWithoutExtension(sourcePath) ?? "").Trim();
        foreach (var type in Enum.GetValues<GeneratorType>().OrderByDescending(t => t.ToString().Length))
        {
            string prefix = type.ToString();
            if (stem.StartsWith(prefix, StringComparison.Ordinal) && stem.Length > prefix.Length)
            {
                stem = stem[prefix.Length..];
                break;
            }
        }

        return SanitizeName(stem.Trim());
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
    /// Reads a stored model path (with or without its storage root) as a stored model, or false
    /// when it isn't one: the parent folder must be the kind's, the extension must be the kind's,
    /// and the stem must be a valid model name. Flat files with no type folder never parse.
    /// </summary>
    public static bool TryParseStoredPath(string path, out GeneratorModelId? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string file = Path.GetFileName(path);
        string? folder = Path.GetFileName(Path.GetDirectoryName(path));
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(folder))
            return false;

        foreach (var type in Enum.GetValues<GeneratorType>())
        {
            if (!string.Equals(folder, type.ToString(), StringComparison.Ordinal))
                continue;
            if (!string.Equals(Path.GetExtension(file), "." + GetExtension(type), StringComparison.OrdinalIgnoreCase))
                continue;

            string stem = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(stem) && SanitizeName(stem) == stem)
            {
                id = new GeneratorModelId(type, stem);
                return true;
            }
        }

        return false;
    }
}
