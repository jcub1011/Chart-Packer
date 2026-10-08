using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Settings;

namespace ChartPacker.Core.Charting.Recovery;

/// <summary>What a recovery file holds beside the chart (charter-spec C15 item 6).</summary>
/// <param name="SongPath">The song's full path, folder or <c>.sng</c>; null for a new chart that hasn't been saved.</param>
/// <param name="Kind">Whether the song is a folder or a <c>.sng</c>; null for a new chart.</param>
/// <param name="ChartFile">The song's chart file as it was opened or last saved; null when it had none.</param>
/// <param name="SavedAt">When it was autosaved.</param>
/// <param name="MidiLength">The chart's length in bytes, which, with its hash, says the chart is the one written with it.</param>
/// <param name="MidiSha256">The chart's SHA-256, in hex.</param>
/// <param name="SongIni">The song's <c>song.ini</c> as the next save would have left it, which the chart is read with.</param>
/// <param name="Pending">The pending <c>song.ini</c> changes, for the record.</param>
/// <param name="Reverted">The keys of the pending changes the user had reverted.</param>
/// <param name="Values">The values the song had nowhere else: a new chart's name and artist.</param>
/// <param name="Stems">A new chart's audio, where it is.</param>
/// <param name="Title">What the Editor's title said, to list it by.</param>
public sealed record RecoveryManifest(
    string? SongPath,
    SongEntryKind? Kind,
    ChartFileStamp? ChartFile,
    DateTimeOffset SavedAt,
    long MidiLength,
    string MidiSha256,
    IReadOnlyList<KeyValuePair<string, string>> SongIni,
    IReadOnlyList<KeyValuePair<string, string?>> Pending,
    IReadOnlyList<string> Reverted,
    IReadOnlyList<KeyValuePair<string, string>> Values,
    IReadOnlyList<NewChartStem> Stems,
    string Title)
{
    /// <summary>The manifest's format, written into it. A file of another version is ignored.</summary>
    public const int Version = 1;

    /// <summary>Whether it's a new chart that was never saved, which has no song to be opened from.</summary>
    public bool IsUntitled => SongPath is null;

    /// <summary>The delay Find first downbeat set and the song hadn't saved yet, in milliseconds; null for none.</summary>
    public long? DelayMs { get; init; }
}

/// <summary>A recovery file found in the store.</summary>
/// <param name="Key">Its name in the store.</param>
/// <param name="ChartPath">Where its chart is.</param>
public sealed record RecoveryFile(string Key, string ChartPath, RecoveryManifest Manifest);

/// <summary>What to do with a recovery file found for a song being opened.</summary>
public enum RecoveryOffer
{
    /// <summary>Offer it: the song's chart is as it was when the autosave was made.</summary>
    Offer,

    /// <summary>Offer it, but warn that the song's chart has changed on disk since.</summary>
    OfferChanged,

    /// <summary>Nothing to recover: the song's chart is the recovery's, byte for byte. It can be deleted.</summary>
    Stale,
}

/// <summary>
/// Chart Builder's autosaves (charter-spec C15 item 6): for each song open, its chart as a <c>notes.mid</c> and a
/// manifest, in the per-user recovery folder, never in the song.
/// </summary>
/// <remarks>
/// <para>A song's files are named by a hash of its full path, as its lock is (<see cref="SongLock.FileNameOf"/>), and a
/// new chart's by an ID of its own until it's first saved. Each file is written to a temp file and renamed into place,
/// the chart first and the manifest last. The chart's file is named by the start of its hash too, so writing a new one
/// leaves the last autosave whole until the new manifest replaces it, and the manifest holds the whole hash, so a chart
/// that doesn't match it is ignored.</para>
/// <para>The folder is always passed in, so tests never touch the real one.</para>
/// </remarks>
public sealed class RecoveryStore(string folder)
{
    private const string ChartExtension = ".mid";
    private const string ManifestExtension = ".json";
    private const string NewChartPrefix = "new-";

    /// <summary>The folder the recovery files are in.</summary>
    public string Folder { get; } = folder ?? throw new ArgumentNullException(nameof(folder));

    /// <summary>The key of the song at <paramref name="songPath"/>'s recovery file.</summary>
    public static string KeyOf(string songPath) => SongLock.FileNameOf(songPath);

    /// <summary>The key of a new chart's recovery file, before it's saved.</summary>
    public static string KeyOfNew(Guid id) => NewChartPrefix + id.ToString("N");

    /// <summary>
    /// Writes <paramref name="midi"/> and its manifest under <paramref name="key"/>, replacing what was there, the
    /// chart first and the manifest last.
    /// </summary>
    /// <exception cref="IOException">A file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public async Task WriteAsync(string key, byte[] midi, RecoveryManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(midi);
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(Folder);

        // The chart goes under a name of its own, so the last autosave stays whole until the new manifest replaces it.
        var stamped = manifest with { MidiLength = midi.Length, MidiSha256 = Convert.ToHexString(SHA256.HashData(midi)) };
        string chart = ChartPathOf(key, stamped);
        string temp = $"{chart}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, midi, cancellationToken).ConfigureAwait(false);
            File.Move(temp, chart, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }

        if (!JsonFiles.WriteAtomically(Path.Combine(Folder, key + ManifestExtension), ToJson(stamped)))
            throw new IOException($"The recovery manifest '{key}' couldn't be written.");
        DeleteCharts(key, except: chart);
    }

    // A chart's file: the key and the start of its hash.
    private string ChartPathOf(string key, RecoveryManifest manifest) =>
        Path.Combine(Folder, $"{key}-{manifest.MidiSha256[..16].ToLowerInvariant()}{ChartExtension}");

    // Deletes the key's charts but one, those earlier autosaves left.
    private void DeleteCharts(string key, string? except)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(Folder, key + "-*" + ChartExtension))
            {
                if (!string.Equals(path, except, StringComparison.OrdinalIgnoreCase))
                    TryDelete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The recovery file for the song at <paramref name="songPath"/>, or null when there's none that's whole.</summary>
    public RecoveryFile? Find(string songPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
        return Read(KeyOf(songPath));
    }

    /// <summary>The recovery files of new charts that were never saved, the latest first.</summary>
    public IReadOnlyList<RecoveryFile> NewCharts()
    {
        if (!Directory.Exists(Folder))
            return [];
        try
        {
            return [.. Directory.EnumerateFiles(Folder, NewChartPrefix + "*" + ManifestExtension)
                .Select(path => Read(Path.GetFileNameWithoutExtension(path)))
                .OfType<RecoveryFile>()
                .Where(f => f.Manifest.IsUntitled)
                .OrderByDescending(f => f.Manifest.SavedAt)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The recovery file stored under <paramref name="key"/>, or null when there's none, or it isn't whole.</summary>
    public RecoveryFile? Read(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (JsonFiles.ReadObject(Path.Combine(Folder, key + ManifestExtension), warn: true) is not { } json
            || FromJson(json) is not { } manifest
            || manifest.MidiSha256.Length < 16
            || ChartPathOf(key, manifest) is not { } chart
            || !File.Exists(chart))
            return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(chart);
            return bytes.Length == manifest.MidiLength && Convert.ToHexString(SHA256.HashData(bytes)) == manifest.MidiSha256
                ? new RecoveryFile(key, chart, manifest)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <paramref name="file"/>'s chart into the editable model, with the settings it was saved for.
    /// </summary>
    /// <exception cref="IOException">The chart can't be read, or isn't a MIDI file the loader can read.</exception>
    public static ChartLoadResult ReadChart(RecoveryFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return MidiChartLoader.Load(File.ReadAllBytes(file.ChartPath), ChartReadOptions.From(file.Manifest.SongIni));
    }

    /// <summary>
    /// Whether <paramref name="file"/> is worth offering for <paramref name="entry"/>: stale when the song's chart is the
    /// recovery's byte for byte, otherwise offered, with a warning when the chart on disk has changed since the
    /// recovery's manifest recorded it.
    /// </summary>
    public static async Task<RecoveryOffer> AssessAsync(RecoveryFile file, SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(entry);
        var recorded = file.Manifest.ChartFile;
        try
        {
            if (ChartFileStamp.Of(entry, ChartFiles.MidiName) is not null
                && await SongFile.ReadAsync(entry, ChartFiles.MidiName, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false) is { } onDisk)
            {
                byte[] recovered = await File.ReadAllBytesAsync(file.ChartPath, cancellationToken).ConfigureAwait(false);
                if (onDisk.AsSpan().SequenceEqual(recovered))
                    return RecoveryOffer.Stale;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var now = recorded is null ? ChartFileStamp.Of(entry, ChartFiles.MidiName) : ChartFileStamp.Of(entry, recorded.Name);
        return now == recorded ? RecoveryOffer.Offer : RecoveryOffer.OfferChanged;
    }

    /// <summary>Deletes the recovery file under <paramref name="key"/>, if there is one.</summary>
    public void Delete(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        TryDelete(Path.Combine(Folder, key + ManifestExtension));
        if (Directory.Exists(Folder))
            DeleteCharts(key, except: null);
    }

    private static JsonObject ToJson(RecoveryManifest m)
    {
        static JsonArray Pairs(IEnumerable<KeyValuePair<string, string?>> pairs) =>
            [.. pairs.Select(p => (JsonNode)new JsonArray(JsonValue.Create(p.Key), p.Value is null ? null : JsonValue.Create(p.Value)))];

        var json = new JsonObject
        {
            ["version"] = RecoveryManifest.Version,
            ["songPath"] = m.SongPath,
            ["kind"] = m.Kind?.ToString(),
            ["savedAt"] = m.SavedAt.ToString("O", CultureInfo.InvariantCulture),
            ["midiLength"] = m.MidiLength,
            ["midiSha256"] = m.MidiSha256,
            ["songIni"] = Pairs(m.SongIni.Select(p => KeyValuePair.Create(p.Key, (string?)p.Value))),
            ["pending"] = Pairs(m.Pending),
            ["reverted"] = new JsonArray([.. m.Reverted.Select(k => (JsonNode)JsonValue.Create(k))]),
            ["values"] = Pairs(m.Values.Select(p => KeyValuePair.Create(p.Key, (string?)p.Value))),
            ["stems"] = new JsonArray([.. m.Stems.Select(s => (JsonNode)new JsonArray(JsonValue.Create(s.SourcePath), JsonValue.Create(s.Stem)))]),
            ["title"] = m.Title,
        };
        if (m.DelayMs is { } delay)
            json["delayMs"] = delay;
        if (m.ChartFile is { } chart)
        {
            json["chartFile"] = new JsonObject
            {
                ["name"] = chart.Name,
                ["length"] = chart.Length,
                ["lastWriteTimeUtc"] = chart.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            };
        }

        return json;
    }

    private static RecoveryManifest? FromJson(JsonObject json)
    {
        try
        {
            if (json["version"]?.GetValue<int>() != RecoveryManifest.Version)
                return null;

            static List<KeyValuePair<string, string?>> Pairs(JsonNode? node) =>
                [.. (node as JsonArray ?? []).OfType<JsonArray>().Select(p => KeyValuePair.Create(p[0]!.GetValue<string>(), p[1]?.GetValue<string>()))];

            ChartFileStamp? chart = json["chartFile"] is JsonObject c
                ? new ChartFileStamp(
                    c["name"]!.GetValue<string>(),
                    c["length"]!.GetValue<long>(),
                    DateTime.Parse(c["lastWriteTimeUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
                : null;
            return new RecoveryManifest(
                json["songPath"]?.GetValue<string>(),
                json["kind"]?.GetValue<string>() is { } kind ? Enum.Parse<SongEntryKind>(kind) : null,
                chart,
                DateTimeOffset.Parse(json["savedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                json["midiLength"]!.GetValue<long>(),
                json["midiSha256"]!.GetValue<string>(),
                [.. Pairs(json["songIni"]).Select(p => KeyValuePair.Create(p.Key, p.Value ?? ""))],
                Pairs(json["pending"]),
                [.. (json["reverted"] as JsonArray ?? []).Select(k => k!.GetValue<string>())],
                [.. Pairs(json["values"]).Select(p => KeyValuePair.Create(p.Key, p.Value ?? ""))],
                [.. (json["stems"] as JsonArray ?? []).OfType<JsonArray>().Select(s => new NewChartStem(s[0]!.GetValue<string>(), s[1]!.GetValue<string>()))],
                json["title"]?.GetValue<string>() ?? "")
            {
                DelayMs = json["delayMs"]?.GetValue<long>(),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or NullReferenceException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
