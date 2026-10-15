using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChartPacker.Core.Metadata;
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
    /// <para>
    /// Rows are queued when the list realizes them. One that has scrolled away by the time its turn
    /// comes is skipped, so fast scrolling doesn't read everything it passes. A loaded thumbnail
    /// stays on its item until the next scan, so scrolling back never re-reads it. Every method is
    /// called on the UI thread.
    /// </para>
    /// <para>
    /// A thumbnail shows the song's unsaved album-art change, if it has one: the staged image, or
    /// none when the art is to be removed. <see cref="DraftChanged"/> reloads it when that changes.
    /// </para>
    /// </remarks>
    /// <param name="stagedArt">The unsaved album-art change for a song's path, if there is one.</param>
    public sealed class AlbumArtLoader(Func<string, AlbumArtChange?> stagedArt)
    {
        private const int Parallelism = 2;

        // A 36px tile at 2x scaling. Decoding small is what keeps a whole library's thumbnails cheap.
        private const int ThumbnailDecodeWidth = 72;

        private readonly List<Bitmap> _issued = [];
        private Channel<ArtRequest>? _queue;

        /// <summary>Starts a new list: drops the old queue and disposes every thumbnail handed out for the old items.</summary>
        /// <param name="generation">Cancelled when these items are replaced; results arriving after that are discarded.</param>
        public void Reset(CancellationToken generation)
        {
            _queue?.Writer.TryComplete();
            _queue = Channel.CreateUnbounded<ArtRequest>(new UnboundedChannelOptions { SingleWriter = true });

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
            if (item.ArtState == AlbumArtState.NotLoaded)
                Enqueue(item);
        }

        /// <summary>The list let go of <paramref name="item"/>'s row. Its thumbnail, if any, is kept.</summary>
        public static void Unrealized(LibraryItemViewModel item) => item.IsRealized = false;

        /// <summary>
        /// <paramref name="item"/>'s draft may have changed, or been saved or discarded. Its art is
        /// read again when the album-art change it shows is no longer the draft's.
        /// </summary>
        public void DraftChanged(LibraryItemViewModel item)
        {
            if (stagedArt(item.Entry.FullPath) != item.ArtChange)
                Reload(item);
        }

        /// <summary>
        /// Reads <paramref name="item"/>'s art again: now if its row is on screen, otherwise when it
        /// next is. The old thumbnail stays until the new one arrives, and a read already under way
        /// is discarded when it finishes.
        /// </summary>
        public void Reload(LibraryItemViewModel item)
        {
            item.ArtVersion++;
            if (item.ArtState == AlbumArtState.Loading)
                return;

            item.ArtState = AlbumArtState.NotLoaded;
            if (item.IsRealized)
                Enqueue(item);
        }

        /// <summary>Queues <paramref name="item"/> with the album-art change its draft has now.</summary>
        private void Enqueue(LibraryItemViewModel item)
        {
            if (_queue is not { } queue)
                return;

            item.ArtState = AlbumArtState.Loading;
            item.ArtChange = stagedArt(item.Entry.FullPath);
            queue.Writer.TryWrite(new ArtRequest(item, item.ArtChange, item.ArtVersion));
        }

        private async Task RunAsync(Channel<ArtRequest> queue, CancellationToken cancellationToken)
        {
            // Progress<T> posts back to the UI thread, where the items live.
            var done = new Progress<(ArtRequest Request, Bitmap? Art, bool Skipped)>(result =>
            {
                var (request, art, skipped) = result;
                var item = request.Item;
                if (cancellationToken.IsCancellationRequested)
                {
                    art?.Dispose();
                    return;
                }

                // Skipped, or read for a draft that has changed since: it's read again if it's
                // (still, or once more) on screen.
                if (skipped || request.Version != item.ArtVersion)
                {
                    art?.Dispose();
                    if (item.IsRealized)
                        Enqueue(item);
                    else
                        item.ArtState = AlbumArtState.NotLoaded;
                    return;
                }

                if (art is null)
                {
                    // A reload finds the art was removed. The old thumbnail stays in _issued until the next Reset.
                    item.AlbumArt = null;
                    item.ArtState = AlbumArtState.None;
                    return;
                }

                _issued.Add(art);
                item.AlbumArt = art;
                item.ArtState = AlbumArtState.Loaded;
            });
            IProgress<(ArtRequest, Bitmap?, bool)> report = done;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken };

            try
            {
                await Parallel.ForEachAsync(queue.Reader.ReadAllAsync(cancellationToken), options, async (request, token) =>
                {
                    if (!request.Item.IsRealized)
                    {
                        report.Report((request, null, true));
                        return;
                    }

                    report.Report((request, await LoadAsync(request, token).ConfigureAwait(false), false));
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// The art the request's song will have once its draft is saved, decoded at thumbnail size,
        /// or <see langword="null"/> when it has none that can be read.
        /// </summary>
        private static async Task<Bitmap?> LoadAsync(ArtRequest request, CancellationToken cancellationToken)
        {
            byte[]? bytes;
            try
            {
                bytes = request.Change switch
                {
                    null => await AlbumArt.ReadAsync(request.Item.Entry, cancellationToken: cancellationToken).ConfigureAwait(false),
                    { SourcePath: { } source } when new FileInfo(source).Length <= AlbumArt.DefaultMaxBytes =>
                        await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false),
                    _ => null,
                };
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

        /// <summary>An item to read, with the album-art change and art version it was queued with.</summary>
        private sealed record ArtRequest(LibraryItemViewModel Item, AlbumArtChange? Change, int Version);
    }
}
