using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ChartBuilder.Resources;
using ChartPacker.Audio.Encoding;
using ChartPacker.Core.Jobs;
using ChartPacker.UI.Services;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A stem as the export dialog classifies it: already Opus, or packed as-is unless auto-transcode is on.</summary>
    public sealed record ExportStemRow(string Stem, string FileName, bool IsOpus);

    /// <summary>A stem as the export dialog lists it, with what happens to its file.</summary>
    public sealed record ExportStemDisplay(string Stem, string FileName, string Note)
    {
        public string Text => $"{FileName} — {Note}";
    }

    /// <summary>A metadata pair as the export dialog previews it, as the next save leaves it.</summary>
    public sealed record ExportMetadataRow(string Key, string Value)
    {
        public string Text => $"{Key} = {Value}";
    }

    /// <summary>An if-exists choice in the export dialog's policy dropdown.</summary>
    public sealed record ExportPolicyChoice(OverwritePolicy Value, string Label);

    /// <summary>An Opus quality in the export dialog's quality dropdown.</summary>
    public sealed record ExportQualityChoice(OpusQuality Quality, string Label);

    /// <summary>
    /// Export to <c>.sng</c> (chart editor): a read-only preview of the song's metadata, art and stems,
    /// the export location (pre-filled, never silent), and what to do when the <c>.sng</c> already
    /// exists. Nothing is written until Export is chosen.
    /// </summary>
    /// <remarks>
    /// The target path comes from <paramref name="targetFor"/>, so this stays testable without a song on
    /// disk: the caller passes <c>dest => OutputPaths.PackTarget(entry, scanRoot, dest, layout)</c> for a
    /// song folder, or <c>dest => Path.Combine(dest, fileName)</c> for a song opened from a <c>.sng</c>.
    /// </remarks>
    public sealed partial class ExportSngViewModel : ViewModelBase
    {
        private readonly IFolderPickerService _folders;
        private readonly string _browseTitle;
        private readonly Func<string, string> _targetFor;
        private readonly Func<string, bool> _exists;

        /// <param name="songTitle">The song's title, as the dialog shows it.</param>
        /// <param name="metadata">The effective <c>song.ini</c> pairs, as the next save leaves them.</param>
        /// <param name="artStatus">The album art, in words (file name, or none).</param>
        /// <param name="albumArt">The album art image, read-only; null for none.</param>
        /// <param name="stems">The audio stems as the next save leaves them.</param>
        /// <param name="initialFolder">The location pre-filled: the last export folder, the pack folder, or the song's parent.</param>
        /// <param name="isSngSource">The song is opened from a <c>.sng</c>, which is copied as-is: no transcoding.</param>
        /// <param name="errorCount">The chart's blocking error count, which disables Export.</param>
        /// <param name="pendingCount">The unsaved changes Export saves first.</param>
        /// <param name="initialPolicy">The if-exists choice pre-filled: the last one, or Chart Packer's policy.</param>
        /// <param name="initialTranscode">Whether auto-transcode starts on. Off by default.</param>
        /// <param name="transcodeAvailable">Whether this build can transcode to Opus.</param>
        /// <param name="initialBitrateKbps">The Opus bitrate auto-transcode starts on, in kbps per stereo stem.</param>
        /// <param name="targetFor">The preferred <c>.sng</c> path for a destination folder.</param>
        /// <param name="folders">Picks the export location.</param>
        /// <param name="browseTitle">The folder picker's title.</param>
        /// <param name="exists">Whether a path exists; the file system by default.</param>
        public ExportSngViewModel(
            string songTitle,
            IReadOnlyList<ExportMetadataRow> metadata,
            string artStatus,
            Bitmap? albumArt,
            IReadOnlyList<ExportStemRow> stems,
            string initialFolder,
            bool isSngSource,
            int errorCount,
            int pendingCount,
            OverwritePolicy initialPolicy,
            bool initialTranscode,
            Func<string, string> targetFor,
            IFolderPickerService folders,
            string browseTitle,
            Func<string, bool>? exists = null,
            bool transcodeAvailable = true,
            int initialBitrateKbps = 128)
        {
            ArgumentNullException.ThrowIfNull(songTitle);
            ArgumentNullException.ThrowIfNull(metadata);
            ArgumentNullException.ThrowIfNull(artStatus);
            ArgumentNullException.ThrowIfNull(stems);
            ArgumentNullException.ThrowIfNull(initialFolder);
            ArgumentNullException.ThrowIfNull(targetFor);
            ArgumentNullException.ThrowIfNull(folders);
            ArgumentNullException.ThrowIfNull(browseTitle);

            SongTitle = songTitle;
            Metadata = metadata;
            ArtStatus = artStatus;
            AlbumArt = albumArt;
            Stems = stems;
            _targetFor = targetFor;
            _folders = folders;
            _browseTitle = browseTitle;
            _exists = exists ?? (path => File.Exists(path) || Directory.Exists(path));
            IsSngSource = isSngSource;
            ErrorCount = errorCount;
            PendingCount = pendingCount;
            IsTranscodeAvailable = transcodeAvailable;

            PolicyChoices =
            [
                new ExportPolicyChoice(OverwritePolicy.Overwrite, Strings.ExportSngPolicyOverwrite),
                new ExportPolicyChoice(OverwritePolicy.Rename, Strings.ExportSngPolicyRename),
                new ExportPolicyChoice(OverwritePolicy.Skip, Strings.ExportSngPolicySkip),
            ];
            SelectedPolicy = PolicyChoices.First(c => c.Value == initialPolicy);
            QualityChoices =
            [
                QualityChoice(OpusQuality.Voice, Strings.ExportSngQualityVoiceName),
                QualityChoice(OpusQuality.Balanced, Strings.ExportSngQualityBalancedName),
                QualityChoice(OpusQuality.Music, Strings.ExportSngQualityMusicName),
                QualityChoice(OpusQuality.High, Strings.ExportSngQualityHighName),
            ];
            SelectedQuality = QualityChoices
                .OrderBy(c => Math.Abs(OpusBitrates.StereoBps(c.Quality) / 1000 - initialBitrateKbps))
                .First();
            ExportFolder = initialFolder;
            TranscodeToOpus = !isSngSource && initialTranscode;
            RefreshDerived();
        }

        private static ExportQualityChoice QualityChoice(OpusQuality quality, string name) =>
            new(quality, string.Format(
                CultureInfo.CurrentCulture,
                Strings.ExportSngQualityChoiceFormat,
                name,
                OpusBitrates.StereoBps(quality) / 1000));

        /// <summary>The song's title, as the dialog shows it.</summary>
        public string SongTitle { get; }

        /// <summary>The effective <c>song.ini</c> pairs, as the next save leaves them.</summary>
        public IReadOnlyList<ExportMetadataRow> Metadata { get; }

        private string? Field(string key) =>
            Metadata.LastOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } value ? value : null;

        /// <summary>The headline fields, as the next save leaves them; null when absent.</summary>
        public string? Name => Field("name");

        public string? Artist => Field("artist");

        public string? Album => Field("album");

        public string? Genre => Field("genre");

        public string? Year => Field("year");

        public string? Charter => Field("charter");

        /// <summary><c>song_length</c> as minutes and seconds, or the raw value when it isn't milliseconds.</summary>
        public string? SongLength
        {
            get
            {
                string? raw = Field("song_length");
                if (string.IsNullOrWhiteSpace(raw))
                    return null;
                if (long.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out long ms) && ms >= 0)
                    return string.Format(CultureInfo.CurrentCulture, "{0}:{1:D2}", ms / 60000, ms / 1000 % 60);
                return raw;
            }
        }

        public bool HasName => Name is not null;

        public bool HasArtist => Artist is not null;

        public bool HasAlbum => Album is not null;

        public bool HasGenre => Genre is not null;

        public bool HasYear => Year is not null;

        public bool HasCharter => Charter is not null;

        public bool HasSongLength => SongLength is not null;

        public bool HasDetails => HasName || HasArtist || HasAlbum || HasGenre || HasYear || HasCharter || HasSongLength;

        private static readonly HashSet<string> HeadlineKeys = new(
            ["name", "artist", "album", "genre", "year", "charter", "song_length"],
            StringComparer.OrdinalIgnoreCase);

        /// <summary>The metadata beyond the headline fields, read-only.</summary>
        public IReadOnlyList<ExportMetadataRow> OtherMetadata =>
            [.. Metadata.Where(m => !HeadlineKeys.Contains(m.Key))];

        public bool HasOtherMetadata => OtherMetadata.Count > 0;

        /// <summary>The album art, in words.</summary>
        public string ArtStatus { get; }

        /// <summary>The album art image, read-only; null for none.</summary>
        public Bitmap? AlbumArt { get; }

        public bool HasAlbumArt => AlbumArt is not null;

        /// <summary>The stems' classification.</summary>
        public IReadOnlyList<ExportStemRow> Stems { get; }

        public bool HasStems => Stems.Count > 0;

        /// <summary>How many stems aren't Opus.</summary>
        public int NonOpusCount => Stems.Count(s => !s.IsOpus);

        /// <summary>The stems as listed, with what happens to each file.</summary>
        public IReadOnlyList<ExportStemDisplay> StemDisplays { get; private set; } = [];

        public bool IsSngSource { get; }

        public int ErrorCount { get; }

        public int PendingCount { get; }

        public string? ErrorsText => ErrorCount > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.ExportSngErrorsFormat, ErrorCount)
            : null;

        public string? PendingText => PendingCount > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.ExportSngPendingFormat, PendingCount)
            : null;

        /// <summary>Whether auto-transcode can be turned on: song folders only.</summary>
        public bool CanTranscode => !IsSngSource;

        /// <summary>Whether this build can transcode to Opus at all.</summary>
        public bool IsTranscodeAvailable { get; }

        public string TranscodeNote => IsSngSource ? Strings.ExportSngTranscodeSngNote : Strings.ExportSngTranscodeNote;

        /// <summary>Whether auto-transcode is on. Off by default; unavailable for <c>.sng</c> sources.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StemDisplays), nameof(TranscodeBlocked), nameof(Problem), nameof(HasProblem))]
        [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
        public partial bool TranscodeToOpus { get; set; }

        /// <summary>Auto-transcode is checked but can't run: this build has no Opus encoder.</summary>
        public bool TranscodeBlocked => TranscodeToOpus && !IsSngSource && NonOpusCount > 0 && !IsTranscodeAvailable;

        public IReadOnlyList<ExportQualityChoice> QualityChoices { get; }

        /// <summary>The Opus quality auto-transcode encodes with.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TranscodeBitrateKbps))]
        public partial ExportQualityChoice SelectedQuality { get; set; }

        /// <summary>The Opus bitrate in kbps per stereo stem (mono stems use half).</summary>
        public int TranscodeBitrateKbps => OpusBitrates.StereoBps(SelectedQuality.Quality) / 1000;

        /// <summary>The export location.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TargetPath), nameof(TargetExists), nameof(ExistsText), nameof(EffectiveTarget), nameof(HasTarget), nameof(RenameText), nameof(Problem), nameof(HasProblem))]
        [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
        public partial string ExportFolder { get; set; } = "";

        /// <summary>The preferred <c>.sng</c> path, or empty when the location is unusable.</summary>
        public string TargetPath { get; private set; } = "";

        /// <summary>Whether the preferred target already exists.</summary>
        public bool TargetExists { get; private set; }

        public string? ExistsText => TargetExists
            ? string.Format(CultureInfo.CurrentCulture, Strings.ExportSngExistsFormat, TargetPath)
            : null;

        /// <summary>What Export writes: the preferred target, or the rename candidate.</summary>
        public string EffectiveTarget { get; private set; } = "";

        public bool HasTarget => EffectiveTarget.Length > 0;

        public string? RenameText => SelectedPolicy.Value == OverwritePolicy.Rename && TargetExists && EffectiveTarget.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.ExportSngRenamePreviewFormat, EffectiveTarget)
            : null;

        public IReadOnlyList<ExportPolicyChoice> PolicyChoices { get; }

        /// <summary>What to do when the <c>.sng</c> already exists.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveTarget), nameof(HasTarget), nameof(RenameText))]
        public partial ExportPolicyChoice SelectedPolicy { get; set; }

        /// <summary>Why Export can't run, or null when it can.</summary>
        public string? Problem
        {
            get
            {
                if (ErrorCount > 0)
                    return ErrorsText;
                if (string.IsNullOrWhiteSpace(ExportFolder))
                    return Strings.ExportSngNoFolder;
                if (TargetPath.Length == 0)
                    return Strings.ExportSngNoFolder;
                if (TranscodeBlocked)
                    return Strings.ExportSngTranscodeUnavailable;
                return null;
            }
        }

        public bool HasProblem => Problem is not null;

        public bool CanExport() => !HasProblem;

        /// <summary>Raised when the user chooses Export with usable settings.</summary>
        public event System.EventHandler? Exported;

        /// <summary>The policy to pack or copy with.</summary>
        public OverwritePolicy Policy => SelectedPolicy.Value;

        partial void OnTranscodeToOpusChanged(bool value)
        {
            if (value && IsSngSource)
            {
                TranscodeToOpus = false;
                return;
            }

            RefreshDerived();
        }

        partial void OnExportFolderChanged(string value) => RefreshDerived();

        partial void OnSelectedPolicyChanged(ExportPolicyChoice value) => RefreshDerived();

        [RelayCommand]
        private async Task BrowseAsync()
        {
            string? folder = await _folders.PickFolderAsync(
                _browseTitle,
                string.IsNullOrWhiteSpace(ExportFolder) ? null : ExportFolder);
            if (folder is not null)
                ExportFolder = folder;
        }

        [RelayCommand(CanExecute = nameof(CanExport))]
        private void Export() => Exported?.Invoke(this, System.EventArgs.Empty);

        private void RefreshDerived()
        {
            string preferred = "";
            if (!string.IsNullOrWhiteSpace(ExportFolder))
            {
                try
                {
                    preferred = _targetFor(ExportFolder);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    preferred = "";
                }
            }

            TargetPath = preferred;
            TargetExists = preferred.Length > 0 && _exists(preferred);
            EffectiveTarget = SelectedPolicy.Value == OverwritePolicy.Rename && TargetExists
                ? FirstFreeName(preferred)
                : preferred;
            StemDisplays = [.. Stems.Select(s => new ExportStemDisplay(
                s.Stem,
                s.FileName,
                s.IsOpus ? Strings.ExportSngStemPassthrough
                    : TranscodeToOpus ? Strings.ExportSngStemWillTranscode
                    : Strings.ExportSngStemAsIs))];
            OnPropertyChanged(nameof(TargetPath));
            OnPropertyChanged(nameof(TargetExists));
            OnPropertyChanged(nameof(ExistsText));
            OnPropertyChanged(nameof(EffectiveTarget));
            OnPropertyChanged(nameof(HasTarget));
            OnPropertyChanged(nameof(RenameText));
            OnPropertyChanged(nameof(StemDisplays));
            OnPropertyChanged(nameof(TranscodeBlocked));
            OnPropertyChanged(nameof(Problem));
            OnPropertyChanged(nameof(HasProblem));
            ExportCommand.NotifyCanExecuteChanged();
        }

        /// <summary>The first free <c>Name (n).sng</c>, mirroring <see cref="OutputPaths.Resolve"/>.</summary>
        private string FirstFreeName(string preferred)
        {
            string folder = Path.GetDirectoryName(preferred) ?? "";
            string stem = Path.GetFileNameWithoutExtension(preferred);
            string extension = Path.GetExtension(preferred);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
                if (!_exists(candidate))
                    return candidate;
            }
        }
    }
}
