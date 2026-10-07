using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Charting;

/// <summary>
/// The <c>song.ini</c> changes an open chart calls for, shown as pending changes until they're saved (charter-spec C17
/// item 2): an unrated difficulty for each part added, and the drum layout keys for the chart's drum variant.
/// </summary>
/// <remarks>
/// They're worked out from the chart as it is, not kept as edits are made, so undoing an edit takes its change away, and a
/// variant detected on load (§4.1) is among them as well. A change <c>song.ini</c> already has isn't listed.
/// </remarks>
public static class PendingSongIniChanges
{
    /// <summary>The changes <paramref name="chart"/> calls for, in the order they're listed: the parts' ratings in part order, then the drum keys.</summary>
    /// <param name="loadedParts">The parts the chart had when it was opened, which have no rating added.</param>
    /// <param name="pairs">The song's <c>song.ini</c> as it was opened.</param>
    public static IReadOnlyList<MetadataChange> Of(EditableChart chart, IReadOnlySet<ChartInstrument> loadedParts, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(loadedParts);
        ArgumentNullException.ThrowIfNull(pairs);

        var changes = new List<MetadataChange>();
        foreach (var part in chart.Parts)
        {
            if (loadedParts.Contains(part.Instrument) || !NewChartSettings.Instruments.Contains(part.Instrument))
                continue;
            foreach (var key in SongIniPartKeys.DifficultyKeysOf(part.Instrument, chart.DrumsVariant))
            {
                if (SongIniValues.Last(pairs, key) is null)
                    changes.Add(new MetadataChange(key, MetadataDifficulty.UnsetValue));
            }
        }

        if (chart.PartOf(ChartInstrument.Drums) is not null && chart.DrumsVariant is { } variant)
            changes.AddRange(DrumsChanges(variant, ChartReadOptions.From(pairs)));
        return changes;
    }

    // The keys that make the song read its drums as the variant, as MidiChartWriter.SettingsAfterSave has them.
    private static IEnumerable<MetadataChange> DrumsChanges(DrumsVariant variant, ChartReadOptions song)
    {
        switch (variant)
        {
            case DrumsVariant.FourLane:
                if (song.ProDrums != false)
                    yield return new MetadataChange("pro_drums", "False");
                if (song.FiveLaneDrums == true)
                    yield return new MetadataChange("five_lane_drums", "False");
                break;
            case DrumsVariant.Pro:
                if (song.ProDrums != true)
                    yield return new MetadataChange("pro_drums", "True");
                if (song.FiveLaneDrums == true)
                    yield return new MetadataChange("five_lane_drums", "False");
                break;
            case DrumsVariant.FiveLane:
                if (song.FiveLaneDrums != true)
                    yield return new MetadataChange("five_lane_drums", "True");
                break;
        }
    }
}
