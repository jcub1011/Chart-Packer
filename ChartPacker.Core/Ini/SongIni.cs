using System.Text;
using ChartPacker.Core.Metadata;

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
public sealed record SongIniReadResult(SongIniStatus Status, IReadOnlyList<KeyValuePair<string, string>> Pairs)
{
    /// <summary>The <c>[song]</c> keys that appear more than once (SNG022), as first spelled; the last value of each is in <see cref="Pairs"/>.</summary>
    public IReadOnlyList<string> DuplicateKeys { get; init; } = [];
}

/// <summary>Reads and writes the <c>[song]</c> section of a <c>song.ini</c>.</summary>
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

    /// <summary>Suffix of the temp file <see cref="ApplyEditsAsync"/> writes next to the target.</summary>
    public const string TempSuffix = ".tmp";

    private const string SongSection = "song";

    /// <summary>
    /// Parses INI text and returns the <c>[song]</c> section's pairs, or <see langword="null"/>
    /// when there is no <c>[song]</c> section.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? Parse(TextReader reader) => Parse(reader, duplicateKeys: null);

    /// <summary>
    /// Like <see cref="Parse(TextReader)"/>, and adds each <c>[song]</c> key that appears more than
    /// once to <paramref name="duplicateKeys"/>, once, as first spelled.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? Parse(TextReader reader, ICollection<string>? duplicateKeys)
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
                string firstKey = pairs[existing].Key;
                if (duplicateKeys is not null && !duplicateKeys.Contains(firstKey))
                    duplicateKeys.Add(firstKey);
                pairs[existing] = new KeyValuePair<string, string>(firstKey, value);
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

        var duplicateKeys = new List<string>();
        var pairs = Parse(new StringReader(text), duplicateKeys);
        return pairs is null
            ? new SongIniReadResult(SongIniStatus.NoSongSection, [])
            : new SongIniReadResult(SongIniStatus.Ok, pairs) { DuplicateKeys = duplicateKeys };
    }

    /// <summary>
    /// Writes <paramref name="pairs"/> as a single <c>[song]</c> section of <c>key = value</c>
    /// lines, the form the reference decoder produces (sng-spec §9.5).
    /// </summary>
    /// <remarks>Callers leave out pairs that can't round-trip through INI (sng-spec §6.1).</remarks>
    public static void Write(TextWriter writer, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(pairs);

        writer.WriteLine("[" + SongSection + "]");
        foreach (var (key, value) in pairs)
            writer.WriteLine(key + " = " + value);
    }

    /// <summary>Writes <paramref name="pairs"/> to <paramref name="path"/> as UTF-8 without a byte order mark.</summary>
    /// <exception cref="IOException">The file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public static async Task WriteAsync(string path, IEnumerable<KeyValuePair<string, string>> pairs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var text = new StringWriter();
        Write(text, pairs);
        await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies <paramref name="draft"/> to INI text, changing only the lines of the keys it edits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sections, keys and comments are recognised exactly as <see cref="Parse"/> does. Everything
    /// else (comments, blank lines, other sections, spacing, line endings, and the lines of keys the
    /// draft doesn't touch) is kept as it is.
    /// </para>
    /// <list type="bullet">
    /// <item>A set key's first line in <c>[song]</c> is rewritten as <c>key = value</c>, keeping the
    /// line's indentation and the key as written. Its later duplicates are removed, since
    /// <see cref="Parse"/> would let them win.</item>
    /// <item>A removed key's lines in <c>[song]</c> are all removed.</item>
    /// <item>A set key that isn't present goes after the last pair of the last <c>[song]</c>
    /// section. If there's no <c>[song]</c> section, one is appended.</item>
    /// </list>
    /// Afterwards, <see cref="Parse"/> of the result gives
    /// <see cref="MetadataDraft.ApplyTo"/> of what <see cref="Parse"/> gave before.
    /// </remarks>
    /// <param name="text">The file's text. Empty for a file that doesn't exist yet.</param>
    /// <param name="draft">The changes to apply. Callers leave out values that can't round-trip (sng-spec §6.1).</param>
    public static string ApplyEdits(string text, MetadataDraft draft)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(draft);

        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\n') ? "\n"
            : Environment.NewLine;

        var lines = SplitLines(text);
        var output = new List<IniLine>(lines.Count + draft.Keys.Count);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int insertAfter = -1;
        bool inSection = false;

        foreach (var line in lines)
        {
            ReadOnlySpan<char> trimmed = line.Content.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] is '#' or ';')
            {
                output.Add(line);
                continue;
            }

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                inSection = trimmed[1..^1].Equals(SongSection, StringComparison.OrdinalIgnoreCase);
                output.Add(line);
                if (inSection)
                    insertAfter = output.Count - 1;
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (!inSection || separator < 0)
            {
                output.Add(line);
                continue;
            }

            string key = trimmed[..separator].Trim().ToString();
            if (!draft.TryGetChange(key, out var change))
            {
                output.Add(line);
                insertAfter = output.Count - 1;
                continue;
            }

            if (change.Value is { } value && written.Add(key))
            {
                string indent = line.Content[..(line.Content.Length - line.Content.AsSpan().TrimStart().Length)];
                output.Add(line with { Content = indent + key + " = " + value });
                insertAfter = output.Count - 1;
            }
        }

        var added = draft.Changes
            .Where(c => c.Value is not null && !written.Contains(c.Key))
            .Select(c => new IniLine(c.Key + " = " + c.Value, newLine))
            .ToList();

        if (added.Count > 0)
        {
            if (insertAfter < 0)
            {
                // No [song] section: start one at the end, after a blank line if the file has content.
                if (output.Count > 0)
                {
                    if (output[^1].Ending.Length == 0)
                        output[^1] = output[^1] with { Ending = newLine };
                    if (!string.IsNullOrWhiteSpace(output[^1].Content))
                        output.Add(new IniLine("", newLine));
                }

                output.Add(new IniLine("[" + SongSection + "]", newLine));
                output.AddRange(added);
            }
            else
            {
                // Keep a missing final newline missing: it moves to the last inserted line.
                if (output[insertAfter].Ending.Length == 0)
                {
                    output[insertAfter] = output[insertAfter] with { Ending = newLine };
                    added[^1] = added[^1] with { Ending = "" };
                }

                output.InsertRange(insertAfter + 1, added);
            }
        }

        var result = new StringBuilder(text.Length + added.Sum(l => l.Content.Length + l.Ending.Length));
        foreach (var line in output)
            result.Append(line.Content).Append(line.Ending);
        return result.ToString();
    }

    /// <summary>
    /// Applies <paramref name="draft"/> to the <c>song.ini</c> at <paramref name="path"/> with
    /// <see cref="ApplyEdits"/>, creating the file if it doesn't exist.
    /// </summary>
    /// <remarks>
    /// The file's encoding and byte order mark are kept; a new file is UTF-8 without one. The new
    /// text goes to a temp file that is then renamed over the original.
    /// </remarks>
    /// <exception cref="IOException">The file can't be read or written, or is larger than <see cref="MaxFileSize"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public static async Task ApplyEditsAsync(string path, MetadataDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(draft);

        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string text = "";
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > MaxFileSize)
                throw new IOException($"'{path}' is larger than {MaxFileSize} bytes.");

            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            encoding = DetectEncoding(bytes);
            int preamble = encoding.Preamble.Length;
            text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        }

        string edited = ApplyEdits(text, draft);
        byte[] output = [.. encoding.Preamble, .. encoding.GetBytes(edited)];

        string tempPath = path + TempSuffix;
        try
        {
            await File.WriteAllBytesAsync(tempPath, output, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    /// <summary>The encoding its byte order mark names, as <see cref="ReadAsync"/> honours it; otherwise UTF-8 without one.</summary>
    private static Encoding DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    /// <summary>Splits text into lines, each keeping its own line ending ("" for a last line without one).</summary>
    private static List<IniLine> SplitLines(string text)
    {
        var lines = new List<IniLine>();
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(new IniLine(text[start..], ""));
                break;
            }

            int end = newline > start && text[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add(new IniLine(text[start..end], text[end..(newline + 1)]));
            start = newline + 1;
        }

        return lines;
    }

    /// <summary>A line of an INI file and the line ending that follows it.</summary>
    private readonly record struct IniLine(string Content, string Ending);
}
