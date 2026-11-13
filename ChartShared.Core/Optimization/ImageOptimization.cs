using ChartShared.Core.Metadata;

namespace ChartShared.Core.Optimization;

/// <summary>What an image optimizer writes.</summary>
public enum ImageOutput
{
    /// <summary>The image is left as it is: nothing is written.</summary>
    KeepOriginal,

    /// <summary>The image, resized if it must be, as JPEG.</summary>
    Jpeg,

    /// <summary>The image, resized, as PNG.</summary>
    Png,
}

/// <summary>Which encodes an image needs for <see cref="ImageOptimization.Choose"/>.</summary>
/// <param name="Jpeg">Whether the JPEG result is a candidate.</param>
/// <param name="Png">Whether a resized PNG result is a candidate.</param>
public readonly record struct ImageCandidates(bool Jpeg, bool Png);

/// <summary>What an image optimizer settled on for one image.</summary>
/// <param name="Output">What it writes.</param>
/// <param name="Bytes">The size of the result; the original's size for <see cref="ImageOutput.KeepOriginal"/>.</param>
/// <param name="Extension">The extension of the result's name, with the dot.</param>
public sealed record ImageChoice(ImageOutput Output, long Bytes, string Extension)
{
    /// <summary>Whether the image is left as it is.</summary>
    public bool IsKeep => Output == ImageOutput.KeepOriginal;

    /// <summary>The estimate this choice gives the planner.</summary>
    public OptimizeEstimate ToEstimate() => new(Bytes, Extension, AlreadyOptimal: IsKeep);
}

/// <summary>
/// The decisions of the image optimizer, kept apart from the codec that carries them out (<c>AvaloniaImageOptimizer</c>)
/// so they're testable without one: the size an image is fitted to, which encodes are worth making, and which result is kept.
/// </summary>
public static class ImageOptimization
{
    private const string JpegExtension = ".jpg";
    private const string PngExtension = ".png";

    /// <summary>
    /// The size <paramref name="width"/> by <paramref name="height"/> is fitted to inside <paramref name="options"/>' limits:
    /// scaled down to fit with its aspect kept, never up, and never below one pixel a side.
    /// </summary>
    public static (int Width, int Height) FitWithin(int width, int height, ImageOptimizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FitWithin(width, height, options.MaxWidth, options.MaxHeight);
    }

    /// <inheritdoc cref="FitWithin(int, int, ImageOptimizeOptions)"/>
    /// <param name="width">The image's width in pixels.</param>
    /// <param name="height">The image's height in pixels.</param>
    /// <param name="maxWidth">The width limit, or <see langword="null"/> for none.</param>
    /// <param name="maxHeight">The height limit, or <see langword="null"/> for none.</param>
    public static (int Width, int Height) FitWithin(int width, int height, int? maxWidth, int? maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        double scale = 1;
        if (maxWidth is { } w)
            scale = Math.Min(scale, (double)Math.Max(1, w) / width);
        if (maxHeight is { } h)
            scale = Math.Min(scale, (double)Math.Max(1, h) / height);
        if (scale >= 1)
            return (width, height);

        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>The quality the encoder is given: <paramref name="quality"/> held to 1 to 100.</summary>
    public static int ClampQuality(int quality) => Math.Clamp(quality, 1, 100);

    /// <summary>
    /// Which results are worth encoding, so the optimizer makes only those. An image with transparency can only stay PNG
    /// (JPEG has no alpha), and one that's already within its limits has no resized PNG to try, as re-encoding it as PNG
    /// would shrink it only by recompressing the same pixels.
    /// </summary>
    /// <param name="source">The source's format.</param>
    /// <param name="hasAlpha">Whether any pixel is not fully opaque.</param>
    /// <param name="resize">Whether the image is larger than its limits.</param>
    public static ImageCandidates CandidatesFor(ImageFormat source, bool hasAlpha, bool resize) => source switch
    {
        // A JPEG stays a JPEG.
        ImageFormat.Jpeg => new ImageCandidates(Jpeg: true, Png: false),
        _ when hasAlpha => new ImageCandidates(Jpeg: false, Png: resize),
        _ => new ImageCandidates(Jpeg: true, Png: resize),
    };

    /// <summary>
    /// Picks what to write: the smallest of the candidates and the original, where the original wins a tie. So a PNG that's
    /// already smaller than its JPEG is kept, and so is any image the encoder can't make smaller.
    /// </summary>
    /// <remarks>
    /// An image that's <paramref name="resize">to be resized</paramref> is the exception: the original breaks the size limits,
    /// so it isn't a candidate, and the smallest result that fits is chosen even when it's larger than the original. The
    /// planner then reports it as one that would grow, rather than the image passing as already optimal while it's too big.
    /// </remarks>
    /// <param name="source">The source's format.</param>
    /// <param name="sourceExtension">The source's extension, with the dot.</param>
    /// <param name="originalBytes">The source's size.</param>
    /// <param name="jpegBytes">The size of the JPEG result; <see langword="null"/> when it wasn't made.</param>
    /// <param name="pngBytes">The size of the resized PNG result; <see langword="null"/> when it wasn't made.</param>
    /// <param name="resize">Whether the image is larger than its limits, so the original can't stand.</param>
    public static ImageChoice Choose(ImageFormat source, string sourceExtension, long originalBytes, long? jpegBytes, long? pngBytes, bool resize = false)
    {
        ArgumentNullException.ThrowIfNull(sourceExtension);

        string keep = sourceExtension.ToLowerInvariant();
        ImageChoice? best = resize ? null : new ImageChoice(ImageOutput.KeepOriginal, originalBytes, keep);
        if (jpegBytes is { } jpeg && (best is null || jpeg < best.Bytes))
            best = new ImageChoice(ImageOutput.Jpeg, jpeg, JpegExtensionFor(sourceExtension));
        if (pngBytes is { } png && (best is null || png < best.Bytes))
            best = new ImageChoice(ImageOutput.Png, png, PngExtension);
        return best ?? new ImageChoice(ImageOutput.KeepOriginal, originalBytes, keep);
    }

    /// <summary>The extension a JPEG result is stored under: a <c>.jpg</c> or <c>.jpeg</c> source keeps its own, anything else becomes <c>.jpg</c>.</summary>
    public static string JpegExtensionFor(string sourceExtension)
    {
        ArgumentNullException.ThrowIfNull(sourceExtension);
        string extension = sourceExtension.ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" ? extension : JpegExtension;
    }

    /// <summary>The metadata keys whose value can name an image file; any other key, such as <c>name</c>, is text that may only look like one.</summary>
    private static readonly HashSet<string> ImageKeys = new(StringComparer.OrdinalIgnoreCase) { "cover", "background" };

    /// <summary>
    /// The metadata edits that carry renamed files into <c>song.ini</c> or a <c>.sng</c>'s metadata: an image key
    /// (<c>cover</c> or <c>background</c>) whose value is exactly the old stored name (ignoring case and surrounding space)
    /// is set to the new one. No other key is touched, whatever its value.
    /// </summary>
    /// <param name="pairs">The metadata as it is, with <paramref name="edits"/> applied if they're to take effect first.</param>
    /// <param name="renames">Old stored name to new, compared ignoring case.</param>
    /// <param name="edits">Where the edits are recorded.</param>
    /// <returns>Whether any value named a renamed file.</returns>
    public static bool UpdateReferences(IReadOnlyList<KeyValuePair<string, string>> pairs, IReadOnlyDictionary<string, string> renames, MetadataDraft edits)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentNullException.ThrowIfNull(edits);
        if (renames.Count == 0)
            return false;

        var byName = new Dictionary<string, string>(renames, StringComparer.OrdinalIgnoreCase);
        bool any = false;
        foreach (var group in pairs.Where(p => ImageKeys.Contains(p.Key)).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            // A draft sets a key whole, so a duplicated key is only edited when every one of its pairs names the same new file.
            var renamed = group.Select(p => byName.TryGetValue(p.Value.Trim(), out string? to) ? to : null).ToList();
            if (renamed[0] is { } target && renamed.All(r => r == target))
            {
                edits.Set(group.Key, target);
                any = true;
            }
        }

        return any;
    }
}
