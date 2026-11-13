using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.UI.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// One of a chart's pictures in its breakdown, as it is beside as optimizing would leave it: an image, or a video
    /// through a handful of its frames, which the user steps between.
    /// </summary>
    /// <remarks>A frame is made when it's first shown and kept until this is disposed, so stepping back is instant.</remarks>
    public sealed partial class OptimizeMediaPreviewViewModel : ObservableObject, IDisposable
    {
        /// <summary>How many frames of a video can be compared, spread evenly through it.</summary>
        public const int VideoFrames = 5;

        private readonly Func<int, CancellationToken, Task<MediaPreview>> _load;
        private readonly Dictionary<int, MediaPreview> _loaded = [];
        private readonly CancellationTokenSource _closing = new();
        private bool _disposed;

        /// <param name="title">The file's name.</param>
        /// <param name="isVideo">Whether it's a video, with frames to step between.</param>
        /// <param name="load">Makes the picture for a frame, 0 for an image; called off the UI thread's wait, at most once per frame.</param>
        public OptimizeMediaPreviewViewModel(string title, bool isVideo, Func<int, CancellationToken, Task<MediaPreview>> load)
        {
            Title = title;
            IsVideo = isVideo;
            FrameCount = isVideo ? VideoFrames : 1;
            _load = load;
        }

        public string Title { get; }

        public bool IsVideo { get; }

        public int FrameCount { get; }

        [ObservableProperty]
        public partial Bitmap? Before { get; set; }

        [ObservableProperty]
        public partial Bitmap? After { get; set; }

        [ObservableProperty]
        public partial string BeforeCaption { get; set; } = Strings.OptimizePreviewBefore;

        [ObservableProperty]
        public partial string AfterCaption { get; set; } = Strings.OptimizePreviewAfter;

        [ObservableProperty]
        public partial bool IsLoading { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string ErrorText { get; set; } = string.Empty;

        public bool HasError => ErrorText.Length > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FrameText))]
        [NotifyCanExecuteChangedFor(nameof(PreviousFrameCommand))]
        [NotifyCanExecuteChangedFor(nameof(NextFrameCommand))]
        public partial int FrameIndex { get; set; }

        /// <summary>Which frame is shown, and when in the video it is once that's known.</summary>
        public string FrameText => _loaded.TryGetValue(FrameIndex, out var shown) && shown.Time is { } time
            ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizePreviewFrameAtFormat, FrameIndex + 1, FrameCount, time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.CurrentCulture))
            : string.Format(CultureInfo.CurrentCulture, Strings.OptimizePreviewFrameFormat, FrameIndex + 1, FrameCount);

        /// <summary>Shows the first frame.</summary>
        public void Start() => _ = ShowAsync(0);

        private bool CanGoBack() => FrameIndex > 0;

        private bool CanGoOn() => FrameIndex < FrameCount - 1;

        [RelayCommand(CanExecute = nameof(CanGoBack))]
        private Task PreviousFrameAsync() => ShowAsync(FrameIndex - 1);

        [RelayCommand(CanExecute = nameof(CanGoOn))]
        private Task NextFrameAsync() => ShowAsync(FrameIndex + 1);

        private async Task ShowAsync(int index)
        {
            if (_disposed)
                return;

            FrameIndex = index;
            ErrorText = string.Empty;
            if (!_loaded.TryGetValue(index, out var preview))
            {
                IsLoading = true;
                try
                {
                    preview = await _load(index, _closing.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ObjectDisposedException)
                {
                    if (!_disposed && FrameIndex == index)
                    {
                        IsLoading = false;
                        ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.OptimizePreviewFailedFormat, ex.Message);
                    }

                    return;
                }

                // It was closed, or made twice by a quick step away and back, while this one was being made.
                if (_disposed || !_loaded.TryAdd(index, preview))
                {
                    preview.Dispose();
                    if (_disposed)
                        return;
                    preview = _loaded[index];
                }
            }

            // The user has stepped on since; that frame's own call shows it.
            if (FrameIndex != index)
                return;

            IsLoading = false;
            Before = preview.Before;
            After = preview.After;
            BeforeCaption = Caption(Strings.OptimizePreviewBefore, preview.Before, preview.BeforeBytes);
            AfterCaption = Caption(Strings.OptimizePreviewAfter, preview.After, preview.AfterBytes);
            OnPropertyChanged(nameof(FrameText));
        }

        // "Before: 1.5 MB, 1024×1024", or without the size for a video's frame.
        private static string Caption(string label, Bitmap bitmap, long? bytes) => bytes is { } size
            ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizePreviewImageFormat, label, ByteSizeFormatter.Format((ulong)Math.Max(0, size)), bitmap.PixelSize.Width, bitmap.PixelSize.Height)
            : string.Format(CultureInfo.CurrentCulture, Strings.OptimizePreviewFrameSizeFormat, label, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _closing.Cancel();
            Before = null;
            After = null;
            foreach (var preview in _loaded.Values)
                preview.Dispose();
            _loaded.Clear();
        }
    }
}
