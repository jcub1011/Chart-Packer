using ChartPacker.Core.Charting.Lint.Rules;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>
/// The quick fixes that change <c>song.ini</c> rather than the chart, through Chart Builder's pending changes (charter-spec
/// C17 item 2): a fix restores a pending change the user reverted. They aren't undo steps, as reverting isn't.
/// </summary>
public static class PendingSongIniFixes
{
    /// <summary>
    /// The key of the reverted pending change that fixes <paramref name="finding"/>, or null when it has none: CHR084's
    /// <c>song_length</c>, when its correction was reverted.
    /// </summary>
    public static string? KeyToRestore(Finding finding, EditorSong song)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(song);
        if (finding.RuleId != ChrRules.Chr084 || !song.Reverted.Contains(SongLengthRule.Key))
            return null;
        return song.AllPendingChanges().Any(c => c.Key == SongLengthRule.Key) ? SongLengthRule.Key : null;
    }
}
