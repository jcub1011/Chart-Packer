using ChartPacker.Core.Charting.Layout;

namespace ChartPacker.Audio.Analysis;

/// <summary>
/// The spectrogram's band presets (charter-spec C13 item 4): which rows each emphasises, the rest being dimmed, to help
/// pick out drum hits and bass notes.
/// </summary>
public static class SpectrogramBands
{
    private static readonly Dictionary<SpectrogramBand, bool[]> Masks = new()
    {
        [SpectrogramBand.Full] = Mask((SpectrogramRows.LowHz, SpectrogramRows.HighHz)),
        [SpectrogramBand.Kick] = Mask((30, 120)),
        [SpectrogramBand.Snare] = Mask((150, 300), (2000, 5000)),
        [SpectrogramBand.Cymbals] = Mask((6000, 16000)),
        [SpectrogramBand.Bass] = Mask((40, 400)),
    };

    /// <summary>The frequency ranges <paramref name="band"/> emphasises, in Hz.</summary>
    public static IReadOnlyList<(double LowHz, double HighHz)> RangesOf(SpectrogramBand band) => band switch
    {
        SpectrogramBand.Kick => [(30, 120)],
        SpectrogramBand.Snare => [(150, 300), (2000, 5000)],
        SpectrogramBand.Cymbals => [(6000, 16000)],
        SpectrogramBand.Bass => [(40, 400)],
        _ => [(SpectrogramRows.LowHz, SpectrogramRows.HighHz)],
    };

    /// <summary>Whether each row, lowest first, is in <paramref name="band"/>: drawn at full strength rather than dimmed.</summary>
    public static IReadOnlyList<bool> Emphasised(SpectrogramBand band) => Masks.TryGetValue(band, out var mask) ? mask : Masks[SpectrogramBand.Full];

    private static bool[] Mask(params (double LowHz, double HighHz)[] ranges)
    {
        var mask = new bool[SpectrogramRows.Count];
        foreach (var (low, high) in ranges)
        {
            var (first, last) = SpectrogramRows.RowsOf(low, high);
            for (int row = first; row <= last; row++)
                mask[row] = true;
        }

        return mask;
    }
}
