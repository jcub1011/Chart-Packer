using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChartPacker.Core.Scanning;

namespace ChartPacker.ViewModels
{
    /// <summary>Where an item's album art is up to.</summary>
    public enum AlbumArtState
    {
        /// <summary>Not read yet, or skipped because the row scrolled away before its turn.</summary>
        NotLoaded,

        /// <summary>Queued or being read.</summary>
        Loading,

        /// <summary>The thumbnail is on the item.</summary>
        Loaded,

        /// <summary>The song has no usable art. It isn't read again.</summary>
        None,
    }

    /// <summary>
    /// Loads album-art thumbnails for the rows the library list has on screen, a couple at a time.
    /// </summary>
    /// <remarks>
    /// Rows are queued when the list realizes them. One that has scrolled away by the time its turn
    /// comes is skipped, so fast scrolling doesn't read everything it passes. A loaded thumbnail
    /// stays on its item until the next scan, so scrolling back never re-reads it. Every method is
    /// called on the UI thread.
    /// </remarks>
    public sealed class AlbumArtLoader
    {
        private const int Parallelism = 2;

        // A 36px tile at 2x scaling. Decoding small is what keeps a whole library's thumbnails cheap.
        private const int ThumbnailDecodeWidth = 72;

        private readonly List<Bitmap> _issued = [];
        private Channel<LibraryItemViewModel>? _queue;

        /// <summary>Starts a new list: drops the old queue and disposes every thumbnail handed out for the old items.</summary>
        /// <param name="generation">Cancelled when these items are replaced; results arriving after that are discarded.</param>
        public void Reset(CancellationToken generation)
        {
            _queue?.Writer.TryComplete();
            _queue = Channel.CreateUnbounded<LibraryItemViewModel>(new UnboundedChannelOptions { SingleWriter = true });

            // Disposed after the cleared list has been laid out and rendered without them.
            var old = _issued.ToArray();
            _issued.Clear();
            if (old.Length > 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var bitmap in old)
                        bitmap.Dispose();
                }, DispatcherPriority.Background);
            }

            _ = RunAsync(_queue, generation);
        }

        /// <summary>The list created a row for <paramref name="item"/>.</summary>
        public void Realized(LibraryItemViewModel item)
        {
            item.IsRealized = true;
            if (item.ArtState == AlbumArtState.NotLoaded && _queue is { } queue)
            {
                item.ArtState = AlbumArtState.Loading;
                queue.Writer.TryWrite(item);
            }
        }

        /// <summary>The list let go of <paramref name="item"/>'s row. Its thumbnail, if any, is kept.</summary>
        public void Unrealized(LibraryItemViewModel item) => item.IsRealized = false;

        private async Task RunAsync(Channel<LibraryItemViewModel> queue, CancellationToken cancellationToken)
        {
            // Progress<T> posts back to the UI thread, where the items live.
            var done = new Progress<(LibraryItemViewModel Item, Bitmap? Art, bool Skipped)>(result =>
            {
                var (item, art, skipped) = result;
                if (cancellationToken.IsCancellationRequested)
                {
                    art?.Dispose();
                    return;
                }

                if (skipped)
                {
                    // It may have scrolled back into view while the skip was on its way.
                    if (item.IsRealized)
                        queue.Writer.TryWrite(item);
                    else
                        item.ArtState = AlbumArtState.NotLoaded;
                    return;
                }

                if (art is null)
                {
                    item.ArtState = AlbumArtState.None;
                    return;
                }

                _issued.Add(art);
                item.AlbumArt = art;
                item.ArtState = AlbumArtState.Loaded;
            });
            IProgress<(LibraryItemViewModel, Bitmap?, bool)> report = done;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken };

            try
            {
                await Parallel.ForEachAsync(queue.Reader.ReadAllAsync(cancellationToken), options, async (item, token) =>
                {
                    if (!item.IsRealized)
                    {
                        report.Report((item, null, true));
                        return;
                    }

                    report.Report((item, await LoadAsync(item.Entry, token).ConfigureAwait(false), false));
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>The entry's art decoded at thumbnail size, or <see langword="null"/> when it has none that can be read.</summary>
        private static async Task<Bitmap?> LoadAsync(SongEntry entry, CancellationToken cancellationToken)
        {
            byte[]? bytes;
            try
            {
                bytes = await AlbumArt.ReadAsync(entry, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (bytes is null)
                return null;

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return Bitmap.DecodeToWidth(stream, ThumbnailDecodeWidth, BitmapInterpolationMode.MediumQuality);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Corrupt or unsupported images surface as assorted decoder exceptions; any of them
                // just means there's no art to show.
                return null;
            }
        }
    }
}
