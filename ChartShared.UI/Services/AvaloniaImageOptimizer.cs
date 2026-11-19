using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ChartShared.Core.Optimization;

namespace ChartShared.UI.Services;

/// <summary>
/// The Library Optimization Tool's image optimizer: fits album art or a background image inside the strategy's size, and
/// writes it as JPEG, or keeps it as it is, with Avalonia's own Skia <see cref="Bitmap"/>. No package is added.
/// </summary>
/// <remarks>
/// <para><b>What it does.</b> The size and which result is kept are <see cref="ImageOptimization"/>'s: the image is scaled
/// down (never up, aspect kept) to fit, then written as JPEG at <see cref="ImageOptimizeOptions.Quality"/>. A PNG with
/// transparency stays PNG, as JPEG has none; a PNG that's already smaller than its JPEG is kept as it is. A <c>.png</c>
/// that becomes JPEG is renamed <c>.jpg</c>, which <see cref="OptimizationPlanner"/> carries into the chart.</para>
/// <para><b>Estimates.</b> Images are quick to encode, so the estimate is the real encode, in memory: it's exact, and is
/// the size <see cref="OptimizeAsync"/> writes. A source is decoded at the reduced size where it's larger than the
/// limits (<see cref="Bitmap.DecodeToWidth"/>), so a big image is never decoded at its full size, and is scaled the last
/// pixel if the decoder's rounding differs from <see cref="ImageOptimization.FitWithin(int, int, ImageOptimizeOptions)"/>'s.</para>
/// <para><b>Reuse.</b> What an estimate encodes is kept, briefly and within a size budget, by the source's contents and the
/// options, so applying the plan writes those bytes rather than encoding every image a second time. A source that's
/// changed since is a different key, and is encoded afresh.</para>
/// <para><b>Needs Skia.</b> <see cref="IsAvailable"/> is false until the app's Skia platform is initialised, which it is in
/// Chart Packer and Chart Builder and isn't in the test projects; they use a fake of <see cref="IImageOptimizer"/>.</para>
/// <para>EXIF orientation, colour profiles and metadata aren't carried into the result. An image whose pixels Skia can't
/// decode (some CMYK JPEGs) fails with <see cref="InvalidDataException"/> and is left as it is.</para>
/// </remarks>
public sealed class AvaloniaImageOptimizer : IImageOptimizer
{
    /// <summary>The most encoded bytes kept for <see cref="OptimizeAsync"/>; the oldest are dropped past it.</summary>
    private const long CacheBudgetBytes = 64L * 1024 * 1024;

    private readonly EncodeCache _cache = new(CacheBudgetBytes);

    /// <inheritdoc />
    /// <remarks>The app's platform, Skia included, is set up before its <see cref="Application"/> is created, so there being one means bitmaps decode.</remarks>
    public bool IsAvailable => Application.Current is not null;

    /// <inheritdoc />
    public Task<OptimizeEstimate> EstimateAsync(AssetFile source, ImageOptimizeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => EncodeAndKeep(source, options, cancellationToken).Choice.ToEstimate(), cancellationToken);
    }

    /// <inheritdoc />
    public Task OptimizeAsync(AssetFile source, string destinationPath, ImageOptimizeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Optimize(source, destinationPath, options, cancellationToken), cancellationToken);
    }

    /// <summary>Encodes <paramref name="source"/> and keeps the result for the <see cref="OptimizeAsync"/> that follows.</summary>
    private Encoded EncodeAndKeep(AssetFile source, ImageOptimizeOptions options, CancellationToken cancellationToken)
    {
        string key = CacheKey(source.Path, options);
        var encoded = Encode(source, options, cancellationToken);
        _cache.Add(key, encoded);
        return encoded;
    }

    private void Optimize(AssetFile source, string destinationPath, ImageOptimizeOptions options, CancellationToken cancellationToken)
    {
        // The estimate's encode is used when there is one for these very bytes and options.
        string key = CacheKey(source.Path, options);
        var encoded = _cache.Take(key) ?? Encode(source, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (encoded.Bytes is { } bytes)
                File.WriteAllBytes(destinationPath, bytes);
            else
                File.Copy(source.Path, destinationPath, overwrite: true);
        }
        catch
        {
            // Nothing partial is left for the caller to find.
            TryDelete(destinationPath);
            throw;
        }
    }

    /// <summary>What encoding made: the choice, and the bytes to write for it (none for a kept original).</summary>
    private sealed record Encoded(ImageChoice Choice, byte[]? Bytes);

    /// <summary>
    /// The key an encode is kept under: a hash of the file's contents and the options that shape the result. Not the path or
    /// timestamp, as a <c>.sng</c>'s image is extracted to a different path for each run and a changed file must not match.
    /// </summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    private static string CacheKey(string path, ImageOptimizeOptions options)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        string hash = Convert.ToHexString(SHA256.HashData(input));
        return $"{hash}|{input.Length}|{options.MaxWidth}|{options.MaxHeight}|{ImageOptimization.ClampQuality(options.Quality)}";
    }

    /// <summary>Encodes held for <see cref="OptimizeAsync"/>, oldest dropped first once their bytes pass the budget.</summary>
    private sealed class EncodeCache(long budgetBytes)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, Encoded> _items = [];
        private readonly Queue<string> _order = new();
        private long _bytes;

        public void Add(string key, Encoded encoded)
        {
            lock (_gate)
            {
                if (_items.TryGetValue(key, out var old))
                    _bytes -= old.Bytes?.Length ?? 0;
                else
                    _order.Enqueue(key);

                _items[key] = encoded;
                _bytes += encoded.Bytes?.Length ?? 0;
                while (_bytes > budgetBytes && _order.TryDequeue(out string? oldest))
                {
                    if (_items.Remove(oldest, out var dropped))
                        _bytes -= dropped.Bytes?.Length ?? 0;
                }
            }
        }

        /// <summary>The encode for <paramref name="key"/>, removed as it's taken: an image is written once.</summary>
        public Encoded? Take(string key)
        {
            lock (_gate)
            {
                if (!_items.Remove(key, out var encoded))
                    return null;

                _bytes -= encoded.Bytes?.Length ?? 0;
                return encoded;
            }
        }
    }

    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="InvalidDataException">The file isn't a PNG or JPEG, or can't be decoded.</exception>
    private static Encoded Encode(AssetFile source, ImageOptimizeOptions options, CancellationToken cancellationToken)
    {
        string name = source.Asset.StoredName;
        using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        long originalBytes = input.Length;

        var header = ImageHeader.Read(input) ?? throw new InvalidDataException($"'{name}' isn't a PNG or JPEG image with a size that can be read.");
        var (width, height) = ImageOptimization.FitWithin(header.Width, header.Height, options);
        bool resize = width != header.Width || height != header.Height;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // A PNG's chunks show whether it can be transparent at all; only one that can has its pixels scanned.
            input.Position = 0;
            bool canBeTransparent = header.Format == ImageFormat.Png && ImageHeader.PngCanBeTransparent(input);

            input.Position = 0;
            using var bitmap = Decode(input, header, options, width, height, resize);
            cancellationToken.ThrowIfCancellationRequested();

            bool hasAlpha = canBeTransparent && HasAlpha(bitmap);
            var wanted = ImageOptimization.CandidatesFor(header.Format, hasAlpha, resize);
            byte[]? jpeg = wanted.Jpeg ? Save(bitmap, new JpegBitmapEncoderOptions { Quality = ImageOptimization.ClampQuality(options.Quality) }) : null;
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? png = wanted.Png ? Save(bitmap, new PngBitmapEncoderOptions { CompressionLevel = System.IO.Compression.CompressionLevel.Optimal }) : null;

            var choice = ImageOptimization.Choose(header.Format, source.Asset.Extension, originalBytes, jpeg?.Length, png?.Length, resize);
            return new Encoded(choice, choice.Output switch
            {
                ImageOutput.Jpeg => jpeg,
                ImageOutput.Png => png,
                _ => null,
            });
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or InvalidDataException))
        {
            // Skia reports an image it can't decode in more than one way.
            throw new InvalidDataException($"'{name}' can't be decoded: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Decodes the image, at the reduced size when it's to be resized, so a large one isn't decoded in full. The result is
    /// exactly <paramref name="width"/> by <paramref name="height"/>: the decoder derives the other side from the aspect and
    /// can round it a pixel differently, in which case the (already reduced) bitmap is scaled to the size asked for.
    /// </summary>
    private static Bitmap Decode(Stream input, ImageHeader header, ImageOptimizeOptions options, int width, int height, bool resize)
    {
        if (!resize)
            return new Bitmap(input);

        // Whichever limit binds is the one the decoder is given; the other side follows from the aspect.
        double byWidth = options.MaxWidth is { } w ? (double)w / header.Width : double.PositiveInfinity;
        double byHeight = options.MaxHeight is { } h ? (double)h / header.Height : double.PositiveInfinity;
        var decoded = byWidth <= byHeight
            ? Bitmap.DecodeToWidth(input, width, BitmapInterpolationMode.HighQuality)
            : Bitmap.DecodeToHeight(input, height, BitmapInterpolationMode.HighQuality);

        var target = new PixelSize(width, height);
        if (decoded.PixelSize == target)
            return decoded;

        using (decoded)
            return decoded.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>
    /// Whether any pixel is not fully opaque; for a PNG whose chunks say it can be transparent
    /// (<see cref="ImageHeader.PngCanBeTransparent"/>). The pixels are copied to a <see cref="PixelFormat.Bgra8888"/> bitmap,
    /// so the alpha is the fourth byte of each pixel whatever format the decoder gave. It's of the reduced bitmap, so at most
    /// the size of the limits.
    /// </summary>
    private static bool HasAlpha(Bitmap bitmap)
    {
        if (bitmap.AlphaFormat == AlphaFormat.Opaque)
            return false;

        using var target = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var frame = target.Lock();
        bitmap.CopyPixels(frame);

        var row = new byte[frame.RowBytes];
        for (int y = 0; y < frame.Size.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(frame.Address + (y * frame.RowBytes), row, 0, row.Length);
            for (int x = 3; x < frame.Size.Width * 4; x += 4)
            {
                if (row[x] != 0xFF)
                    return true;
            }
        }

        return false;
    }

    private static byte[] Save(Bitmap bitmap, BitmapEncoderOptions options)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, options);
        return stream.ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
