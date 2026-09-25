namespace ChartPacker.Core.Scanning;

/// <summary>What kind of song source a <see cref="SongEntry"/> points at.</summary>
public enum SongEntryKind
{
    /// <summary>A packed <c>.sng</c> file.</summary>
    SngFile,

    /// <summary>An unpacked song folder, identified by the <c>song.ini</c> it contains.</summary>
    SongFolder,
}
