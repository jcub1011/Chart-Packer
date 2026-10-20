using System.Globalization;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>One part of a song in the difficulty report: its <c>song.ini</c> rating, and what the model made of it.</summary>
/// <param name="Instrument">The part.</param>
/// <param name="Reported">Its <c>diff_*</c> value as <c>song.ini</c> has it (-1 for unrated); null with no value.</param>
/// <param name="Analyzed">What the model made of its highest charted difficulty; null when the song hasn't the part.</param>
public sealed record DifficultyReportPart(ChartInstrument Instrument, int? Reported, DifficultyRating? Analyzed);

/// <summary>A song in the difficulty report.</summary>
/// <param name="SongPath">The song folder or <c>.sng</c>, as a full path.</param>
/// <param name="Parts">Each of <see cref="DifficultyReport.Instruments"/>, in that order.</param>
/// <param name="Problem">Why the song couldn't be read, or null when it was.</param>
public sealed record DifficultyReportRow(string SongPath, IReadOnlyList<DifficultyReportPart> Parts, string? Problem);

/// <summary>
/// Rates every song under a folder with the difficulty model, beside the ratings their <c>song.ini</c> gives, and writes
/// the result as CSV: for each part the model covers, the reported rating, the model's rank and each attribute's score,
/// to a tenth. For checking and tuning the model against charts already rated.
/// </summary>
/// <remarks>Songs are only read, never written.</remarks>
public static class DifficultyReport
{
    /// <summary>The parts reported: the five-fret ones the difficulty model rates and the games read.</summary>
    public static IReadOnlyList<ChartInstrument> Instruments { get; } =
        [ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Rhythm, ChartInstrument.GuitarCoop];

    /// <summary>Each column's heading, in order.</summary>
    public static IReadOnlyList<string> Header { get; } =
    [
        "Song",
        .. Instruments.SelectMany(i => new[]
        {
            $"{NameOf(i)} reported",
            $"{NameOf(i)} analyzed",
            $"{NameOf(i)} strum",
            $"{NameOf(i)} note",
            $"{NameOf(i)} rhythm",
            $"{NameOf(i)} pattern",
            $"{NameOf(i)} endurance",
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
            var parts = Instruments.Select(instrument => new DifficultyReportPart(
                instrument,
                ReportedOf(song.Pairs, instrument),
                DifficultyEstimator.EstimatePart(song.Chart, instrument, song.Options)?.Rating)).ToList();
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
            foreach (var instrument in Instruments)
            {
                var part = row.Parts.FirstOrDefault(p => p.Instrument == instrument);
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

    // The part's own diff_* value, the last if song.ini has it twice; null when it has none, or not a number.
    private static int? ReportedOf(IReadOnlyList<KeyValuePair<string, string>> pairs, ChartInstrument instrument)
    {
        string key = SongIniPartKeys.DifficultyKeysOf(instrument, null)[0];
        string? value = pairs.LastOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        return int.TryParse(value?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tier) ? tier : null;
    }

    private static string NameOf(ChartInstrument instrument) => instrument == ChartInstrument.GuitarCoop ? "Guitar Coop" : instrument.ToString();

    private static string Tenth(double? value) =>
        value is { } v ? Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) : "";

    // A CSV field, quoted when it holds a comma, a quote or a line break.
    private static string Field(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
