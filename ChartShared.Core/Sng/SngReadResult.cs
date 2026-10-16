using ChartShared.Core.Validation;

namespace ChartShared.Core.Sng;

/// <summary>The fixed 26-byte header of a <c>.sng</c> (sng-spec §4.1).</summary>
/// <param name="Version">The format version. Only 1 is defined.</param>
/// <param name="Mask">The 16-byte XOR mask seed.</param>
public sealed record SngHeader(uint Version, ReadOnlyMemory<byte> Mask);

/// <summary>One metadata key/value pair (sng-spec §4.2).</summary>
/// <param name="Key">The key. Undecodable bytes are shown as U+FFFD and reported as SNG013.</param>
/// <param name="Value">The value, decoded the same way as the key.</param>
/// <param name="Offset">Byte offset of the pair's <c>keyLen</c> field in the <c>.sng</c>.</param>
public sealed record SngMetadataPair(string Key, string Value, long Offset);

/// <summary>One FileIndex entry (sng-spec §4.3). The file's data is not read.</summary>
/// <param name="Name">The stored filename: a <c>/</c>-separated path relative to the song folder.</param>
/// <param name="Length">The file's size in bytes (<c>contentsLen</c>).</param>
/// <param name="Offset">The file's first byte, from the start of the <c>.sng</c> (<c>contentsIndex</c>, sng-spec A4).</param>
public sealed record SngFileEntry(string Name, ulong Length, ulong Offset);

/// <summary>Everything <see cref="SngReader"/> could parse, plus the structural problems it found.</summary>
/// <remarks>
/// Results are partial rather than all-or-nothing: if the file index is damaged, the metadata
/// read before it is still returned.
/// </remarks>
public sealed record SngReadResult(
    SngHeader? Header,
    IReadOnlyList<SngMetadataPair> Metadata,
    IReadOnlyList<SngFileEntry> Files,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>True when any finding is an <see cref="Severity.Error"/>.</summary>
    public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);
}
