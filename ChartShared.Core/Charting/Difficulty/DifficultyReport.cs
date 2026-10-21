using System.Globalization;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>One part of a song in the difficulty report: one of its <c>song.ini</c> ratings, and what the model made of it.</summary>
/// <param name="Instrument">The part.</param>
/// <param name="Key">The <c>diff_*</c> key: Pro drums have <c>diff_drums_real</c> beside <c>diff_drums</c>.</param>
/// <param name="Reported">Its <c>diff_*</c> value as <c>song.ini</c> has it (-1 for unrated); null with no value.</param>
/// <param name="Analyzed">
/// What the model that rates the key made of the part's highest charted difficulty; null when the song hasn't the part,
/// or for <c>diff_drums_real</c>, drums that aren't Pro.
/// </param>
public sealed record DifficultyReportPart(ChartInstrument Instrument, string Key, int? Reported, DifficultyRating? Analyzed);

/// <summary>A part of the difficulty report: its <c>diff_*</c> key, the model that rates it, and its name in the heading.</summary>
public sealed record DifficultyReportColumn(ChartInstrument Instrument, string Key, DifficultyInstrument Model, string Name);

/// <summary>A song in the difficulty report.</summary>
/// <param name="SongPath">The song folder or <c>.sng</c>, as a full path.</param>
/// <param name="Parts">Each of <see cref="DifficultyReport.Columns"/>, in that order.</param>
/// <param name="Problem">Why the song couldn't be read, or null when it was.</param>
public sealed record DifficultyReportRow(string SongPath, IReadOnlyList<DifficultyReportPart> Parts, string? Problem);

/// <summary>
/// Rates every song under a folder with the difficulty model, beside the ratings their <c>song.ini</c> gives, and writes
/// the result as CSV: for each part and <c>diff_*</c> key, the reported rating, the model's rank and each attribute's
/// score, to a tenth. For checking and tuning the model against charts already rated.
/// </summary>
/// <remarks>Songs are only read, never written.</remarks>
public static class DifficultyReport
{
    /// <summary>The ratings reported: every part's, and Pro drums' own beside the drums'.</summary>
    public static IReadOnlyList<DifficultyReportColumn> Columns { get; } =
    [
        Column(ChartInstrument.Guitar, "Guitar"),
        Column(ChartInstrument.Bass, "Bass"),
        Column(ChartInstrument.Rhythm, "Rhythm"),
        Column(ChartInstrument.GuitarCoop, "Guitar Coop"),
        Column(ChartInstrument.Keys, "Keys"),
        Column(ChartInstrument.Drums, "Drums"),
        new(ChartInstrument.Drums, SongIniPartKeys.ProDrumsDifficulty, DifficultyInstrument.ProDrums, "Pro Drums"),
        Column(ChartInstrument.GhlGuitar, "GHL Guitar"),
        Column(ChartInstrument.GhlBass, "GHL Bass"),
        Column(ChartInstrument.GhlRhythm, "GHL Rhythm"),
        Column(ChartInstrument.GhlCoop, "GHL Coop"),
    ];

    /// <summary>Each column's heading, in order. Drums' first two attributes are the kick and the hands.</summary>
    public static IReadOnlyList<string> Header { get; } =
    [
        "Song",
        .. Columns.SelectMany(c => new[]
        {
            $"{c.Name} reported",
            $"{c.Name} analyzed",
            $"{c.Name} {(c.Model.IsDrums() ? "kick" : "strum")}",
            $"{c.Name} {(c.Model.IsDrums() ? "hands" : "note")}",
            $"{c.Name} rhythm",
            $"{c.Name} pattern",
            $"{c.Name} endurance",
        }),
        "Problem",
    ];

    /// <summary>
    /// The rows of every song under <paramref name="folder"/>, down to <paramref name="maxDepth"/> levels, in the order the
    /// library lists them. Songs are read <paramref name="parallelism"/> at a time; <paramref name="progress"/> hears how
    /// many are done each time one is.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="folder"/> doesn't exist.</exception>
    public static async Task<IReadOnlyList<DifficultyReportRow>> BuildAsync(
        string folder, int maxDepth, int parallelism, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var entries = SongLibraryScanner.Enumerate(folder, maxDepth, cancellationToken).ToList();
        var rows = new DifficultyReportRow[entries.Count];
        int done = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (index, token) =>
            {
                rows[index] = await RowAsync(entries[index], token).ConfigureAwait(false);
                progress?.Report((Interlocked.Increment(ref done), entries.Count));
            }).ConfigureAwait(false);
        return rows;
    }

    /// <summary><paramref name="entry"/>'s row: its parts, or why it couldn't be read.</summary>
    public static async Task<DifficultyReportRow> RowAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        SongOpenResult result;
        try
        {
            result = await SongOpener.OpenAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DifficultyReportRow(entry.FullPath, [], ex.Message);
        }

        if (result.Song is not { } song)
        {
            string problem = result.Problem is { } p ? (p.Detail is { } detail ? $"{p.Kind}: {detail}" : p.Kind.ToString()) : "Not read";
            return new DifficultyReportRow(entry.FullPath, [], problem);
        }

        try
        {
            var parts = Columns.Select(column => new DifficultyReportPart(
                column.Instrument,
                column.Key,
                ReportedOf(song.Pairs, column.Key),
                column.Model == DifficultyInstrument.ProDrums && song.Chart.DrumsVariant != DrumsVariant.Pro
                    ? null
                    : DifficultyEstimator.EstimatePart(song.Chart, column.Instrument, song.Options, column.Model)?.Rating)).ToList();
            return new DifficultyReportRow(entry.FullPath, parts, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DifficultyReportRow(entry.FullPath, [], ex.Message);
        }
    }

    /// <summary>Writes <paramref name="rows"/> as CSV, a heading line first.</summary>
    public static void WriteCsv(TextWriter writer, IEnumerable<DifficultyReportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);
        writer.WriteLine(string.Join(",", Header.Select(Field)));
        foreach (var row in rows)
        {
            var fields = new List<string> { Field(row.SongPath) };
            foreach (var column in Columns)
            {
                var part = row.Parts.FirstOrDefault(p => p.Instrument == column.Instrument && string.Equals(p.Key, column.Key, StringComparison.OrdinalIgnoreCase));
                fields.Add(part?.Reported is { } reported ? reported.ToString(CultureInfo.InvariantCulture) : "");
                var rating = part?.Analyzed;
                fields.Add(Tenth(rating?.Rank));
                fields.Add(Tenth(rating?.Attributes.Strum));
                fields.Add(Tenth(rating?.Attributes.Note));
                fields.Add(Tenth(rating?.Attributes.Rhythm));
                fields.Add(Tenth(rating?.Attributes.Pattern));
                fields.Add(Tenth(rating?.Attributes.Endurance));
            }

            fields.Add(Field(row.Problem ?? ""));
            writer.WriteLine(string.Join(",", fields));
        }
    }

    // The diff_* value, the last if song.ini has it twice; null when it has none, or not a number.
    private static int? ReportedOf(IReadOnlyList<KeyValuePair<string, string>> pairs, string key)
    {
        string? value = pairs.LastOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        return int.TryParse(value?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tier) ? tier : null;
    }

    // A part's column for its own diff_* key, rated by its own model.
    private static DifficultyReportColumn Column(ChartInstrument instrument, string name) =>
        new(instrument, SongIniPartKeys.DifficultyKeysOf(instrument, null)[0], DifficultyInstruments.Of(instrument), name);

    private static string Tenth(double? value) =>
        value is { } v ? Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) : "";

    // A CSV field, quoted when it holds a comma, a quote or a line break.
    private static string Field(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
