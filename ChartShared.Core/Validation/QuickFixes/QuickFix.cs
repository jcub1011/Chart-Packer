using ChartShared.Core.Metadata;

namespace ChartShared.Core.Validation.QuickFixes;

/// <summary>One change a quick fix makes to a song's metadata.</summary>
/// <param name="Key">The key to change.</param>
/// <param name="Value">The value to set, or <see langword="null"/> to remove the key.</param>
public sealed record MetadataEdit(string Key, string? Value)
{
    /// <summary>True when the edit removes <see cref="Key"/>.</summary>
    public bool IsRemoval => Value is null;
}

/// <summary>
/// The fix for one finding, worked out from the metadata it was found in: what it does, in words, and the
/// edits that make it.
/// </summary>
/// <param name="RuleId">The rule of the finding it fixes.</param>
/// <param name="Key">The key the finding is about.</param>
/// <param name="Description">What the fix does, ready to show, e.g. "Change to True".</param>
/// <param name="Edits">The changes, in order.</param>
public sealed record QuickFix(string RuleId, string Key, string Description, IReadOnlyList<MetadataEdit> Edits)
{
    /// <summary>
    /// Writes the edits to <paramref name="item"/>'s draft, through <see cref="BulkMetadata"/>, so an edit back to
    /// the saved value reverts the key's change rather than marking it.
    /// </summary>
    public void ApplyTo(MetadataItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        MetadataItem[] items = [item];
        foreach (var edit in Edits)
        {
            if (edit.Value is null)
                BulkMetadata.Remove(items, edit.Key);
            else
                BulkMetadata.Set(items, edit.Key, edit.Value);
        }
    }
}
