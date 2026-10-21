using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Library;
using ChartShared.Core.Metadata;
using ChartShared.Core.Validation.QuickFixes;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// Chart settings' Metadata (charter-spec C1): a row per <c>song.ini</c> key, as Chart Packer's inspector edits them,
    /// but each edit an undo step (<see cref="SongCommands"/>). The difficulties are left to Parts.
    /// </summary>
    /// <remarks>
    /// Every edit is a pending change until the song is saved. While it's being saved nothing can be edited, as the save
    /// moves the song on to what it wrote.
    /// </remarks>
    public sealed class ChartMetadataViewModel : ViewModelBase, IMetadataEditorHost, IDisposable
    {
        private readonly EditorSong _song;
        private readonly Action<IChartCommand> _execute;
        private IReadOnlyList<MetadataItem> _items = [];
        private bool _refreshQueued;
        private bool _isSaving;
        private bool _disposed;

        /// <param name="song">The song open.</param>
        /// <param name="execute">Makes an edit, as one undo step.</param>
        public ChartMetadataViewModel(EditorSong song, Action<IChartCommand> execute)
        {
            ArgumentNullException.ThrowIfNull(song);
            ArgumentNullException.ThrowIfNull(execute);
            _song = song;
            _execute = execute;
            MetadataEditor = new MetadataEditorViewModel(this, showDifficulties: false);
            _song.Changed += OnSongChanged;
            Refresh();
        }

        /// <summary>The rows, and the row to add a key.</summary>
        public MetadataEditorViewModel MetadataEditor { get; }

        /// <summary>Whether the song can be edited now: not while it's being saved.</summary>
        public bool CanEdit => !_isSaving;

        /// <summary>Whether a save is running, which the song can't be edited during.</summary>
        public bool IsSaving
        {
            get => _isSaving;
            set
            {
                if (_isSaving == value)
                    return;
                _isSaving = value;
                OnPropertyChanged(nameof(CanEdit));
                MetadataEditor.RefreshCommands();
            }
        }

        bool IMetadataEditorHost.IsBulk => false;

        IReadOnlyList<MetadataItem> IMetadataEditorHost.Items => _items;

        // The difficulties are Parts', so their icons aren't shown and need no note counts or suggestions.
        IReadOnlyDictionary<string, ChartPart>? IMetadataEditorHost.ChartParts => null;

        IReadOnlyDictionary<string, int>? IMetadataEditorHost.SuggestedRatings => null;

        string? IMetadataEditorHost.LoadNote => null;

        // song_length follows the audio on its own (charter-spec C17 item 2).
        IRelayCommand? IMetadataEditorHost.AutoSetSongLengthCommand => null;

        bool IMetadataEditorHost.CanEditKey(string key) => _song.CanEdit(key);

        public void SetValue(string key, string value) => Execute(SongCommands.SetValue(_song, key, value));

        public void RemoveKey(string key) => Execute(SongCommands.Remove(_song, key));

        /// <summary>Puts <paramref name="key"/> back as the song has it saved.</summary>
        public void RevertKey(string key) => Execute(SongCommands.RevertToSaved(_song, key));

        /// <summary>Unsets the difficulty <paramref name="key"/>: <c>-1</c> where the song has one or will, and nothing otherwise.</summary>
        public void UnsetDifficulty(string key)
        {
            if (_song.EffectiveValue(key) is not null)
                Execute(SongCommands.SetDifficulty(_song, key, null));
        }

        /// <summary>
        /// Applies every quick fix for <paramref name="key"/>, worked out on a copy of the song's metadata as the next save
        /// leaves it, as one undo step: whatever the fixes changed.
        /// </summary>
        public void ApplyQuickFixes(string key)
        {
            var before = _song.EffectivePairs();
            var item = new MetadataItem(before, new MetadataDraft(), DuplicateKeysOf(before));
            if (QuickFixRegistry.FixKey(item, key) == 0)
                return;
            Execute(SongCommands.SetValues(_song, item.Draft.Changes.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase)));
        }

        public void Dispose()
        {
            _disposed = true;
            _song.Changed -= OnSongChanged;
        }

        private void Execute(IChartCommand? command)
        {
            if (command is not null && CanEdit)
                _execute(command);
        }

        private void OnSongChanged(object? sender, EventArgs e)
        {
            // A burst of changes, as a recovered song's are, is shown once.
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            Dispatcher.UIThread.Post(
                () =>
                {
                    _refreshQueued = false;
                    if (!_disposed)
                        Refresh();
                },
                DispatcherPriority.Background);
        }

        /// <summary>Shows the song's metadata as the next save leaves it, each row marked with what the save does to it.</summary>
        private void Refresh()
        {
            _items = [new MetadataItem(_song.Pairs, DraftOf(_song.PendingChanges()), DuplicateKeysOf(_song.Pairs))];
            MetadataEditor.Refresh();
        }

        // The pending changes as a draft over the song.ini as opened, so each row shows what the next save does to it.
        private static MetadataDraft DraftOf(IEnumerable<PendingSongIniChange> changes)
        {
            var draft = new MetadataDraft();
            foreach (var change in changes)
            {
                if (change.Value is { } value)
                    draft.Set(change.Key, value);
                else
                    draft.Remove(change.Key);
            }

            return draft;
        }

        // The keys song.ini has more than once, which validation marks.
        private static IReadOnlyCollection<string> DuplicateKeysOf(IReadOnlyList<KeyValuePair<string, string>> pairs) =>
            [.. pairs.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)];
    }
}
