using System.Collections.Generic;
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

        /// <summary>Maintained by <see cref="AlbumArtLoader"/>.</summary>
        internal AlbumArtState ArtState { get; set; }

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

        /// <summary>Artist, length, charter and size; a loading note until the metadata is read.</summary>
        [ObservableProperty]
        private string _details = Strings.MetadataLoading;

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
                Details = Entry.RelativePath;
                return;
            }

            var headline = SongHeadline.From(draft?.ApplyTo(_summary.Pairs) ?? _summary.Pairs);
            Title = headline.Name ?? Entry.DisplayName;

            var parts = new List<string>(4);
            if (headline.Artist is { } artist)
                parts.Add(artist);
            if (headline.LengthMs is { } length)
                parts.Add(MetadataTimes.FormatDuration(length));
            if (headline.Charter is { } charter)
                parts.Add(charter);
            parts.Add(ByteSizeFormatter.Format(_summary.TotalSize));
            Details = string.Join(Strings.ListDetailSeparator, parts);
        }
    }
}
