using ChartShared.Core.Scanning;

namespace ChartShared.Core.Charts;

/// <summary>Reads a song's chart, <c>notes.chart</c> or <c>notes.mid</c>, into a <see cref="ChartDocument"/>.</summary>
public static class ChartReader
{
    /// <summary>The largest chart that's read.</summary>
    public const long MaxBytes = 32 * 1024 * 1024;

    /// <summary>Reads <paramref name="bytes"/> as the format <paramref name="storedName"/>'s extension says.</summary>
    /// <exception cref="ArgumentException"><paramref name="storedName"/> isn't a chart's name.</exception>
    /// <exception cref="ChartFormatException">The chart is malformed.</exception>
    public static ChartDocument Read(byte[] bytes, string storedName, ChartReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return IsMidi(storedName) ? MidiChartReader.Read(bytes, options ?? ChartReadOptions.Default) : ChartTextReader.Read(bytes, options);
    }

    /// <summary>
    /// The chart a song plays, among its stored names: <c>notes.mid</c> when it has both, as
    /// Clone Hero does; otherwise <c>notes.chart</c>.
    /// </summary>
    public static string? Pick(IEnumerable<string> storedNames)
    {
        ArgumentNullException.ThrowIfNull(storedNames);
        var names = ChartFiles.FilesOf(storedNames);
        return names.FirstOrDefault(n => n.Equals(ChartFiles.MidiName, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
    }

    internal static bool IsMidi(string storedName) =>
        ChartFiles.StoredNameFor(storedName ?? throw new ArgumentNullException(nameof(storedName))) switch
        {
            ChartFiles.MidiName => true,
            ChartFiles.ChartName => false,
            _ => throw new ArgumentException($"'{storedName}' isn't a chart.", nameof(storedName)),
        };
}
