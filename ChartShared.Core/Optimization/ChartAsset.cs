using ChartShared.Core.Dedup;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>What a file in a chart is, as far as optimizing goes.</summary>
public enum AssetKind
{
    /// <summary>A registered audio stem (<see cref="AudioStems"/>).</summary>
    Audio,

    /// <summary>The background video (<see cref="SongBackgrounds"/>).</summary>
    Video,

    /// <summary>The album art (<see cref="AlbumArt"/>).</summary>
    AlbumArt,

    /// <summary>The background image (<see cref="SongBackgrounds"/>).</summary>
    BackgroundImage,

    /// <summary>A <c>highway.*</c> image. It's always left alone.</summary>
    HighwayImage,

    /// <summary><c>notes.chart</c> or <c>notes.mid</c>.</summary>
    Chart,

    /// <summary>A folder's <c>song.ini</c>.</summary>
    SongIni,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>What a probe learned about a file.</summary>
/// <param name="CodecKey">The codec, as <c>kind/codec</c>: <c>video/vp9</c>, <c>audio/ogg</c>, <c>image/png</c>.</param>
/// <param name="Width">The width in pixels, for an image or video.</param>
/// <param name="Height">The height in pixels, for an image or video.</param>
/// <param name="Duration">How long it plays, for audio or video.</param>
public sealed record ProbeResult(string CodecKey, int? Width = null, int? Height = null, TimeSpan? Duration = null);

/// <summary>A file directly in a chart.</summary>
/// <param name="StoredName">Its name in the chart.</param>
/// <param name="Extension">Its lowercased extension, with the dot.</param>
/// <param name="Length">Its size in bytes.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Stem">The audio stem, for <see cref="AssetKind.Audio"/>.</param>
/// <param name="Probe">What a probe learned about it; <see langword="null"/> when none could.</param>
public sealed record ChartAsset(string StoredName, string Extension, long Length, AssetKind Kind, string? Stem = null, ProbeResult? Probe = null)
{
    /// <summary>The probed codec, or <c>kind/ext</c> from the extension when it wasn't probed.</summary>
    public string CodecKey => Probe?.CodecKey ?? KindPrefix(Kind) + FormatName(Extension.TrimStart('.'));

    // .jpg and .jpeg are one format, so they group as one.
    private static string FormatName(string extension) => extension == "jpg" ? "jpeg" : extension;

    private static string KindPrefix(AssetKind kind) => kind switch
    {
        AssetKind.Audio => "audio/",
        AssetKind.Video => "video/",
        AssetKind.AlbumArt or AssetKind.BackgroundImage or AssetKind.HighwayImage => "image/",
        _ => "file/",
    };
}

/// <summary>A chart and the files directly in it.</summary>
/// <param name="Entry">The chart.</param>
/// <param name="Assets">Its files.</param>
/// <param name="Stamp">The chart as it was inspected, for noticing a change before it's written; <see langword="null"/> when not stamped.</param>
public sealed record ChartAssets(SongEntry Entry, IReadOnlyList<ChartAsset> Assets, EntryStamp? Stamp = null)
{
    /// <summary>The total size of the files in <see cref="Assets"/>.</summary>
    public long TotalBytes => Assets.Sum(a => a.Length);
}

/// <summary>A file of a chart, on disk where an optimizer can read it.</summary>
/// <param name="Asset">The file.</param>
/// <param name="Path">Where it can be read: the folder's own file, or a <c>.sng</c>'s extracted to a temp file.</param>
public sealed record AssetFile(ChartAsset Asset, string Path);
