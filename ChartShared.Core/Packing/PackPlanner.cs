using System.Globalization;
using ChartShared.Core.Charts;
using ChartShared.Core.Ini;
using ChartShared.Core.Metadata;
using ChartShared.Core.Resources;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Packing;

/// <summary>Choices that change what goes into a <c>.sng</c>.</summary>
/// <param name="IncludeSubfolders">Whether files in subfolders are packed (A13: off by default).</param>
/// <param name="TranscodeToOpus">Whether non-Opus audio stems are transcoded to Opus before packing. Needs an <see cref="IPackTranscoder"/> where it's used.</param>
/// <param name="OpusBitrateKbps">The transcoded Opus bitrate in kbps per stereo stem (mono stems use half), when <paramref name="TranscodeToOpus"/>.</param>
public sealed record PackOptions(bool IncludeSubfolders = false, bool TranscodeToOpus = false, int OpusBitrateKbps = 128)
{
    /// <summary>The defaults from project spec §8.</summary>
    public static PackOptions Default { get; } = new();
}

/// <summary>What packing a folder would do (project spec F1.1).</summary>
/// <param name="SourceFolder">The song folder.</param>
/// <param name="Included">Files to store, with their final names, in order.</param>
/// <param name="Excluded">Relative paths of files left out, including <c>song.ini</c>.</param>
/// <param name="Metadata">Metadata pairs to store, in order, with empty values and the keys of parts the chart has no notes in already dropped.</param>
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

        if (!included.Any(f => AudioStems.StemOf(f.StoredName) is not null))
            findings.Add(new Finding(SngRules.Sng052, Severity.Warning, FindingMessages.NoAudioStem));

        var metadata = await ReadMetadataAsync(folder, songIni, findings, cancellationToken).ConfigureAwait(false);
        metadata = await PruneForChartAsync(included, metadata, findings, cancellationToken).ConfigureAwait(false);
        return new PackPlan(folder, included, excluded, metadata, findings);
    }

    /// <summary><paramref name="metadata"/> without the keys of parts the chart has no notes in (SNG062), as <see cref="ExportCleanup.PruneMetadata"/> leaves it.</summary>
    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> PruneForChartAsync(
        IReadOnlyList<SngSourceFile> included,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        List<Finding> findings,
        CancellationToken cancellationToken)
    {
        if (ChartReader.Pick(included.Select(f => f.StoredName)) is not { } name)
            return metadata;
        var chart = included.First(f => f.StoredName == name);
        if (chart.Length > ChartReader.MaxBytes)
            return metadata;

        byte[] bytes = await File.ReadAllBytesAsync(chart.SourcePath, cancellationToken).ConfigureAwait(false);
        return ExportCleanup.PruneMetadata(metadata, ExportCleanup.PartsOf(bytes, name, metadata), ChartReader.IsMidi(name), findings);
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
