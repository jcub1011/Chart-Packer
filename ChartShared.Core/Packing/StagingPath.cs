namespace ChartShared.Core.Packing;

/// <summary>
/// A temp file or folder beside a target, to build it under before moving it into place, so a
/// failure or cancel leaves nothing half-written at the target.
/// </summary>
/// <remarks>
/// The name starts with <c>.</c>, which hides it on Linux and macOS but not on Windows; it isn't
/// marked hidden there, as a folder would keep the attribute once moved into place.
/// </remarks>
public static class StagingPath
{
    /// <summary>What every staging name ends with.</summary>
    public const string Suffix = ".chartpacker-tmp";

    /// <summary>A new staging name for <paramref name="target"/>, in the same folder: <c>.name.1a2b3c4d.chartpacker-tmp</c>.</summary>
    /// <param name="target">The full path of the file or folder to build.</param>
    public static string For(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        string path = Path.TrimEndingDirectorySeparator(target);
        string folder = Path.GetDirectoryName(path) ?? throw new ArgumentException($"'{target}' has no parent folder.", nameof(target));
        return Path.Combine(folder, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + Suffix);
    }

    /// <summary>Deletes the file at <paramref name="path"/>, if it's there and can be.</summary>
    public static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes the folder at <paramref name="path"/> and everything in it, if it's there and can be.</summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
