using System.Globalization;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Resources;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Packing;

/// <summary>Choices that change what goes into a <c>.sng</c>.</summary>
/// <param name="IncludeSubfolders">Whether files in subfolders are packed (A13: off by default).</param>
public sealed record PackOptions(bool IncludeSubfolders = false)
{
    /// <summary>The defaults from project spec §8.</summary>
    public static PackOptions Default { get; } = new();
}

/// <summary>What packing a folder would do (project spec F1.1).</summary>
/// <param name="SourceFolder">The song folder.</param>
/// <param name="Included">Files to store, with their final names, in order.</param>
/// <param name="Excluded">Relative paths of files left out, including <c>song.ini</c>.</param>
/// <param name="Metadata">Metadata pairs to store, in order, with empty values already dropped.</param>
/// <param name="Findings">Problems found. Any error blocks packing.</param>
public sealed record PackPlan(
    string SourceFolder,
    IReadOnlyList<SngSourceFile> Included,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<KeyValuePair<string, string>> Metadata,
    IReadOnlyList<Finding> Findings)
{
    public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);
}

/// <summary>Turns a song folder into a <see cref="PackPlan"/>.</summary>
/// <remarks>
/// Registered names are lowercased and unknown ones kept as they are, like <c>SngCli encode</c>
/// (sng-spec §9.4, <c>SngCli/SngEncode.cs</c> in
/// <see href="https://github.com/mdsitton/SngFileFormat">mdsitton/SngFileFormat</see> at commit
/// <c>6683799</c>, MIT License, © Matthew Sitton; see <c>THIRD-PARTY-NOTICES.md</c>). Unlike the
/// reference encoder, the junk list really is applied (A14), subfolders are opt-in (A13), and the
/// filename and metadata rules are enforced as errors (A15).
/// </remarks>
public static class PackPlanner
{
    private const string SngExtension = ".sng";

    /// <summary>Plans packing <paramref name="songFolder"/>. Nothing is written.</summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="songFolder"/> doesn't exist.</exception>
    /// <exception cref="IOException">The folder or its <c>song.ini</c> can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Planning was cancelled.</exception>
    public static async Task<PackPlan> PlanAsync(string songFolder, PackOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songFolder);
        options ??= PackOptions.Default;

        string folder = Path.GetFullPath(songFolder);
        var listing = await SongFolderListing.ListAsync(folder, cancellationToken).ConfigureAwait(false);
        return await PlanAsync(folder, listing, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Plans packing <paramref name="songFolder"/> from a listing of it already taken with
    /// <see cref="SongFolderListing"/>, so a caller that needs the listing too reads the folder once.
    /// </summary>
    /// <param name="songFolder">The song folder.</param>
    /// <param name="listing">Its files, as <see cref="SongFolderListing.List"/> returns them.</param>
    /// <param name="options">What to include.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <exception cref="IOException">The folder's <c>song.ini</c> can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Planning was cancelled.</exception>
    public static async Task<PackPlan> PlanAsync(
        string songFolder,
        IReadOnlyList<FolderFileEntry> listing,
        PackOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songFolder);
        ArgumentNullException.ThrowIfNull(listing);
        options ??= PackOptions.Default;

        string folder = Path.GetFullPath(songFolder);
        var findings = new List<Finding>();
        var included = new List<SngSourceFile>();
        var excluded = new List<string>();
        var reportedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FolderFileEntry? songIni = null;

        foreach (FolderFileEntry file in listing)
        {
            string relative = file.RelativePath;
            string[] segments = relative.Split('/');

            int junk = Array.FindIndex(segments, FilenameRules.IsJunk);
            if (junk >= 0)
            {
                excluded.Add(relative);
                if (junk == segments.Length - 1)
                    findings.Add(Info(SngRules.Sng072, FindingMessages.ExcludedJunkFormat, relative));
                else if (reportedFolders.Add(JoinPrefix(segments, junk + 1)))
                    findings.Add(Info(SngRules.Sng072, FindingMessages.ExcludedJunkFormat, JoinPrefix(segments, junk + 1) + "/"));
                continue;
            }

            if (segments.Length > 1 && !options.IncludeSubfolders)
            {
                excluded.Add(relative);
                if (reportedFolders.Add(segments[0]))
                    findings.Add(Info(SngRules.Sng073, FindingMessages.ExcludedSubfolderFormat, segments[0]));
                continue;
            }

            if (relative.EndsWith(SngExtension, StringComparison.OrdinalIgnoreCase))
            {
                excluded.Add(relative);
                findings.Add(Info(SngRules.Sng072, FindingMessages.ExcludedNestedSngFormat, relative));
                continue;
            }

            if (relative.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase))
            {
                // Its contents become the metadata instead.
                excluded.Add(relative);
                songIni = file;
                continue;
            }

            string stored = FilenameRules.NormaliseStoredName(relative);
            if (FilenameRules.FindProblem(stored) is { } problem)
            {
                findings.Add(Error(problem.RuleId, FindingMessages.FilenameProblemFormat, relative, problem.Reason));
                continue;
            }

            if (storedNames.TryGetValue(stored, out string? first))
            {
                findings.Add(Error(SngRules.Sng032, FindingMessages.DuplicateFilenameFormat, first, relative));
                continue;
            }

            storedNames.Add(stored, relative);
            included.Add(new SngSourceFile(stored, Path.Combine(folder, relative), file.Length));
        }

        var metadata = await ReadMetadataAsync(folder, songIni, findings, cancellationToken).ConfigureAwait(false);
        return new PackPlan(folder, included, excluded, metadata, findings);
    }

    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadMetadataAsync(
        string folder,
        FolderFileEntry? songIni,
        List<Finding> findings,
        CancellationToken cancellationToken)
    {
        if (songIni is null)
        {
            findings.Add(new Finding(SngRules.Sng070, Severity.Warning, FindingMessages.NoSongIni));
            return [];
        }

        var result = await SongIni.ReadAsync(Path.Combine(folder, songIni.RelativePath), cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case SongIniStatus.NoSongSection:
                findings.Add(new Finding(SngRules.Sng071, Severity.Warning, FindingMessages.SongIniNoSongSection));
                return [];
            case SongIniStatus.TooLarge:
                // Packing would silently lose all the metadata, so this blocks.
                findings.Add(new Finding(SngRules.Sng071, Severity.Error, FindingMessages.SongIniTooLarge));
                return [];
        }

        var pairs = new List<KeyValuePair<string, string>>(result.Pairs.Count);
        foreach (var (key, value) in result.Pairs)
        {
            if (key.Length == 0)
                findings.Add(new Finding(SngRules.Sng023, Severity.Error, FindingMessages.EmptyKey));
            else if (MetadataRules.KeyHasForbiddenCharacter(key))
                findings.Add(Error(SngRules.Sng020, FindingMessages.KeyForbiddenCharacterFormat, key));
            else if (MetadataRules.ValueHasForbiddenCharacter(value))
                findings.Add(Error(SngRules.Sng021, FindingMessages.ValueForbiddenCharacterFormat, key));
            else if (value.Length == 0)
                findings.Add(Info(SngRules.Sng061, FindingMessages.EmptyValueDroppedFormat, key));
            else
                pairs.Add(KeyValuePair.Create(key, value));
        }

        return pairs;
    }

    private static string JoinPrefix(string[] segments, int count) => string.Join('/', segments, 0, count);

    private static Finding Error(string ruleId, string format, params object[] args) =>
        new(ruleId, Severity.Error, string.Format(CultureInfo.CurrentCulture, format, args));

    private static Finding Info(string ruleId, string format, params object[] args) =>
        new(ruleId, Severity.Info, string.Format(CultureInfo.CurrentCulture, format, args));
}
