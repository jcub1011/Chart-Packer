using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// Sets <c>song.ini</c> values of the open song as one undo step (charter-spec C1, Chart settings): each key's value as the
/// next save leaves it, or its absence. The chart itself isn't touched.
/// </summary>
/// <remarks>
/// It keeps the values as the save would write them, before and after, rather than the edits that make them, so undoing it
/// still gives back what the song had once the song has been saved since. A value that changed as a side effect, such as
/// <c>delay_seconds</c> beside the delay Find first downbeat set, is kept too.
/// </remarks>
public sealed class SongValueCommand : ChartCommand
{
    private readonly EditorSong _song;
    private Dictionary<string, string?>? _before;
    private Dictionary<string, string?> _after;
    private bool _done;

    /// <param name="song">The song, whose chart the command is applied to.</param>
    /// <param name="values">Each key's new value, or null to remove it.</param>
    public SongValueCommand(EditorSong song, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException("There's nothing to change.", nameof(values));
        _song = song;
        Values = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        _after = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The values the command sets, as it was made: each key's, or null to remove it.</summary>
    public IReadOnlyDictionary<string, string?> Values { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        RequireSongChart(chart);
        if (_done)
        {
            _song.SetEffectiveValues(_after);
            return ChartChange.Of(SongChanges.SongIni);
        }

        // The first time, every value the change moves is kept, so undo puts back the side effects too.
        var before = ValuesOf(_song.EffectivePairs());
        _song.SetEffectiveValues(_after);
        var after = ValuesOf(_song.EffectivePairs());
        _before = Diff(before, after, out _after);
        _done = true;
        return ChartChange.Of(SongChanges.SongIni);
    }

    private protected override void Undo(EditableChart chart)
    {
        RequireSongChart(chart);
        _song.SetEffectiveValues(_before!);
    }

    private void RequireSongChart(EditableChart chart)
    {
        if (!ReferenceEquals(chart, _song.Chart))
            throw new InvalidOperationException("The command belongs to another song.");
    }

    // The last value of each key, as song.ini is read.
    private static Dictionary<string, string?> ValuesOf(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
            values[key] = value;
        return values;
    }

    // The keys whose values differ, with each side's value.
    private static Dictionary<string, string?> Diff(Dictionary<string, string?> before, Dictionary<string, string?> after, out Dictionary<string, string?> changedAfter)
    {
        var changedBefore = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        changedAfter = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            string? was = before.GetValueOrDefault(key);
            string? now = after.GetValueOrDefault(key);
            if (was == now)
                continue;
            changedBefore[key] = was;
            changedAfter[key] = now;
        }

        return changedBefore;
    }
}

/// <summary>
/// The undoable <c>song.ini</c> edits Chart settings makes (charter-spec C1): each a <see cref="SongValueCommand"/>, or
/// null when it would change nothing.
/// </summary>
public static class SongCommands
{
    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>.</summary>
    public static SongValueCommand? SetValue(EditorSong song, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetValues(song, new Dictionary<string, string?> { [key] = value });
    }

    /// <summary>Removes <paramref name="key"/>.</summary>
    public static SongValueCommand? Remove(EditorSong song, string key) => SetValues(song, new Dictionary<string, string?> { [key] = null });

    /// <summary>Puts <paramref name="key"/> back as the song has it saved, taking back every change to it.</summary>
    public static SongValueCommand? RevertToSaved(EditorSong song, string key)
    {
        ArgumentNullException.ThrowIfNull(song);
        return SetValues(song, new Dictionary<string, string?> { [key] = SongIniValues.Last(song.Pairs, key) });
    }

    /// <summary>
    /// Sets the difficulty rating <paramref name="key"/> to <paramref name="tier"/>, or clears it to <c>-1</c>, unrated,
    /// for null.
    /// </summary>
    public static SongValueCommand? SetDifficulty(EditorSong song, string key, int? tier)
    {
        if (tier is < 0)
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "A rating is 0 or more; null clears it.");
        string value = tier?.ToString(CultureInfo.InvariantCulture) ?? MetadataDifficulty.UnsetValue;
        return SetValues(song, new Dictionary<string, string?> { [key] = value });
    }

    /// <summary>
    /// Sets <c>delay</c> to <paramref name="ms"/>, and when that's 0, removes <c>delay_seconds</c>, which the games read in
    /// place of a <c>delay</c> of 0, as <see cref="EditorSong.SetDelay"/> does.
    /// </summary>
    public static SongValueCommand? SetDelay(EditorSong song, long ms)
    {
        ArgumentNullException.ThrowIfNull(song);
        var values = new Dictionary<string, string?> { ["delay"] = ms.ToString(CultureInfo.InvariantCulture) };
        if (ms == 0 && !string.IsNullOrWhiteSpace(SongIniValues.Last(song.Pairs, "delay_seconds")))
            values["delay_seconds"] = null;
        return SetValues(song, values);
    }

    /// <summary>
    /// Sets <c>video_start_time</c> to <paramref name="ms"/>: where in the background video the song starts. When that's
    /// where the saved song starts it, the key is put back as saved, so a value moved away and back leaves no change.
    /// </summary>
    public static SongValueCommand? SetVideoStart(EditorSong song, long ms)
    {
        ArgumentNullException.ThrowIfNull(song);
        return VideoTimeline.From(song.Pairs).Start == TimeSpan.FromMilliseconds(ms)
            ? RevertToSaved(song, TimingOffsets.VideoKey)
            : SetValue(song, TimingOffsets.VideoKey, ms.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Sets each key of <paramref name="values"/> to its value, or removes it for null, as one step.</summary>
    public static SongValueCommand? SetValues(EditorSong song, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(values);
        foreach (string key in values.Keys)
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(values));

        var changed = values
            .Where(v => song.CanEdit(v.Key) && song.EffectiveValue(v.Key) != v.Value)
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return changed.Count == 0 ? null : new SongValueCommand(song, changed);
    }
}
