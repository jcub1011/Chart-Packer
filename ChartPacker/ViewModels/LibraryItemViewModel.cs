using Avalonia.Media.Imaging;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Formatting;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>A song source in the library list.</summary>
    /// <remarks>
    /// The title and detail line come from the song's metadata once <see cref="SetSummary"/> has
    /// been called, with any unsaved draft applied so edits show before they're saved.
    /// </remarks>
    public sealed partial class LibraryItemViewModel(SongEntry entry, bool hasDrafts) : ObservableObject
    {
        private SongSummary? _summary;
        private bool _summaryRead;
        private volatile bool _isRealized;

        public SongEntry Entry { get; } = entry;

        public bool IsSng => Entry.Kind == SongEntryKind.SngFile;

        /// <summary>"SNG" or "FOLDER", for the chip after the title.</summary>
        public string KindLabel => IsSng ? Strings.KindSng : Strings.KindFolder;

        public bool HasAlbumArt => AlbumArt is not null;

        public bool HasSize => SizeText is not null;

        public bool HasLength => LengthText is not null;

        /// <summary>Maintained by <see cref="AlbumArtLoader"/>.</summary>
        internal AlbumArtState ArtState { get; set; }

        /// <summary>The unsaved album-art change the thumbnail was last queued with. Maintained by <see cref="AlbumArtLoader"/>.</summary>
        internal AlbumArtChange? ArtChange { get; set; }

        /// <summary>Bumped by <see cref="AlbumArtLoader"/> whenever a read under way goes stale.</summary>
        internal int ArtVersion { get; set; }

        /// <summary>The list has a row for this item. Read by <see cref="AlbumArtLoader"/>'s workers, so it's volatile.</summary>
        internal bool IsRealized
        {
            get => _isRealized;
            set => _isRealized = value;
        }

        /// <summary>The song has metadata changes that haven't been saved.</summary>
        [ObservableProperty]
        private bool _hasDrafts = hasDrafts;

        /// <summary>The song's name, or the file or folder name when it has none.</summary>
        [ObservableProperty]
        private string _title = entry.DisplayName;

        /// <summary>A loading note until the metadata is read, or the relative path when it couldn't be; otherwise empty.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowDetails))]
        private string _details = Strings.MetadataLoading;

        /// <summary>The artist, without rich-text tags; null when there is none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasArtist), nameof(HasArtistAndCharter), nameof(ShowByline), nameof(ShowDetails))]
        private string? _artist;

        /// <summary>The charter with its rich-text tags left in, for the view to render; null when there is none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCharter), nameof(HasArtistAndCharter), nameof(ShowByline), nameof(ShowDetails))]
        private string? _charter;

        /// <summary>This song is the one the player is playing, so the play button on its art shows a stop button.</summary>
        [ObservableProperty]
        private bool _isPreviewing;

        public bool HasArtist => Artist is not null;

        public bool HasCharter => Charter is not null;

        public bool HasArtistAndCharter => HasArtist && HasCharter;

        /// <summary>The line shows the artist and charter rather than <see cref="Details"/>.</summary>
        public bool ShowByline => HasArtist || HasCharter;

        public bool ShowDetails => !ShowByline;

        /// <summary>The song length, for the chip before the size chip; null until read or when unknown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLength))]
        private string? _lengthText;

        /// <summary>The file's or folder's total size, for the chip at the end of the detail line; null until read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSize))]
        private string? _sizeText;

        /// <summary>The album-art thumbnail, once <see cref="AlbumArtLoader"/> has read it. Owned by the loader.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAlbumArt))]
        private Bitmap? _albumArt;

        /// <summary>Shows what was read from disk, or <see langword="null"/> when the song couldn't be read.</summary>
        public void SetSummary(SongSummary? summary, MetadataDraft? draft)
        {
            _summary = summary;
            _summaryRead = true;
            Refresh(draft);
        }

        /// <summary>Recomputes the title and detail line with <paramref name="draft"/> applied.</summary>
        public void Refresh(MetadataDraft? draft)
        {
            if (!_summaryRead)
                return;

            if (_summary is null)
            {
                Title = Entry.DisplayName;
                Artist = null;
                Charter = null;
                Details = Entry.RelativePath;
                LengthText = null;
                SizeText = null;
                return;
            }

            var pairs = draft?.ApplyTo(_summary.Pairs) ?? _summary.Pairs;
            var headline = SongHeadline.From(pairs);
            Title = headline.Name ?? Entry.DisplayName;
            Artist = headline.Artist;
            Charter = SongHeadline.CharterMarkup(pairs);
            Details = string.Empty;

            // Without a song_length, the longest audio file stands in.
            LengthText = (headline.LengthMs ?? _summary.FallbackLengthMs) is { } length ? MetadataTimes.FormatDuration(length) : null;
            SizeText = ByteSizeFormatter.Format(_summary.TotalSize);
        }
    }
}
