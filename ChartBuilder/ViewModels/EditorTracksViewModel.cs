using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Scanning;
using ChartShared.UI.Formatting;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// Chart settings' Tracks (charter-spec C1): the song's audio tracks as Chart Packer's inspector lists them, each with
    /// its mute, solo and volume, in place of the transport bar's Stems menu. Adding, replacing and removing a track is an
    /// undo step (<see cref="MediaCommands"/>), a pending change until the song is saved, and the editor plays the tracks
    /// as they'll be saved; the mute, solo and volume are only for listening.
    /// </summary>
    public sealed partial class EditorTracksViewModel : ViewModelBase, IDisposable
    {
        private readonly EditorSong _song;
        private readonly PlaybackViewModel _playback;
        private readonly IFilePickerService _filePicker;
        private readonly Action<IChartCommand> _execute;
        private readonly CancellationTokenSource _closing = new();
        private readonly Dictionary<string, RelayCommand> _addCommands = [];
        private CancellationTokenSource? _sizesCts;
        private IReadOnlyList<TrackRowViewModel> _rows = [];

        /// <param name="song">The song open.</param>
        /// <param name="playback">Plays the tracks, and holds their rows and switches.</param>
        /// <param name="filePicker">Picks a track's file.</param>
        /// <param name="execute">Makes an edit, as one undo step.</param>
        public EditorTracksViewModel(EditorSong song, PlaybackViewModel playback, IFilePickerService filePicker, Action<IChartCommand> execute)
        {
            ArgumentNullException.ThrowIfNull(song);
            ArgumentNullException.ThrowIfNull(playback);
            ArgumentNullException.ThrowIfNull(filePicker);
            ArgumentNullException.ThrowIfNull(execute);
            _song = song;
            _playback = playback;
            _filePicker = filePicker;
            _execute = execute;
            foreach (string stem in AudioStems.Stems)
                _addCommands[stem] = new RelayCommand(() => _ = SetTrackAsync(stem), () => CanEdit);
            AddTrackOptions = TrackStaging.OptionsFor(stem => _addCommands[stem]);
            _playback.PropertyChanged += OnPlaybackChanged;
            _song.Changed += OnSongChanged;
            _song.TracksChanged += OnSongTracksChanged;
            _rows = RowsNow();
            Refresh();
        }

        /// <summary>
        /// The tracks, as they're played: a row each. A track removed goes at once, rather than once the audio is opened
        /// again without it, so its row isn't there to be removed again.
        /// </summary>
        public IReadOnlyList<TrackRowViewModel> Tracks => _rows;

        /// <summary>The song's tracks a pending change removes, and nothing replaces.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<RemovedTrackRow> RemovedTracks { get; private set; } = [];

        /// <summary>The Add track menu: every stem, labelled with its name.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<StemOption> AddTrackOptions { get; private set; } = [];

        /// <summary>Why the file chosen can't be a track; null when there's nothing to say.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? Error { get; private set; }

        public bool HasError => Error is not null;

        /// <summary>Whether the tracks can be changed now: not while the song is being saved.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        public partial bool IsSaving { get; set; }

        public bool CanEdit => !IsSaving;

        public void Dispose()
        {
            _closing.Cancel();
            _sizesCts?.Cancel();
            _playback.PropertyChanged -= OnPlaybackChanged;
            _song.Changed -= OnSongChanged;
            _song.TracksChanged -= OnSongTracksChanged;
        }

        /// <summary>Asks for a file to put in place of <paramref name="row"/>'s track, as Add track does for its stem.</summary>
        [RelayCommand]
        private Task ReplaceTrackAsync(TrackRowViewModel? row) => row is null ? Task.CompletedTask : SetTrackAsync(row.Track.Stem);

        /// <summary>
        /// Removes <paramref name="row"/>'s track. One a pending change put in place of the song's own takes the change
        /// back instead, as Chart Packer's Remove does, bringing the song's back.
        /// </summary>
        [RelayCommand]
        private void RemoveTrack(TrackRowViewModel? row)
        {
            if (row is null || !CanEdit)
                return;
            var slot = MediaSlot.Track(row.Track.Stem);
            bool replacesStored = row.Track.IsStaged && _song.StoredFilesOf(slot).Count > 0 && _song.Media.ContainsKey(slot);
            Execute(replacesStored ? MediaCommands.RevertToSaved(_song, slot) : MediaCommands.Remove(_song, slot));
        }

        /// <summary>Keeps a track a pending change removes after all.</summary>
        [RelayCommand]
        private void RestoreTrack(RemovedTrackRow? row)
        {
            if (row?.Stem is { } stem && CanEdit)
                Execute(MediaCommands.RevertToSaved(_song, MediaSlot.Track(stem)));
        }

        partial void OnIsSavingChanged(bool value)
        {
            foreach (var command in _addCommands.Values)
                command.NotifyCanExecuteChanged();
        }

        /// <summary>Asks for an audio file and, once it's checked to be audio, puts it in as <paramref name="stem"/>'s track.</summary>
        private async Task SetTrackAsync(string stem)
        {
            string? path = await _filePicker.PickAudioAsync(TrackStaging.PickTitleFor(stem));
            if (path is null || !CanEdit)
                return;

            Error = null;
            var check = await TrackStaging.CheckAsync(path);
            if (check.Error is { } error)
            {
                Error = error;
                return;
            }

            if (CanEdit)
                Execute(MediaCommands.Set(_song, MediaSlot.Track(stem), path));
        }

        private void Execute(IChartCommand? command)
        {
            if (command is null)
                return;
            Error = null;
            _execute(command);
        }

        private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlaybackViewModel.Tracks))
                RefreshRows();
        }

        private void OnSongChanged(object? sender, EventArgs e) => Refresh();

        private void OnSongTracksChanged(object? sender, EventArgs e) => RefreshRows();

        /// <summary>Shows the rows of the tracks played that the song still has, when they've changed.</summary>
        private void RefreshRows()
        {
            var rows = RowsNow();
            if (rows.SequenceEqual(_rows))
                return;
            _rows = rows;
            OnPropertyChanged(nameof(Tracks));
            _ = ShowSizesAsync();
        }

        // By stem, so a track replaced or saved keeps its row until the audio is opened again with the one in its place.
        private List<TrackRowViewModel> RowsNow()
        {
            var stems = _song.Tracks.Select(t => t.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. _playback.Tracks.Where(r => stems.Contains(r.Track.Stem))];
        }

        /// <summary>Lists the tracks removed, as the song's tracks are now.</summary>
        private void Refresh()
        {
            if (!RemovedNames().SequenceEqual(RemovedTracks.Select(r => r.Name)))
                _ = ShowSizesAsync();
        }

        // The song's stored tracks a pending change removes, of stems it puts nothing in place of.
        private List<string> RemovedNames()
        {
            var stems = _song.Tracks.Select(t => t.Stem).ToHashSet(StringComparer.Ordinal);
            return [.. _song.Media
                .Where(m => m.Key.Kind == MediaKind.Track && !stems.Contains(m.Key.Stem!))
                .SelectMany(m => _song.StoredFilesOf(m.Key))
                .OrderBy(n => AudioStems.OrderOf(AudioStems.StemOf(n) ?? ""))
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)];
        }

        /// <summary>Shows each track's size, and the removed tracks with theirs, read off the UI thread.</summary>
        private async Task ShowSizesAsync()
        {
            _sizesCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            _sizesCts = cts;
            var opened = _song.Opened;
            var rows = Tracks;
            var removed = RemovedNames();

            IReadOnlyDictionary<string, long> sizes;
            try
            {
                var staged = rows.Select(r => r.Track.StagedSourcePath).OfType<string>().ToList();
                sizes = await Task.Run(() => SongFileSizes.Of(opened, staged, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cts.IsCancellationRequested)
                return;

            string? SizeOf(string key) => sizes.TryGetValue(key, out long size) ? ByteSizeFormatter.Format((ulong)size) : null;
            foreach (var row in rows)
                row.Size = SizeOf(row.Track.StagedSourcePath ?? row.Track.StoredName);
            RemovedTracks = [.. removed.Select(n => new RemovedTrackRow(n, SizeOf(n) ?? "-"))];
        }
    }
}
