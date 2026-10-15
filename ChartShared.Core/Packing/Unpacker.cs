using System.Globalization;
using ChartShared.Core.Ini;
using ChartShared.Core.Metadata;
using ChartShared.Core.Resources;
using ChartShared.Core.Sng;
using ChartShared.Core.Validation;
using Microsoft.Win32.SafeHandles;

namespace ChartShared.Core.Packing;

/// <summary>The outcome of <see cref="Unpacker.UnpackAsync"/>.</summary>
/// <param name="Extracted">Whether the output folder was written. False when the <c>.sng</c> has structural errors.</param>
/// <param name="Findings">The reader's findings, plus any files or metadata that weren't extracted.</param>
public sealed record UnpackResult(bool Extracted, IReadOnlyList<Finding> Findings)
{
    /// <summary>The folder was written, but something in the <c>.sng</c> was left out ("Completed with errors").</summary>
    public bool CompletedWithErrors => Extracted && Findings.Any(f => f.Severity == Severity.Error);
}

/// <summary>Extracts a <c>.sng</c> into a song folder (project spec F2).</summary>
/// <remarks>
/// <para>
/// The generated <c>song.ini</c> takes the same single-<c>[song]</c>-section form as
/// <c>SngCli decode</c> (sng-spec §9.5, <c>SngCli/SngDecode.cs</c> in
/// <see href="https://github.com/mdsitton/SngFileFormat">mdsitton/SngFileFormat</see> at commit
/// <c>6683799</c>, MIT License, © Matthew Sitton; see <c>THIRD-PARTY-NOTICES.md</c>).
/// </para>
/// <para>
/// Unlike the reference decoder, every stored name is checked before anything is written, and
/// must resolve inside the output folder (A16). Everything is extracted into a temp folder first
/// and moved into place at the end, so a failure or cancel leaves no partial output.
/// </para>
/// </remarks>
public static class Unpacker
{
    /// <summary>Extracts <paramref name="sngPath"/> into <paramref name="targetFolder"/> on the thread pool.</summary>
    /// <param name="sngPath">The <c>.sng</c> to read.</param>
    /// <param name="targetFolder">The song folder to create.</param>
    /// <param name="overwrite">
    /// Whether an existing <paramref name="targetFolder"/> is written into, replacing files with the
    /// same names. Other files in it are left alone; the folder is never deleted.
    /// </param>
    /// <param name="cancellationToken">Cancels the unpack; nothing is left behind.</param>
    /// <exception cref="IOException">A file can't be read or written, or the target exists and <paramref name="overwrite"/> is false.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The unpack was cancelled.</exception>
    public static Task<UnpackResult> UnpackAsync(string sngPath, string targetFolder, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sngPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        return Task.Run(() => Unpack(sngPath, targetFolder, overwrite, cancellationToken), cancellationToken);
    }

    private static UnpackResult Unpack(string sngPath, string targetFolder, bool overwrite, CancellationToken cancellationToken)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFolder));
        if (!overwrite && (Directory.Exists(target) || File.Exists(target)))
            throw new IOException($"'{target}' already exists.");

        using SafeFileHandle handle = File.OpenHandle(sngPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        SngReadResult read = SngReader.Read(sngPath, cancellationToken);
        var findings = new List<Finding>(read.Findings);

        // Every error the reader reports is structural (SNG001–013, SNG040), which stops the unpack (F2.1).
        if (read.HasErrors || read.Header is null)
            return new UnpackResult(false, findings);

        string parent = Path.GetDirectoryName(target) ?? throw new IOException($"'{target}' has no parent folder.");
        Directory.CreateDirectory(parent);
        string temp = StagingPath.For(target);

        try
        {
            Directory.CreateDirectory(temp);
            Extract(handle, read, temp, findings, cancellationToken);
            WriteSongIni(read.Metadata, Path.Combine(temp, SongIni.FileName), findings);

            cancellationToken.ThrowIfCancellationRequested();
            Commit(temp, target);
        }
        finally
        {
            StagingPath.TryDeleteDirectory(temp);
        }

        return new UnpackResult(true, findings);
    }

    private static void Extract(SafeFileHandle handle, SngReadResult read, string temp, List<Finding> findings, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadOnlySpan<byte> mask = read.Header!.Mask.Span;

        foreach (SngFileEntry entry in read.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FilenameRules.FindProblem(entry.Name) is { } problem)
            {
                findings.Add(Error(SngRules.Sng033, FindingMessages.UnsafeExtractNameFormat, entry.Name, problem.Reason));
                continue;
            }

            string path = Path.GetFullPath(Path.Combine([temp, .. entry.Name.Split('/')]));
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                findings.Add(Error(SngRules.Sng033, FindingMessages.ExtractOutsideFolderFormat, entry.Name));
                continue;
            }

            if (entry.Name.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase))
            {
                // A17: the song.ini generated from the metadata wins.
                findings.Add(new Finding(SngRules.Sng053, Severity.Warning, FindingMessages.StoredSongIni));
                continue;
            }

            if (!extracted.Add(entry.Name))
            {
                findings.Add(Error(SngRules.Sng032, FindingMessages.DuplicateExtractNameFormat, entry.Name));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1);
            SngFileData.CopyUnmasked(handle, entry, mask, output, cancellationToken);
        }
    }

    private static void WriteSongIni(IReadOnlyList<SngMetadataPair> metadata, string path, List<Finding> findings)
    {
        var pairs = new List<KeyValuePair<string, string>>(metadata.Count);
        foreach (SngMetadataPair pair in metadata)
        {
            if (MetadataRules.CanRoundTrip(pair.Key, pair.Value))
                pairs.Add(KeyValuePair.Create(pair.Key, pair.Value));
            else
                findings.Add(Error(
                    pair.Key.Length == 0 ? SngRules.Sng023 : MetadataRules.KeyHasForbiddenCharacter(pair.Key) ? SngRules.Sng020 : SngRules.Sng021,
                    FindingMessages.MetadataNotWrittenFormat,
                    pair.Key));
        }

        using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        SongIni.Write(writer, pairs);
    }

    /// <summary>Moves the extracted files from <paramref name="temp"/> to <paramref name="target"/>.</summary>
    private static void Commit(string temp, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(temp, target);
            return;
        }

        foreach (string file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(temp, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file, destination, overwrite: true);
        }
    }

    private static Finding Error(string ruleId, string format, params object[] args) =>
        new(ruleId, Severity.Error, string.Format(CultureInfo.CurrentCulture, format, args));
}
