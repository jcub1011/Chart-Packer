using ChartShared.Core.Charts;
using ChartShared.Core.Ini;
using ChartShared.Core.Metadata;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Optimization;

/// <summary>What metadata cleanup would change in one chart.</summary>
/// <param name="Edits">The metadata edits, to apply to a <c>song.ini</c> with <see cref="SongIni.ApplyEditsAsync"/> or to a <c>.sng</c>'s pairs.</param>
/// <param name="Before">The metadata as it was.</param>
/// <param name="After">The metadata once the edits are applied.</param>
/// <param name="MidiName">The stored name of the MIDI chart <paramref name="NewMidiBytes"/> replaces.</param>
/// <param name="NewMidiBytes">The MIDI chart without its editor events; <see langword="null"/> when it has none to leave out.</param>
/// <param name="MidiBytesBefore">The size of the MIDI chart before.</param>
public sealed record MetadataCleanupPlan(
    MetadataDraft Edits,
    IReadOnlyList<KeyValuePair<string, string>> Before,
    IReadOnlyList<KeyValuePair<string, string>> After,
    string? MidiName,
    byte[]? NewMidiBytes,
    long MidiBytesBefore)
{
    /// <summary>Whether there's anything to change.</summary>
    public bool HasChanges => Edits.HasMetadataChanges || NewMidiBytes is not null;

    /// <summary>The size of the metadata as <c>key = value</c> lines before, plus the MIDI chart.</summary>
    public long BytesBefore => MetadataBytes(Before) + MidiBytesBefore;

    /// <summary>The size after the edits.</summary>
    public long BytesAfter => MetadataBytes(After) + (NewMidiBytes?.Length ?? MidiBytesBefore);

    private static long MetadataBytes(IReadOnlyList<KeyValuePair<string, string>> pairs) =>
        pairs.Sum(p => (long)p.Key.Length + p.Value.Length + 4);
}

/// <summary>
/// Finds the metadata a chart doesn't need, as an optional step of optimizing it: the keys of parts with no notes
/// (<see cref="ExportCleanup.PruneMetadata"/>), the editor events in <c>notes.mid</c> (<see cref="ExportCleanup.StripEditorEvents"/>),
/// keys with an empty value (SNG061), and legacy keys (<see cref="KnownKeys.LegacyReplacements"/>).
/// </summary>
public static class MetadataCleanup
{
    /// <summary>Works out what cleaning <paramref name="entry"/> would change; nothing is written.</summary>
    /// <returns>The plan; <see cref="MetadataCleanupPlan.HasChanges"/> is false when there's nothing to change, or the chart's metadata can't be read.</returns>
    /// <exception cref="IOException">The chart can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The analysis was cancelled.</exception>
    public static async Task<MetadataCleanupPlan> AnalyzeAsync(SongEntry entry, MetadataCleanupOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        var pairs = await ReadMetadataAsync(entry, cancellationToken).ConfigureAwait(false);
        var draft = new MetadataDraft();
        if (pairs is null)
            return new MetadataCleanupPlan(draft, [], [], null, null, 0);

        var current = new List<KeyValuePair<string, string>>(pairs);
        string? midiName = null;
        byte[]? newMidi = null;
        long midiBefore = 0;

        if (options.PruneEmptyParts || options.StripEditorEvents)
        {
            using var reader = await Task.Run(() => SongFileReader.Open(entry, cancellationToken), cancellationToken).ConfigureAwait(false);
            string? chartName = ChartReader.Pick(reader.TopLevel.Select(f => f.Name));
            byte[]? chart = chartName is null ? null : await reader.ReadAsync(chartName, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (chartName is not null && chart is not null)
            {
                bool isMidi = ChartReader.IsMidi(chartName);
                if (options.PruneEmptyParts)
                {
                    var kept = ExportCleanup.PruneMetadata(current, ExportCleanup.PartsOf(chart, chartName, current), isMidi, []);
                    // A draft removes a key whole, duplicates included, so a key is only removed when none of its pairs is kept.
                    foreach (var dropped in current.Where(p => !kept.Contains(p) && !HasKey(kept, p.Key)).ToList())
                        draft.Remove(dropped.Key);
                    current = [.. kept];
                }

                if (options.StripEditorEvents && isMidi && ExportCleanup.TryStripEditorEvents(chart) is { } stripped)
                {
                    midiName = chartName;
                    newMidi = stripped;
                    midiBefore = chart.Length;
                }
            }
        }

        if (options.DropEmptyValues)
        {
            // Only a key whose every pair is empty goes: the draft can't drop one of a duplicated key's pairs and keep another.
            foreach (string key in current.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (current.Where(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Any(p => !string.IsNullOrWhiteSpace(p.Value)))
                    continue;

                draft.Remove(key);
                current.RemoveAll(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (options.ReplaceLegacyKeys)
        {
            foreach (var pair in current.ToList())
            {
                if (!KnownKeys.LegacyReplacements.TryGetValue(pair.Key, out string? replacement)
                    || HasKey(current, replacement))
                    continue;

                // A duplicated legacy key with differing values has no one value to carry across, so it's left as it is.
                var same = current.Where(p => p.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Any(p => p.Value != pair.Value))
                    continue;

                draft.Remove(pair.Key);
                draft.Set(replacement, pair.Value);
                current.RemoveAll(p => p.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                current.Add(KeyValuePair.Create(replacement, pair.Value));
            }
        }

        return new MetadataCleanupPlan(draft, pairs, draft.ApplyTo(pairs), midiName, newMidi, midiBefore);
    }

    private static bool HasKey(IEnumerable<KeyValuePair<string, string>> pairs, string key) =>
        pairs.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<KeyValuePair<string, string>>?> ReadMetadataAsync(SongEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Kind == SongEntryKind.SngFile)
        {
            var read = await SngReader.ReadAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
            return read.HasErrors || read.Header is null ? null : [.. read.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value))];
        }

        string? songIni = SongIni.FindIn(entry.FullPath);
        if (songIni is null)
            return null;

        var ini = await SongIni.ReadAsync(songIni, cancellationToken).ConfigureAwait(false);
        return ini.Status == SongIniStatus.Ok ? ini.Pairs : null;
    }
}
