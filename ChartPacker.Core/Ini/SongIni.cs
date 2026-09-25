using System.Text;

namespace ChartPacker.Core.Ini;

/// <summary>What <see cref="SongIni.ReadAsync"/> found.</summary>
public enum SongIniStatus
{
    /// <summary>The file has a <c>[song]</c> section; its pairs are in <see cref="SongIniReadResult.Pairs"/>.</summary>
    Ok,

    /// <summary>The file was read but has no <c>[song]</c> section.</summary>
    NoSongSection,

    /// <summary>The file is larger than <see cref="SongIni.MaxFileSize"/> and was not read.</summary>
    TooLarge,
}

/// <summary>The result of reading a <c>song.ini</c> file.</summary>
/// <param name="Status">Whether a <c>[song]</c> section was found.</param>
/// <param name="Pairs">The <c>[song]</c> pairs in file order; empty unless <paramref name="Status"/> is <see cref="SongIniStatus.Ok"/>.</param>
public sealed record SongIniReadResult(SongIniStatus Status, IReadOnlyList<KeyValuePair<string, string>> Pairs);

/// <summary>Reads the <c>[song]</c> section of a <c>song.ini</c>.</summary>
/// <remarks>
/// Written independently, to match the parsing rules of <c>SongLib/IniParser.cs</c> in
/// <see href="https://github.com/mdsitton/SngFileFormat">mdsitton/SngFileFormat</see> at commit
/// <c>6683799</c> (MIT License, © Matthew Sitton; see <c>THIRD-PARTY-NOTICES.md</c>), so a folder's
/// metadata reads the same way the reference encoder would see it (sng-spec §9.4):
/// <list type="bullet">
/// <item>Lines are trimmed. Blank lines and lines starting with <c>#</c> or <c>;</c> are skipped.</item>
/// <item>A <c>[name]</c> line starts a section. The name is matched ignoring case but isn't trimmed,
/// so <c>[ song ]</c> is not the song section. A repeated section continues the earlier one.</item>
/// <item>Other lines split on the first <c>=</c>, and both sides are trimmed. Lines without <c>=</c> are skipped.</item>
/// <item>Keys are matched ignoring case, and a repeated key's last value wins.</item>
/// </list>
/// Unlike the reference parser, pairs are returned in the order their keys first appear, rather
/// than in dictionary order.
/// </remarks>
public static class SongIni
{
    /// <summary>The standard filename, matched ignoring case.</summary>
    public const string FileName = "song.ini";

    /// <summary>Files larger than this aren't read. Real <c>song.ini</c> files are a few KB.</summary>
    public const long MaxFileSize = 1024 * 1024;

    private const string SongSection = "song";

    /// <summary>
    /// Parses INI text and returns the <c>[song]</c> section's pairs, or <see langword="null"/>
    /// when there is no <c>[song]</c> section.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var pairs = new List<KeyValuePair<string, string>>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool foundSection = false;
        bool inSection = false;

        while (reader.ReadLine() is { } rawLine)
        {
            ReadOnlySpan<char> line = rawLine.AsSpan().Trim();
            if (line.IsEmpty || line[0] is '#' or ';')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                inSection = line[1..^1].Equals(SongSection, StringComparison.OrdinalIgnoreCase);
                foundSection |= inSection;
                continue;
            }

            if (!inSection)
                continue;

            int separator = line.IndexOf('=');
            if (separator < 0)
                continue;

            string key = line[..separator].Trim().ToString();
            string value = line[(separator + 1)..].Trim().ToString();

            if (indexByKey.TryGetValue(key, out int existing))
            {
                pairs[existing] = new KeyValuePair<string, string>(pairs[existing].Key, value);
            }
            else
            {
                indexByKey.Add(key, pairs.Count);
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return foundSection ? pairs : null;
    }

    /// <summary>
    /// Reads <paramref name="path"/> as UTF-8, honouring a byte order mark, and parses its
    /// <c>[song]</c> section.
    /// </summary>
    /// <exception cref="IOException">The file can't be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<SongIniReadResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        if (stream.Length > MaxFileSize)
            return new SongIniReadResult(SongIniStatus.TooLarge, []);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var pairs = Parse(new StringReader(text));
        return pairs is null
            ? new SongIniReadResult(SongIniStatus.NoSongSection, [])
            : new SongIniReadResult(SongIniStatus.Ok, pairs);
    }
}
