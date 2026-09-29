using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Formatting;
using ChartPacker.Resources;
using ChartPacker.Video;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// A song's background image and video as stored: the file of each kind that's used, if any,
    /// and a key that's the same only for the same content.
    /// </summary>
    public sealed record StoredBackgrounds(StoredBackground? Image, StoredBackground? Video)
    {
        public static StoredBackgrounds None { get; } = new(null, null);

        public StoredBackground? Of(BackgroundKind kind) => kind == BackgroundKind.Image ? Image : Video;
    }

    /// <param name="Key">Equal for two songs only when their files are the same.</param>
    public sealed record StoredBackground(string StoredName, long Length, string Key);

    /// <summary>A file chosen as a background.</summary>
    /// <param name="Key">Equal for two choices only when they're the same file or content.</param>
    /// <param name="Error">Why it can't be used, if it can't.</param>
    public sealed record StagedBackground(BackgroundKind Kind, string Key, long Length, string? Error);

    /// <summary>One selected song, as the background tile needs it.</summary>
    /// <param name="Stored">Its backgrounds as stored.</param>
    /// <param name="Timeline">How its video lines up with its audio, with its draft applied.</param>
    public sealed record BackgroundSong(SongEntry Entry, MetadataDraft Draft, StoredBackgrounds Stored, VideoTimeline Timeline);

    /// <summary>The selection's backgrounds as loaded off the UI thread.</summary>
    /// <param name="Stored">Each song's, by index; <see cref="StoredBackgrounds.None"/> for a song that wasn't read.</param>
    /// <param name="Images">Decoded images by key: the first song's stored image, and every staged one.</param>
    /// <param name="Staged">Staged files by source path.</param>
    public sealed record LoadedBackgrounds(IReadOnlyList<StoredBackgrounds> Stored, Dictionary<string, Bitmap> Images, Dictionary<string, StagedBackground> Staged)
    {
        // A new one each time: the dictionaries are handed over and added to.
        public static LoadedBackgrounds Empty => new([], [], new(StringComparer.Ordinal));
    }

    /// <summary>
    /// The background tile beside the album art: the selected songs' background video or image once
    /// their drafts are applied, shown only when every song has the same one, like the art.
    /// </summary>
    /// <remarks>
    /// A song can have both kinds. Then the video is shown first, as games prefer it, and
    /// <see cref="ShowVideo"/> switches between them. The video follows the audio player's position,
    /// through the first song's <see cref="VideoTimeline"/>; it's opened while shown, and holds its
    /// file open until <see cref="Unload"/>.
    /// </remarks>
    public sealed partial class BackgroundViewModel : ViewModelBase
    {
        /// <summary>Background images larger than this aren't read, as for album art.</summary>
        public const long MaxImageBytes = AlbumArt.DefaultMaxBytes;

        /// <summary>The tile's shape while there's no image or video to take it from: the usual video shape.</summary>
        public const double DefaultAspectRatio = 16.0 / 9.0;

        // A 16:9 tile at its 512px-tall largest is 910px wide; this is that at 2x scaling.
        private const int DecodeWidth = 1820;

        // Video is decoded a frame at a time, so it's decoded smaller than a still.
        private const int VideoDecodeWidth = 1280;

        private readonly Func<TimeSpan> _clock;

        // The video whose frame size the tile has taken; read on the playback thread, so a frame
        // doesn't post an update once it has.
        private volatile VideoPlayback? _sizedVideo;
        private Dictionary<string, Bitmap> _images = [];
        private Dictionary<string, StagedBackground> _staged = new(StringComparer.Ordinal);

        // The video being shown, by what it plays.
        private VideoSource? _videoSource;

        // Playbacks stopped but maybe not yet closed; see WhenClosedAsync.
        private readonly List<Task> _closing = [];

        // The video key last shown, so that a new video is shown first.
        private string? _lastVideoKey;

        // What Refresh last worked out, for showing the other kind without working it out again.
        private IReadOnlyList<BackgroundSong> _songs = [];
        private Slot _imageSlot = Slot.None;
        private Slot _videoSlot = Slot.None;

        // Set while Refresh picks the kind to show, which it then shows itself.
        private bool _isRefreshing;

        // The kind asked for by Show, for the next Refresh.
        private BackgroundKind? _preferredKind;

        /// <param name="clock">Where the audio player is, read each frame the video is drawn.</param>
        public BackgroundViewModel(Func<TimeSpan> clock)
        {
            _clock = clock;
        }

        /// <summary>Where the audio player is; the video frame drawn is the one due then.</summary>
        public Func<TimeSpan> Clock => _clock;

        /// <summary>The image to show, when the image is shown and every song has the same one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasImage))]
        private Bitmap? _image;

        /// <summary>The video to show, when the video is shown and every song has the same one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideo))]
        private VideoPlayback? _video;

        /// <summary>How the shown video lines up with the audio.</summary>
        [ObservableProperty]
        private VideoTimeline _timeline = VideoTimeline.Default;

        /// <summary>Shown instead of an image or video, e.g. "No background set" or "Backgrounds differ".</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNote))]
        private string? _note;

        /// <summary>The video is shown rather than the image. Only a choice when <see cref="HasBoth"/>.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsImageSelected))]
        [NotifyPropertyChangedFor(nameof(IsVideoSelected))]
        [NotifyPropertyChangedFor(nameof(ShownKind))]
        [NotifyPropertyChangedFor(nameof(RemoveToolTip))]
        private bool _showVideo = true;

        /// <summary>The selection has a background image, so the Image toggle can show it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasBoth))]
        [NotifyPropertyChangedFor(nameof(IsImageSelected))]
        private bool _hasImageKind;

        /// <summary>The selection has a background video, so the Video toggle can show it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasBoth))]
        [NotifyPropertyChangedFor(nameof(IsVideoSelected))]
        private bool _hasVideoKind;

        /// <summary>A selected song has a staged background change.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Marker))]
        private bool _isChanged;

        /// <summary>The tile's width over its height: the shown image's or video's, or <see cref="DefaultAspectRatio"/>.</summary>
        [ObservableProperty]
        private double _aspectRatio = DefaultAspectRatio;

        /// <summary>The shown background's stored name, e.g. <c>video.mp4</c>, when every song has the same one.</summary>
        [ObservableProperty]
        private string? _shownFileName;

        /// <summary>The shown background's size, formatted, when it's known.</summary>
        [ObservableProperty]
        private string? _shownFileSize;

        public bool HasImage => Image is not null;

        public bool HasVideo => Video is not null;

        public bool HasNote => Note is not null;

        /// <summary>The selection has an image and a video, so either can be shown.</summary>
        public bool HasBoth => HasImageKind && HasVideoKind;

        /// <summary>The Image toggle: on while the image is shown. Turning it off does nothing; turning Video on does.</summary>
        public bool IsImageSelected
        {
            get => !ShowVideo && HasImageKind;
            set => Select(BackgroundKind.Image, value);
        }

        /// <summary>The Video toggle: on while the video is shown. Turning it off does nothing; turning Image on does.</summary>
        public bool IsVideoSelected
        {
            get => ShowVideo && HasVideoKind;
            set => Select(BackgroundKind.Video, value);
        }

        /// <summary>The kind shown, and so the one Delete removes.</summary>
        public BackgroundKind ShownKind => ShowVideo ? BackgroundKind.Video : BackgroundKind.Image;

        /// <summary><c>*</c> on the tile while a background has a staged change, like the art's.</summary>
        public string? Marker => IsChanged ? "*" : null;

        public string RemoveToolTip => ShowVideo ? Strings.RemoveBackgroundVideoToolTip : Strings.RemoveBackgroundImageToolTip;

        /// <summary>The staged file at <paramref name="sourcePath"/>, once it's been read.</summary>
        public StagedBackground? StagedAt(string sourcePath) => _staged.GetValueOrDefault(sourcePath);

        /// <summary>
        /// The key of the background of <paramref name="kind"/> that <paramref name="song"/> will have
        /// once its draft is saved, or <see langword="null"/> for none.
        /// </summary>
        public string? EffectiveKey(BackgroundSong song, BackgroundKind kind) => song.Draft.GetBackground(kind) switch
        {
            null => song.Stored.Of(kind)?.Key,
            { SourcePath: null } => null,
            { SourcePath: { } source } => _staged.GetValueOrDefault(source)?.Key ?? "unreadable:" + source,
        };

        /// <summary>Takes the selection's loaded backgrounds, letting go of what was shown before.</summary>
        public void Apply(LoadedBackgrounds loaded)
        {
            Unload();
            _images = loaded.Images;
            _staged = loaded.Staged;
        }

        /// <summary>Stops the video and lets go of every decoded image. <see cref="WhenClosedAsync"/> waits for the video's file to close.</summary>
        public void Unload()
        {
            StopVideo();
            Image = null;
            Note = null;
            AspectRatio = DefaultAspectRatio;
            ShownFileName = null;
            ShownFileSize = null;
            HasImageKind = false;
            HasVideoKind = false;
            IsChanged = false;
            _lastVideoKey = null;
            _preferredKind = null;
            _isRefreshing = true;
            ShowVideo = true;
            _isRefreshing = false;
            _songs = [];
            _imageSlot = Slot.None;
            _videoSlot = Slot.None;
            DisposeLater(_images.Values);
            _images = [];
            _staged = new(StringComparer.Ordinal);
        }

        /// <summary>Completes once every stopped video has closed its file.</summary>
        public async Task WhenClosedAsync()
        {
            _closing.RemoveAll(t => t.IsCompleted);
            if (_closing.Count > 0)
                await Task.WhenAny(Task.WhenAll(_closing), Task.Delay(TimeSpan.FromSeconds(2)));
        }

        /// <summary>Adds a file read by <see cref="LoadStagedAsync"/>, to be shown once a draft uses it.</summary>
        public void AddStaged(string sourcePath, StagedBackground staged, Bitmap? bitmap)
        {
            _staged[sourcePath] = staged;
            if (bitmap is not null && !_images.TryAdd(staged.Key, bitmap))
                DisposeLater([bitmap]);
        }

        /// <summary>Shows <paramref name="kind"/> from the next <see cref="Refresh"/>, e.g. the kind just chosen, when there's one to show.</summary>
        public void Show(BackgroundKind kind) => _preferredKind = kind;

        /// <summary>Shows what <paramref name="songs"/> have once their drafts are applied, or why there's nothing to show.</summary>
        public void Refresh(IReadOnlyList<BackgroundSong> songs)
        {
            _songs = songs;
            _imageSlot = Resolve(songs, BackgroundKind.Image);
            _videoSlot = Resolve(songs, BackgroundKind.Video);
            IsChanged = songs.Any(s => s.Draft.HasBackgroundChanges);
            HasImageKind = _imageSlot.State != SlotState.None;
            HasVideoKind = _videoSlot.State != SlotState.None;

            // The video comes first: when it's new, or when it's the only kind there is.
            _isRefreshing = true;
            try
            {
                if (_videoSlot.State == SlotState.None)
                    ShowVideo = false;
                else if (_imageSlot.State == SlotState.None)
                    ShowVideo = true;
                else if (_preferredKind is { } preferred)
                    ShowVideo = preferred == BackgroundKind.Video;
                else if (_videoSlot.Key is { } key && key != _lastVideoKey)
                    ShowVideo = true;
            }
            finally
            {
                _isRefreshing = false;
                _preferredKind = null;
            }

            _lastVideoKey = _videoSlot.Key;
            Render();
        }

        partial void OnShowVideoChanged(bool value)
        {
            if (!_isRefreshing)
                Render();
        }

        // A toggle clicked while on stays on: its state is put back once the click is done.
        private void Select(BackgroundKind kind, bool selected)
        {
            if (selected)
            {
                ShowVideo = kind == BackgroundKind.Video;
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(IsImageSelected));
                OnPropertyChanged(nameof(IsVideoSelected));
            });
        }

        /// <summary>Shows the kind <see cref="ShowVideo"/> picks, as <see cref="Refresh"/> last worked it out.</summary>
        private void Render()
        {
            var shown = ShowVideo ? _videoSlot : _imageSlot;
            ShowFileOf(ShownKind, shown);

            if (_imageSlot.State == SlotState.None && _videoSlot.State == SlotState.None)
            {
                bool removing = _songs.Any(s => SongBackgrounds.Kinds.Any(k => s.Draft.GetBackground(k) is { IsRemoval: true }));
                ShowStill(null, _songs.Count == 0 ? null : removing ? Strings.BackgroundWillBeRemoved : Strings.NoBackground);
                return;
            }

            if (shown.State == SlotState.Differs)
            {
                ShowStill(null, Strings.BackgroundsDiffer);
                return;
            }

            if (!ShowVideo)
            {
                var bitmap = _images.GetValueOrDefault(shown.Key!);
                ShowStill(bitmap, bitmap is null ? Strings.BackgroundImageUnreadable : null);
                return;
            }

            // Every song has this video, so the first one's is played, along its timeline.
            var first = _songs[0];
            var source = first.Draft.GetBackground(BackgroundKind.Video)?.SourcePath is { } staged
                ? new VideoSource(first.Entry, null, staged)
                : new VideoSource(first.Entry, first.Stored.Video!.StoredName, null);
            Timeline = first.Timeline;

            if (!FfmpegLibrary.IsAvailable)
            {
                ShowStill(null, string.Format(CultureInfo.CurrentCulture, Strings.VideoPreviewUnavailableFormat, source.DisplayName));
                return;
            }

            if (source != _videoSource || Video is null)
                StartVideo(source);
            Image = null;
            Note = Video?.Error is { } error ? string.Format(CultureInfo.CurrentCulture, Strings.VideoUnplayableFormat, error) : null;
            TakeVideoSize();
        }

        /// <summary>Shows <paramref name="image"/> or <paramref name="note"/>, and no video.</summary>
        private void ShowStill(Bitmap? image, string? note)
        {
            StopVideo();
            Image = image;
            Note = note;
            AspectRatio = image is { Size: { Width: > 0, Height: > 0 } size } ? size.Width / size.Height : DefaultAspectRatio;
        }

        /// <summary>
        /// Names the file of <paramref name="kind"/> that <paramref name="slot"/> shows, and its size:
        /// the first song's, since every song has the same one. Nothing when the songs differ or have none.
        /// </summary>
        private void ShowFileOf(BackgroundKind kind, Slot slot)
        {
            string? name = null;
            string? size = null;
            if (slot.State == SlotState.Same && _songs.Count > 0)
            {
                var first = _songs[0];
                if (first.Draft.GetBackground(kind)?.SourcePath is { } source)
                {
                    name = SongBackgrounds.StoredNameFor(kind, source);
                    if (_staged.GetValueOrDefault(source) is { Error: null } staged)
                        size = ByteSizeFormatter.Format((ulong)staged.Length);
                }
                else if (first.Stored.Of(kind) is { } stored)
                {
                    name = stored.StoredName;
                    size = ByteSizeFormatter.Format((ulong)stored.Length);
                }
            }

            ShownFileName = name;
            ShownFileSize = size;
        }

        private void StartVideo(VideoSource source)
        {
            StopVideo();
            var playback = new VideoPlayback(source.Open, VideoDecodeWidth);
            playback.Changed += OnVideoChanged;
            _videoSource = source;
            Video = playback;
            AspectRatio = DefaultAspectRatio;
        }

        /// <summary>Gives the tile the shown video's shape, once its frame size is known.</summary>
        private void TakeVideoSize()
        {
            if (Video is { Width: > 0, Height: > 0 } playback)
            {
                AspectRatio = (double)playback.Width / playback.Height;
                _sizedVideo = playback;
            }
        }

        private void StopVideo()
        {
            if (Video is { } playback)
            {
                playback.Changed -= OnVideoChanged;
                playback.Dispose();
                _closing.RemoveAll(t => t.IsCompleted);
                _closing.Add(playback.WhenClosedAsync());
            }

            Video = null;
            _videoSource = null;
        }

        // Raised on the playback thread; only a failure, or learning the frame size, changes the tile.
        private void OnVideoChanged(object? sender, EventArgs e)
        {
            if (sender is not VideoPlayback playback)
                return;

            if (playback.Error is { } error)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(playback, Video))
                        Note = string.Format(CultureInfo.CurrentCulture, Strings.VideoUnplayableFormat, error);
                });
            }
            else if (playback.Width > 0 && !ReferenceEquals(playback, _sizedVideo))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(playback, Video))
                        TakeVideoSize();
                });
            }
        }

        private Slot Resolve(IReadOnlyList<BackgroundSong> songs, BackgroundKind kind)
        {
            var keys = songs.Select(s => EffectiveKey(s, kind)).Distinct(StringComparer.Ordinal).ToList();
            return keys switch
            {
                [] or [null] => Slot.None,
                [{ } key] => new Slot(SlotState.Same, key),
                _ => new Slot(SlotState.Differs, null),
            };
        }

        /// <summary>
        /// Finds each song's stored backgrounds, keys them so equal files match, decodes the first
        /// song's image, and reads the staged files. Runs off the UI thread.
        /// </summary>
        /// <param name="entries">The songs, by index; <see langword="null"/> for one that wasn't read.</param>
        /// <param name="stagedSources">The files the drafts stage as backgrounds.</param>
        public static async Task<LoadedBackgrounds> LoadAsync(IReadOnlyList<SongEntry?> entries, IReadOnlyList<string> stagedSources, CancellationToken cancellationToken)
        {
            var stored = new StoredBackgrounds[entries.Count];
            var images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
            var staged = new Dictionary<string, StagedBackground>(StringComparer.Ordinal);
            try
            {
                var videos = new (SongEntry Entry, StoredFile File)?[entries.Count];
                int first = entries.ToList().FindIndex(e => e is not null);
                for (int i = 0; i < entries.Count; i++)
                {
                    stored[i] = StoredBackgrounds.None;
                    if (entries[i] is not { } entry)
                        continue;

                    IReadOnlyList<StoredFile> files;
                    try
                    {
                        files = SongFile.ListTopLevel(entry, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    StoredBackground? image = null;
                    if (SongBackgrounds.Pick(files, f => f.Name, BackgroundKind.Image) is { } imageFile)
                    {
                        byte[]? bytes = await TryReadAsync(entry, imageFile.Name, cancellationToken).ConfigureAwait(false);
                        string key = bytes is null ? $"unreadable:{entry.FullPath}" : Hash(bytes);
                        image = new StoredBackground(imageFile.Name, imageFile.Length, key);
                        if (i == first && bytes is not null && Decode(bytes) is { } bitmap && !images.TryAdd(key, bitmap))
                            bitmap.Dispose();
                    }

                    if (SongBackgrounds.Pick(files, f => f.Name, BackgroundKind.Video) is { } videoFile)
                        videos[i] = (entry, videoFile);

                    stored[i] = new StoredBackgrounds(image, null);
                }

                // Videos of different lengths differ, which is nearly always the case for different
                // videos; only when every song's is the same length are they read through to compare.
                var withVideo = videos.OfType<(SongEntry Entry, StoredFile File)>().ToList();
                bool compare = withVideo.Count > 1 && withVideo.All(v => v.File.Length == withVideo[0].File.Length);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (videos[i] is not var (entry, file))
                        continue;

                    string key = $"length:{file.Length}";
                    if (compare)
                    {
                        try
                        {
                            key = await SongFile.HashAsync(entry, file.Name, cancellationToken).ConfigureAwait(false) ?? $"unreadable:{entry.FullPath}";
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            key = $"unreadable:{entry.FullPath}";
                        }
                    }

                    stored[i] = stored[i] with { Video = new StoredBackground(file.Name, file.Length, key) };
                }

                foreach (string source in stagedSources)
                {
                    var (background, bitmap) = await LoadStagedAsync(source, cancellationToken).ConfigureAwait(false);
                    staged[source] = background;
                    if (bitmap is not null && !images.TryAdd(background.Key, bitmap))
                        bitmap.Dispose();
                }
            }
            catch
            {
                foreach (var bitmap in images.Values)
                    bitmap.Dispose();
                throw;
            }

            return new LoadedBackgrounds(stored, images, staged);
        }

        /// <summary>
        /// Reads a file chosen as a background: an image is size-checked, hashed and decoded; a video
        /// is checked to be one that can be decoded, when FFmpeg is there to check it.
        /// </summary>
        /// <returns>The file, with an image's decoded bitmap; or, when it can't be used, why not and no bitmap.</returns>
        public static async Task<(StagedBackground Background, Bitmap? Bitmap)> LoadStagedAsync(string path, CancellationToken cancellationToken)
        {
            var kind = SongBackgrounds.KindOf(path);
            if (kind is null)
                return (new StagedBackground(BackgroundKind.Image, "unusable:" + path, 0, Strings.BackgroundNotSupported), null);

            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (new StagedBackground(kind.Value, "unusable:" + path, 0, string.Format(CultureInfo.CurrentCulture, Strings.BackgroundUnreadableFormat, ex.Message)), null);
            }

            if (kind == BackgroundKind.Video)
            {
                string? problem = null;
                if (FfmpegLibrary.IsAvailable)
                {
                    try
                    {
                        await Task.Run(() => VideoDecoder.Check(path), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        problem = string.Format(CultureInfo.CurrentCulture, Strings.VideoUnusableFormat, ex.Message);
                    }
                }
                else if (!File.Exists(path))
                {
                    problem = string.Format(CultureInfo.CurrentCulture, Strings.BackgroundUnreadableFormat, path);
                }

                return (new StagedBackground(BackgroundKind.Video, "staged:" + path, length, problem), null);
            }

            if (length > MaxImageBytes)
            {
                string limit = ByteSizeFormatter.Format((ulong)MaxImageBytes);
                return (new StagedBackground(BackgroundKind.Image, "unusable:" + path, length, string.Format(CultureInfo.CurrentCulture, Strings.BackgroundImageTooLargeFormat, limit)), null);
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (new StagedBackground(BackgroundKind.Image, "unusable:" + path, length, string.Format(CultureInfo.CurrentCulture, Strings.BackgroundUnreadableFormat, ex.Message)), null);
            }

            return Decode(bytes) is { } bitmap
                ? (new StagedBackground(BackgroundKind.Image, Hash(bytes), bytes.Length, null), bitmap)
                : (new StagedBackground(BackgroundKind.Image, "unusable:" + path, bytes.Length, Strings.ArtNotAnImage), null);
        }

        /// <summary>Disposes <paramref name="bitmaps"/> once the UI has rendered without them.</summary>
        public static void DisposeLater(IEnumerable<Bitmap> bitmaps)
        {
            var old = bitmaps.ToArray();
            if (old.Length == 0)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var bitmap in old)
                    bitmap.Dispose();
            }, DispatcherPriority.Background);
        }

        private static async Task<byte[]?> TryReadAsync(SongEntry entry, string name, CancellationToken cancellationToken)
        {
            try
            {
                return await SongFile.ReadAsync(entry, name, MaxImageBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        /// <summary>The image decoded at tile size, or <see langword="null"/> when it can't be decoded.</summary>
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

        private enum SlotState
        {
            None,
            Same,
            Differs,
        }

        /// <param name="Key">The key every song shares, when <see cref="State"/> is <see cref="SlotState.Same"/>.</param>
        private sealed record Slot(SlotState State, string? Key)
        {
            public static Slot None { get; } = new(SlotState.None, null);
        }

        /// <summary>The video file to play: a song's stored file, or a staged one.</summary>
        private sealed record VideoSource(SongEntry Entry, string? StoredName, string? StagedPath)
        {
            public string DisplayName => StoredName ?? Path.GetFileName(StagedPath)!;

            // Called on the playback thread.
            public Stream Open() => StagedPath is { } path
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024)
                : SongFile.OpenRead(Entry, StoredName!) ?? throw new FileNotFoundException("The video isn't in the song any more.", StoredName);
        }
    }
}
