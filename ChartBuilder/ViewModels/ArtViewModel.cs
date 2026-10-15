using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Ffmpeg;
using ChartPacker.Resources;
using ChartPacker.UI.Formatting;
using ChartPacker.UI.Services;
using ChartPacker.UI.ViewModels;
using ChartPacker.Video;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Art tab (charter-spec C1): the song's album art, background image and background video, each added, replaced
    /// or removed as an undo step (<see cref="MediaCommands"/>), a pending change until the song is saved. The video plays
    /// against the song, while the tab is shown.
    /// </summary>
    public sealed partial class ArtViewModel : ViewModelBase, IDisposable
    {
        private readonly EditorSong _song;
        private bool _isShown;

        /// <param name="song">The song open.</param>
        /// <param name="imagePicker">Picks the album art.</param>
        /// <param name="filePicker">Picks a background.</param>
        /// <param name="execute">Makes an edit, as one undo step.</param>
        /// <param name="clock">Where the song is playing, which the video follows.</param>
        public ArtViewModel(EditorSong song, IImagePickerService imagePicker, IFilePickerService filePicker, Action<IChartCommand> execute, Func<TimeSpan> clock)
        {
            ArgumentNullException.ThrowIfNull(song);
            _song = song;
            AlbumArt = new ArtSlotViewModel(this, MediaSlot.AlbumArt, Strings.ArtAlbumArtTitle, () => imagePicker.PickImageAsync(Strings.ArtPickAlbumArtTitle), execute);
            BackgroundImage = new ArtSlotViewModel(this, MediaSlot.BackgroundImage, Strings.ArtBackgroundImageTitle, () => filePicker.PickBackgroundAsync(Strings.ArtPickBackgroundImageTitle), execute);
            BackgroundVideo = new ArtSlotViewModel(this, MediaSlot.BackgroundVideo, Strings.ArtBackgroundVideoTitle, () => filePicker.PickBackgroundAsync(Strings.ArtPickBackgroundVideoTitle), execute);
            VideoStart = new VideoStartViewModel(song, execute);
            VideoStart.PreviewChanged += OnVideoStartPreviewChanged;
            Clock = clock;
            _song.Changed += OnSongChanged;
            Refresh();
        }

        public ArtSlotViewModel AlbumArt { get; }

        public ArtSlotViewModel BackgroundImage { get; }

        public ArtSlotViewModel BackgroundVideo { get; }

        public IReadOnlyList<ArtSlotViewModel> Slots => [AlbumArt, BackgroundImage, BackgroundVideo];

        /// <summary>The box under the background video that sets where in it the song starts.</summary>
        public VideoStartViewModel VideoStart { get; }

        /// <summary>Where the song is playing, which the video follows.</summary>
        public Func<TimeSpan> Clock { get; }

        /// <summary>
        /// How the video lines up with the song: its <c>video_start_time</c> and the like, as the next save leaves them, but
        /// the start while it's being typed, so the video shows it at once.
        /// </summary>
        [ObservableProperty]
        public partial VideoTimeline Timeline { get; private set; } = VideoTimeline.Default;

        /// <summary>Whether the art can be changed now: not while the song is being saved.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        public partial bool IsSaving { get; set; }

        public bool CanEdit => !IsSaving;

        /// <summary>Whether the tab is shown, which the video plays only while it is.</summary>
        public bool IsShown
        {
            get => _isShown;
            set
            {
                if (_isShown == value)
                    return;
                _isShown = value;
                BackgroundVideo.Refresh();
            }
        }

        internal EditorSong Song => _song;

        /// <summary>Stops the video and lets go of its file, as a save that replaces it needs; it plays again once shown.</summary>
        public void Release() => BackgroundVideo.StopVideo();

        /// <summary>Shows the art again, as the song has it now, once a save has let go of it.</summary>
        public void Refresh()
        {
            VideoStart.Load();
            UpdateTimeline();
            foreach (var slot in Slots)
                slot.Refresh();
        }

        public void Dispose()
        {
            _song.Changed -= OnSongChanged;
            VideoStart.PreviewChanged -= OnVideoStartPreviewChanged;
            VideoStart.Dispose();
            foreach (var slot in Slots)
                slot.Dispose();
        }

        partial void OnIsSavingChanged(bool value)
        {
            VideoStart.IsSaving = value;
            foreach (var slot in Slots)
                slot.RefreshCommands();
        }

        partial void OnTimelineChanged(VideoTimeline value) => BackgroundVideo.NotifyTimeline();

        private void UpdateTimeline()
        {
            var timeline = VideoTimeline.From(_song.EffectivePairs());
            Timeline = VideoStart.PreviewStart is { } start ? timeline with { Start = start } : timeline;
        }

        private void OnVideoStartPreviewChanged(object? sender, EventArgs e) => UpdateTimeline();

        private void OnSongChanged(object? sender, EventArgs e) => Refresh();
    }

    /// <summary>One tile of the Art tab: what the slot holds as the next save leaves it, and its Choose, Replace, Remove and Revert.</summary>
    public sealed partial class ArtSlotViewModel : ViewModelBase, IDisposable
    {
        // The tile is up to the tab's width; twice a wide panel covers 2x scaling.
        private const int DecodeWidth = 1024;
        private const int VideoDecodeWidth = 960;

        private readonly ArtViewModel _owner;
        private readonly Func<Task<string?>> _pick;
        private readonly Action<IChartCommand> _execute;
        private CancellationTokenSource? _loadCts;
        private (MediaRef Ref, OpenedSong? Opened)? _shown;

        internal ArtSlotViewModel(ArtViewModel owner, MediaSlot slot, string title, Func<Task<string?>> pick, Action<IChartCommand> execute)
        {
            _owner = owner;
            Slot = slot;
            Title = title;
            _pick = pick;
            _execute = execute;
        }

        public MediaSlot Slot { get; }

        public string Title { get; }

        public bool IsVideoSlot => Slot.Kind == MediaKind.BackgroundVideo;

        /// <summary>The image shown, for the album art and the background image; null when there's none to show.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasImage))]
        public partial Bitmap? Image { get; private set; }

        /// <summary>The video playing, for the background video, while the tab is shown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideo))]
        public partial VideoPlayback? Video { get; private set; }

        /// <summary>The file's stored name, e.g. <c>background.png</c>, when the slot will hold one.</summary>
        [ObservableProperty]
        public partial string? FileName { get; private set; }

        /// <summary>The file's size, formatted, once it's known.</summary>
        [ObservableProperty]
        public partial string? FileSize { get; private set; }

        /// <summary>Shown in the tile in place of an image or video: there's none, it's removed at the next save, or it can't be shown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNote))]
        public partial string? Note { get; private set; }

        /// <summary>Why the file chosen can't be used, or the song's can't be read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? Error { get; private set; }

        /// <summary>Whether the slot has a pending change.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Marker))]
        public partial bool IsChanged { get; private set; }

        /// <summary>Whether the slot will hold a file once saved.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        [NotifyPropertyChangedFor(nameof(ShowsVideoStart))]
        public partial bool HasFile { get; private set; }

        public bool IsEmpty => !HasFile;

        /// <summary>The video start box, for the background video; null for the others.</summary>
        public VideoStartViewModel? VideoStart => IsVideoSlot ? _owner.VideoStart : null;

        /// <summary>Whether the video start box shows: under the background video, while there'll be one.</summary>
        public bool ShowsVideoStart => IsVideoSlot && HasFile;

        public bool HasImage => Image is not null;

        public bool HasVideo => Video is not null;

        public bool HasNote => Note is not null;

        public bool HasError => Error is not null;

        /// <summary><c>*</c> beside the name while the slot has a pending change, as an edited row has.</summary>
        public string? Marker => IsChanged ? "*" : null;

        /// <summary>How tall the tile is: a square's height for the art, a 16:9 frame's for a background at the panel's usual width.</summary>
        public double TileHeight => Slot.Kind == MediaKind.AlbumArt ? 200 : 152;

        public VideoTimeline Timeline => _owner.Timeline;

        public Func<TimeSpan> Clock => _owner.Clock;

        public void Dispose()
        {
            _loadCts?.Cancel();
            StopVideo();
            SetImage(null);
        }

        /// <summary>Asks for a file and, once it's checked, puts it in the slot (Choose, or Replace).</summary>
        [RelayCommand(CanExecute = nameof(CanEdit))]
        private async Task ChooseAsync()
        {
            string? path = await _pick();
            if (path is null || !_owner.CanEdit)
                return;

            Error = null;
            if (await CheckAsync(path) is { } problem)
            {
                Error = problem;
                return;
            }

            if (_owner.CanEdit && MediaCommands.Set(_owner.Song, Slot, path) is { } command)
                _execute(command);
        }

        /// <summary>Empties the slot at the next save.</summary>
        [RelayCommand(CanExecute = nameof(CanRemove))]
        private void Remove()
        {
            Error = null;
            if (MediaCommands.Remove(_owner.Song, Slot) is { } command)
                _execute(command);
        }

        /// <summary>Puts the slot back as the song has it saved.</summary>
        [RelayCommand(CanExecute = nameof(CanRevert))]
        private void Revert()
        {
            Error = null;
            if (MediaCommands.RevertToSaved(_owner.Song, Slot) is { } command)
                _execute(command);
        }

        private bool CanEdit() => _owner.CanEdit;

        private bool CanRemove() => _owner.CanEdit && HasFile;

        private bool CanRevert() => _owner.CanEdit && IsChanged;

        internal void NotifyTimeline() => OnPropertyChanged(nameof(Timeline));

        internal void RefreshCommands()
        {
            ChooseCommand.NotifyCanExecuteChanged();
            RemoveCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Shows what the slot holds now, read again only when that's changed; the video plays while the tab is shown.</summary>
        internal void Refresh()
        {
            var song = _owner.Song;
            var effective = song.EffectiveMedia(Slot);
            IsChanged = song.Media.ContainsKey(Slot);
            HasFile = effective is not MediaRef.NoneRef;
            OnPropertyChanged(nameof(Timeline));
            RefreshCommands();

            bool same = _shown is { } shown && shown.Ref == effective && ReferenceEquals(shown.Opened, song.Opened);
            if (IsVideoSlot && (!same || (_owner.IsShown != (Video is not null) && HasFile)))
                ShowVideo(effective);
            else if (!IsVideoSlot && !same)
                _ = ShowImageAsync(effective);
            _shown = (effective, song.Opened);
        }

        internal void StopVideo()
        {
            if (Video is { } playback)
            {
                Video = null;
                playback.Dispose();
            }

            _shown = null;
        }

        private void ShowVideo(MediaRef effective)
        {
            StopVideo();
            var song = _owner.Song;
            (Func<Stream>? open, string? name, string? sizeKey) = effective switch
            {
                MediaRef.SourceRef source => (() => new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024), Slot.StoredNameFor(source.Path), source.Path),
                MediaRef.StoredRef when song.Entry is { } entry && StoredName() is { } stored =>
                    ((Func<Stream>)(() => SongFile.OpenRead(entry, stored) ?? throw new FileNotFoundException("The video isn't in the song any more.", stored)), stored, stored),
                _ => (null, null, null),
            };

            FileName = name;
            Note = open is null ? NoteFor(effective) : null;
            _ = ShowSizeAsync(sizeKey);
            if (open is null || !_owner.IsShown)
                return;

            if (!FfmpegLibrary.IsAvailable)
            {
                Note = Strings.ArtVideoNeedsFfmpeg;
                return;
            }

            Video = new VideoPlayback(open, VideoDecodeWidth, name);
        }

        private async Task ShowImageAsync(MediaRef effective)
        {
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            var song = _owner.Song;
            string? name = null;
            string? sizeKey = null;
            Bitmap? bitmap = null;
            string? note = null;
            string? error = null;
            try
            {
                switch (effective)
                {
                    case MediaRef.SourceRef source:
                        name = Slot.StoredNameFor(source.Path);
                        sizeKey = source.Path;
                        (bitmap, error) = await Task.Run(() => LoadImage(source.Path), cts.Token);
                        break;
                    case MediaRef.StoredRef when song.Entry is { } entry && StoredName() is { } stored:
                        name = stored;
                        sizeKey = stored;
                        byte[]? bytes = await SongFile.ReadAsync(entry, stored, ChartPacker.Core.Scanning.AlbumArt.DefaultMaxBytes, cts.Token);
                        bitmap = bytes is null ? null : await Task.Run(() => Decode(bytes), cts.Token);
                        break;
                    default:
                        note = NoteFor(effective);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error = string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtUnreadableFormat, ex.Message);
            }

            if (cts.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return;
            }

            SetImage(bitmap);
            FileName = name;
            Note = bitmap is null ? note ?? Strings.ArtUnreadable : null;
            if (error is not null)
                Error = error;
            _ = ShowSizeAsync(sizeKey);
        }

        private async Task ShowSizeAsync(string? key)
        {
            FileSize = null;
            if (key is null)
                return;
            var opened = _owner.Song.Opened;
            var sizes = await Task.Run(() => SongFileSizes.Of(opened, Path.IsPathRooted(key) ? [key] : [], CancellationToken.None));
            if (FileName is not null && sizes.TryGetValue(key, out long size))
                FileSize = ByteSizeFormatter.Format((ulong)size);
        }

        private void SetImage(Bitmap? bitmap)
        {
            var old = Image;
            Image = bitmap;
            if (!ReferenceEquals(old, bitmap))
                old?.Dispose();
        }

        // The stored file the games would pick for the slot.
        private string? StoredName()
        {
            var files = _owner.Song.StoredFilesOf(Slot);
            return Slot.Kind == MediaKind.AlbumArt ? ChartPacker.Core.Scanning.AlbumArt.Pick(files, n => n) : SongBackgrounds.Pick(files, n => n, Slot.BackgroundKind!.Value);
        }

        private string NoteFor(MediaRef effective) =>
            effective is MediaRef.NoneRef && _owner.Song.StoredFilesOf(Slot).Count > 0 ? Strings.ArtRemovedNote : Strings.ArtNoneNote;

        /// <summary>Why the file at <paramref name="path"/> can't go in the slot, read off the UI thread; null when it can.</summary>
        private async Task<string?> CheckAsync(string path)
        {
            string fileName = Path.GetFileName(path);
            if (Slot.StoredNameFor(path) is null)
            {
                return Slot.Kind == MediaKind.BackgroundVideo
                    ? string.Format(CultureInfo.CurrentCulture, Strings.ArtNotVideoFormat, fileName)
                    : string.Format(CultureInfo.CurrentCulture, Strings.ArtNotImageFormat, fileName);
            }

            if (Slot.Kind != MediaKind.BackgroundVideo)
            {
                var (bitmap, error) = await Task.Run(() => LoadImage(path));
                bitmap?.Dispose();
                return error;
            }

            if (!FfmpegLibrary.IsAvailable)
                return File.Exists(path) ? null : string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtUnreadableFormat, path);
            try
            {
                await Task.Run(() => VideoDecoder.Check(path));
                return null;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return string.Format(CultureInfo.CurrentCulture, Strings.ArtVideoUnusableFormat, fileName, ex.Message);
            }
        }

        /// <summary>The image at <paramref name="path"/>, decoded at tile size, or why it can't be used.</summary>
        private static (Bitmap? Bitmap, string? Error) LoadImage(string path)
        {
            byte[] bytes;
            try
            {
                long length = new FileInfo(path).Length;
                if (length > ChartPacker.Core.Scanning.AlbumArt.DefaultMaxBytes)
                    return (null, string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtTooLargeFormat, ByteSizeFormatter.Format((ulong)ChartPacker.Core.Scanning.AlbumArt.DefaultMaxBytes)));
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtUnreadableFormat, ex.Message));
            }

            return Decode(bytes) is { } bitmap ? (bitmap, null) : (null, SharedStrings.ArtNotAnImage);
        }

        /// <summary>The image decoded at tile size, or null when it can't be decoded.</summary>
        private static Bitmap? Decode(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Corrupt or unsupported images surface as assorted decoder exceptions.
                return null;
            }
        }
    }
}
