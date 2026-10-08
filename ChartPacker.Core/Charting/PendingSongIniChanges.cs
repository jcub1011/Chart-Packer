using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Charting;

/// <summary>Why Chart Builder calls for a <c>song.ini</c> change (charter-spec C17 item 2).</summary>
public enum PendingChangeReason
{
    /// <summary>A value the song had nowhere else: a <c>.chart</c>'s <c>[Song]</c> value, or a new chart's name or artist.</summary>
    ImportedValue,

    /// <summary>An empty <c>charter</c>, filled from the Default charter name setting.</summary>
    Charter,

    /// <summary>A <c>.chart</c> import's <c>Offset</c>, which only <c>delay</c> can carry once it's a <c>notes.mid</c>.</summary>
    Delay,

    /// <summary><c>song_length</c>, unset or more than a second from the audio's length.</summary>
    SongLength,

    /// <summary>An unrated difficulty for a part added since the song was opened.</summary>
    PartRating,

    /// <summary>The drum layout keys the chart's drum variant needs.</summary>
    DrumLayout,

    /// <summary>A <c>.chart</c> import's <c>sustain_cutoff_threshold</c>, which keeps its sustains sustains once it's a <c>notes.mid</c>.</summary>
    ImportSettings,

    /// <summary><c>multiplier_note</c> removed, as the chart's star power is saved on 116.</summary>
    MultiplierNote,

    /// <summary>The delay Find first downbeat set, so the chart starts on the downbeat (charter-spec C14 item 5).</summary>
    DelayFromDownbeat,
}

/// <summary>A change to <c>song.ini</c> that saving makes, and why.</summary>
/// <param name="Value">The new value, or null when the key is removed.</param>
public sealed record PendingSongIniChange(string Key, string? Value, PendingChangeReason Reason)
{
    /// <summary>
    /// Whether the user can leave it out of the save. The drum layout keys, an import's sustain cutoff and
    /// <c>multiplier_note</c> decide how the saved chart is read, so leaving one out would save a chart that plays
    /// differently from what the editor shows.
    /// </summary>
    public bool CanRevert => Reason is not (PendingChangeReason.DrumLayout or PendingChangeReason.ImportSettings or PendingChangeReason.MultiplierNote);

    /// <summary>As a metadata edit, for a draft.</summary>
    public MetadataChange ToMetadataChange() => new(Key, Value);
}

/// <summary>What the song's pending changes are worked out from, beside the chart.</summary>
/// <param name="LoadedParts">The parts the chart had when it was opened, which have no rating added.</param>
/// <param name="Pairs">The song's <c>song.ini</c> as it was opened, or as it was last saved.</param>
public sealed record PendingSongIniInputs(IReadOnlySet<ChartInstrument> LoadedParts, IReadOnlyList<KeyValuePair<string, string>> Pairs)
{
    /// <summary>The longest stem's length in milliseconds, once it's been measured; null for none, or not yet.</summary>
    public long? AudioLengthMs { get; init; }

    /// <summary>The Default charter name setting; empty for none.</summary>
    public string DefaultCharter { get; init; } = "";

    /// <summary>For a <c>notes.chart</c> not yet saved as a <c>notes.mid</c>, what converting it gave.</summary>
    public ChartImportResult? Import { get; init; }

    /// <summary>Values the song has nowhere else, such as a new chart's name and artist, written where <c>song.ini</c> has none.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Values { get; init; } = [];

    /// <summary>
    /// The <c>delay</c> Find first downbeat set, in milliseconds (charter-spec C14 item 5), in place of the one the song
    /// has; null for none.
    /// </summary>
    public long? DelayMs { get; init; }
}

/// <summary>
/// The <c>song.ini</c> changes an open chart calls for, shown as pending changes until they're saved (charter-spec C17
/// item 2).
/// </summary>
/// <remarks>
/// They're worked out from the chart and the song as they are, not kept as edits are made, so undoing an edit takes its
/// change away, and a variant detected on load (§4.1) is among them as well. A change <c>song.ini</c> already has isn't
/// listed.
/// </remarks>
public static class PendingSongIniChanges
{
    /// <summary>How far <c>song_length</c> can be from the audio's length before it's corrected, in milliseconds.</summary>
    public const long SongLengthTolerance = 1000;

    /// <summary>
    /// The changes <paramref name="chart"/> calls for, as metadata edits: the parts' ratings and the drum keys, the
    /// ones that follow from the chart and the song alone.
    /// </summary>
    /// <param name="loadedParts">The parts the chart had when it was opened, which have no rating added.</param>
    /// <param name="pairs">The song's <c>song.ini</c> as it was opened.</param>
    public static IReadOnlyList<MetadataChange> Of(EditableChart chart, IReadOnlySet<ChartInstrument> loadedParts, IReadOnlyList<KeyValuePair<string, string>> pairs) =>
        [.. Of(chart, new PendingSongIniInputs(loadedParts, pairs)).Select(c => c.ToMetadataChange())];

    /// <summary>
    /// The changes <paramref name="chart"/> and <paramref name="inputs"/> call for, in the order they're listed: the
    /// values the song has nowhere else, <c>charter</c>, <c>delay</c>, <c>song_length</c>, the parts' ratings in part
    /// order, the drum keys, an import's sustain cutoff, then <c>multiplier_note</c>.
    /// </summary>
    public static IReadOnlyList<PendingSongIniChange> Of(EditableChart chart, PendingSongIniInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(inputs);
        var pairs = inputs.Pairs;
        var changes = new List<PendingSongIniChange>();
        bool Has(string key) => !string.IsNullOrWhiteSpace(SongIniValues.Last(pairs, key));

        var values = inputs.Values.Concat(inputs.Import?.SongValues ?? []);
        foreach (var (key, value) in values)
        {
            if (!Has(key) && !string.IsNullOrWhiteSpace(value) && !changes.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                changes.Add(new PendingSongIniChange(key, value.Trim(), PendingChangeReason.ImportedValue));
        }

        if (!Has("charter") && !Has("frets") && !changes.Any(c => c.Key == "charter") && inputs.DefaultCharter.Trim() is { Length: > 0 } charter)
            changes.Add(new PendingSongIniChange("charter", charter, PendingChangeReason.Charter));

        if (inputs.DelayMs is { } set)
        {
            // In place of whatever delay the song reads now; a delay of 0 is passed over for delay_seconds, so that goes too.
            long now = (long)Math.Round(SongChartSettings.DelayOf(pairs, inputs.Import?.Offset).TotalMilliseconds, MidpointRounding.AwayFromZero);
            if (set != now)
            {
                changes.Add(new PendingSongIniChange("delay", set.ToString(CultureInfo.InvariantCulture), PendingChangeReason.DelayFromDownbeat));
                if (set == 0 && Has("delay_seconds"))
                    changes.Add(new PendingSongIniChange("delay_seconds", null, PendingChangeReason.DelayFromDownbeat));
            }
        }
        else if (inputs.Import?.Offset is { } offset && DelayChange(offset, pairs) is { } delay)
        {
            changes.Add(new PendingSongIniChange("delay", delay, PendingChangeReason.Delay));
        }

        if (inputs.AudioLengthMs is { } length && length > 0)
        {
            long? saved = SongIniValues.Integer(SongIniValues.Last(pairs, "song_length"));
            if (saved is null || Math.Abs(saved.Value - length) > SongLengthTolerance)
                changes.Add(new PendingSongIniChange("song_length", length.ToString(CultureInfo.InvariantCulture), PendingChangeReason.SongLength));
        }

        foreach (var part in chart.Parts)
        {
            if (inputs.LoadedParts.Contains(part.Instrument) || !NewChartSettings.Instruments.Contains(part.Instrument))
                continue;
            foreach (var key in SongIniPartKeys.DifficultyKeysOf(part.Instrument, chart.DrumsVariant))
            {
                if (SongIniValues.Last(pairs, key) is null)
                    changes.Add(new PendingSongIniChange(key, MetadataDifficulty.UnsetValue, PendingChangeReason.PartRating));
            }
        }

        var song = ChartReadOptions.From(pairs);
        if (chart.PartOf(ChartInstrument.Drums) is not null && chart.DrumsVariant is { } variant)
            changes.AddRange(DrumsChanges(variant, song));

        if (inputs.Import?.Options.SustainCutoff is { } cutoff && song.SustainCutoff != cutoff)
            changes.Add(new PendingSongIniChange("sustain_cutoff_threshold", cutoff.ToString(CultureInfo.InvariantCulture), PendingChangeReason.ImportSettings));

        if (song.MultiplierNote == 103)
        {
            foreach (string key in new[] { "multiplier_note", "star_power_note" }.Where(Has))
                changes.Add(new PendingSongIniChange(key, null, PendingChangeReason.MultiplierNote));
        }

        return changes;
    }

    /// <summary>
    /// <paramref name="pairs"/> with <paramref name="changes"/> applied as a save applies them
    /// (<see cref="MetadataDraft.ApplyTo"/>): a changed key's value replaced, or added at the end, and a removed key's
    /// values left out.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Apply(IReadOnlyList<KeyValuePair<string, string>> pairs, IEnumerable<PendingSongIniChange> changes)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(changes);
        var draft = new MetadataDraft();
        foreach (var change in changes)
        {
            if (change.Value is null)
                draft.Remove(change.Key);
            else
                draft.Set(change.Key, change.Value);
        }

        return draft.ApplyTo(pairs);
    }

    // The delay the import's offset needs, when song.ini sets none that YARG would read before the chart's own.
    private static string? DelayChange(TimeSpan offset, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        long ms = (long)Math.Round(offset.TotalMilliseconds, MidpointRounding.AwayFromZero);
        if (ms == 0 || SongIniValues.Integer(SongIniValues.Last(pairs, "delay")) is { } delay && delay != 0)
            return null;
        if (SongIniValues.Real(SongIniValues.Last(pairs, "delay_seconds")) is not null)
            return null;
        return ms.ToString(CultureInfo.InvariantCulture);
    }

    // The keys that make the song read its drums as the variant, as MidiChartWriter.SettingsAfterSave has them.
    private static IEnumerable<PendingSongIniChange> DrumsChanges(DrumsVariant variant, ChartReadOptions song)
    {
        static PendingSongIniChange Change(string key, string value) => new(key, value, PendingChangeReason.DrumLayout);
        switch (variant)
        {
            case DrumsVariant.FourLane:
                if (song.ProDrums != false)
                    yield return Change("pro_drums", "False");
                if (song.FiveLaneDrums == true)
                    yield return Change("five_lane_drums", "False");
                break;
            case DrumsVariant.Pro:
                if (song.ProDrums != true)
                    yield return Change("pro_drums", "True");
                if (song.FiveLaneDrums == true)
                    yield return Change("five_lane_drums", "False");
                break;
            case DrumsVariant.FiveLane:
                if (song.FiveLaneDrums != true)
                    yield return Change("five_lane_drums", "True");
                break;
        }
    }
}
