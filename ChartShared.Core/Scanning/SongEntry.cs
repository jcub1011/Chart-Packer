namespace ChartShared.Core.Scanning;

/// <summary>A song source found by <see cref="SongLibraryScanner"/>.</summary>
/// <param name="Kind">Whether this is a <c>.sng</c> file or a song folder.</param>
/// <param name="FullPath">Absolute path to the file or folder.</param>
/// <param name="RelativePath">Path relative to the scan root; <c>.</c> when the root itself is a song folder.</param>
/// <param name="DisplayName">The file name without <c>.sng</c>, or the folder name.</param>
public sealed record SongEntry(SongEntryKind Kind, string FullPath, string RelativePath, string DisplayName);
