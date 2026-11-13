using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.Video;

namespace ChartPacker.Services
{
    /// <summary>A picture as it is and as optimizing would leave it, to compare by eye.</summary>
    /// <param name="Before">The picture as it is.</param>
    /// <param name="After">The picture as it would be.</param>
    /// <param name="BeforeBytes">The file's size as it is; null for a video's frame, which has none of its own.</param>
    /// <param name="AfterBytes">The file's size as it would be; null for a video's frame.</param>
    /// <param name="Time">When a video's frame is shown; null for an image.</param>
    public sealed record MediaPreview(Bitmap Before, Bitmap After, long? BeforeBytes = null, long? AfterBytes = null, TimeSpan? Time = null) : IDisposable
    {
        public void Dispose()
        {
            Before.Dispose();
            After.Dispose();
        }
    }

    /// <summary>Makes before-and-after pictures of a chart's images and video, without changing the chart.</summary>
    public interface IMediaPreviewer
    {
        /// <summary>Starts previewing <paramref name="entry"/>'s files. Disposing what it gives deletes whatever it extracted.</summary>
        IMediaPreviewSession Open(SongEntry entry);
    }

    /// <summary>Before-and-after pictures of one chart's files.</summary>
    public interface IMediaPreviewSession : IDisposable
    {
        /// <summary>The image <paramref name="asset"/> as it is, and recompressed with <paramref name="options"/>.</summary>
        /// <exception cref="IOException">The file can't be read.</exception>
        /// <exception cref="InvalidDataException">The image can't be decoded.</exception>
        Task<MediaPreview> ImageAsync(ChartAsset asset, ImageOptimizeOptions options, CancellationToken cancellationToken);

        /// <summary>A frame of the video <paramref name="asset"/> as it is, and re-encoded with <paramref name="options"/>.</summary>
        /// <param name="position">Where in the video, 0 at its start to 1 at its end.</param>
        /// <exception cref="IOException">The file can't be read.</exception>
        /// <exception cref="InvalidDataException">The video can't be decoded.</exception>
        Task<MediaPreview> VideoFrameAsync(ChartAsset asset, VideoOptimizeOptions options, double position, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Previews made with the optimizers themselves: an image is recompressed to a temp file, and a video has a short run
    /// of frames re-encoded (<see cref="VideoReencoder.PreviewFrame"/>), so what's shown is what a run would write.
    /// </summary>
    /// <param name="images">Recompresses images.</param>
    public sealed class MediaPreviewer(IImageOptimizer images) : IMediaPreviewer
    {
        // A frame is shown at about half the width of the list, so there's no call to decode a 4K frame in full.
        private const int MaxFrameWidth = 1280;

        public IMediaPreviewSession Open(SongEntry entry) => new Session(entry, images);

        private sealed class Session(SongEntry entry, IImageOptimizer images) : IMediaPreviewSession
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            private readonly Dictionary<string, string> _extracted = new(StringComparer.OrdinalIgnoreCase);
            private string? _folder;
            private bool _disposed;

            public async Task<MediaPreview> ImageAsync(ChartAsset asset, ImageOptimizeOptions options, CancellationToken cancellationToken)
            {
                string source = await PathOfAsync(asset, cancellationToken).ConfigureAwait(false);
                string output = Path.Combine(Folder(), "after-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await images.OptimizeAsync(new AssetFile(asset, source), output, options, cancellationToken).ConfigureAwait(false);
                    return await Task.Run(
                        () =>
                        {
                            var before = new Bitmap(source);
                            try
                            {
                                return new MediaPreview(before, new Bitmap(output), asset.Length, new FileInfo(output).Length);
                            }
                            catch
                            {
                                before.Dispose();
                                throw;
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(output);
                }
            }

            public async Task<MediaPreview> VideoFrameAsync(ChartAsset asset, VideoOptimizeOptions options, double position, CancellationToken cancellationToken)
            {
                string source = await PathOfAsync(asset, cancellationToken).ConfigureAwait(false);
                // A few threads: it's one short encode, made while the window may be measuring too.
                var settings = VideoReencodeSettings.From(options) with { Threads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4) };
                return await Task.Run(
                    () =>
                    {
                        var frame = VideoReencoder.PreviewFrame(source, settings, position, MaxFrameWidth, cancellationToken);
                        var before = ToBitmap(frame.Before, frame.Width, frame.Height);
                        try
                        {
                            return new MediaPreview(before, ToBitmap(frame.After, frame.AfterWidth, frame.AfterHeight), Time: frame.Time);
                        }
                        catch
                        {
                            before.Dispose();
                            throw;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            public void Dispose()
            {
                _disposed = true;
                if (_folder is not null)
                {
                    try
                    {
                        Directory.Delete(_folder, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A preview still being made holds a file; the temp folder is the system's to clear.
                    }
                }
            }

            // Where the file can be read: a folder's own, or a .sng's extracted once to the session's temp folder.
            private async Task<string> PathOfAsync(ChartAsset asset, CancellationToken cancellationToken)
            {
                if (entry.Kind != SongEntryKind.SngFile)
                    return Path.Combine(entry.FullPath, asset.StoredName);

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_extracted.TryGetValue(asset.StoredName, out string? path))
                        return path;

                    path = Path.Combine(Folder(), "src-" + asset.StoredName);
                    await Task.Run(
                        () =>
                        {
                            using var reader = SongFileReader.Open(entry, cancellationToken);
                            using var stream = reader.OpenRead(asset.StoredName)
                                ?? throw new FileNotFoundException($"'{asset.StoredName}' is no longer in the chart.", asset.StoredName);
                            using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
                            stream.CopyTo(target);
                        },
                        cancellationToken).ConfigureAwait(false);
                    _extracted[asset.StoredName] = path;
                    return path;
                }
                finally
                {
                    _gate.Release();
                }
            }

            private string Folder() =>
                _folder ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "chartpacker-preview-" + Guid.NewGuid().ToString("N"))).FullName;

            private static Bitmap ToBitmap(byte[] bgra, int width, int height)
            {
                var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using var locked = bitmap.Lock();
                int row = width * 4;
                // A row at a time, as the bitmap's rows may be padded.
                for (int y = 0; y < height; y++)
                    Marshal.Copy(bgra, y * row, locked.Address + (y * locked.RowBytes), row);
                return bitmap;
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
    }
}
