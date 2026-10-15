using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.Core.Scanning;
using ChartShared.UI;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly SettingsService _settings;
        private readonly MetadataDraftStore _drafts;
        private readonly IConfirmationService _confirmation;
        private readonly ISettingsWindowService _settingsWindow;

        public MainWindowViewModel(
            LibraryViewModel library,
            SongDetailViewModel detail,
            SettingsService settings,
            MetadataDraftStore drafts,
            IConfirmationService confirmation,
            ISettingsWindowService settingsWindow,
            NoticeViewModel notice)
        {
            Library = library;
            Detail = detail;
            _settings = settings;
            _drafts = drafts;
            _confirmation = confirmation;
            _settingsWindow = settingsWindow;
            Notice = notice;
            StatusBar = new StatusBarViewModel(library);

            Library.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LibraryViewModel.SelectedEntries))
                    _ = Detail.ShowAsync(Library.SelectedEntries);
            };
            Library.EditedSongsChanged += (_, changed) => _ = RefreshDetailForChangedAsync(changed);
            Library.PrepareEditChart = PrepareEditChartAsync;
        }

        /// <summary>
        /// Called as the window is activated, as when the user comes back from Chart Builder: re-reads the songs opened
        /// in it that have changed, and the inspector's, if it's showing one of them, and the audio latency its
        /// calibration tool measures (charter-spec §4.8). Never throws. Songs Chart Builder saves while both windows
        /// show are also picked up by the library's background poller.
        /// </summary>
        public async Task OnActivatedAsync()
        {
            // Chart Builder's calibration tool may have changed the latency since (charter-spec §4.8).
            Detail.Player.RefreshLatency();

            try
            {
                var changed = await Library.RefreshEditedSongsAsync();
                await RefreshDetailForChangedAsync(changed);
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would be lost.
                Trace.TraceError($"Re-reading the songs Chart Builder changed failed: {ex}");
            }
        }

        /// <summary>
        /// Re-shows the inspector when it displays one of the <paramref name="changed"/> songs Chart Builder saved.
        /// Unsaved changes in the inspector are kept as they are rather than shown over. Never throws.
        /// </summary>
        private async Task RefreshDetailForChangedAsync(IReadOnlyList<SongEntry> changed)
        {
            try
            {
                var shown = Library.SelectedEntries;
                bool showsChanged = changed.Any(c => shown.Any(s => s.FullPath == c.FullPath));

                // Unsaved changes in the inspector are kept as they are rather than shown over.
                if (showsChanged && !Detail.HasDrafts && !Detail.IsSaving)
                    await Detail.ShowAsync(shown, inPlace: true);
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would be lost.
                Trace.TraceError($"Re-reading the songs Chart Builder changed failed: {ex}");
            }
        }

        /// <summary>
        /// Before <paramref name="song"/> opens in Chart Builder, which opens it as it is on disk: saves or discards its
        /// unsaved metadata changes, as the user chooses (charter-spec C2 item 4).
        /// </summary>
        /// <returns>Whether to open it.</returns>
        private async Task<bool> PrepareEditChartAsync(SongEntry song)
        {
            if (!_drafts.HasDrafts(song.FullPath))
                return true;

            var choice = await _confirmation.ChooseAsync(
                Strings.EditChartUnsavedTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.EditChartUnsavedMessageFormat, song.DisplayName),
                Strings.SaveButton,
                Strings.DiscardButton);
            switch (choice)
            {
                case ConfirmChoice.First:
                    // The inspector shows the one song selected, so its Save saves it, asking as it does about invalid
                    // metadata; a save that fails, or is called off, leaves the drafts.
                    if (Detail.SaveCommand.CanExecute(null))
                        await Detail.SaveCommand.ExecuteAsync(null);
                    return !_drafts.HasDrafts(song.FullPath);

                case ConfirmChoice.Second:
                    if (Detail.DiscardCommand.CanExecute(null))
                        Detail.DiscardCommand.Execute(null);
                    if (_drafts.HasDrafts(song.FullPath))
                    {
                        _drafts.Get(song.FullPath).Clear();
                        _drafts.NotifyChanged(song.FullPath);
                    }

                    return true;

                default:
                    return false;
            }
        }

        /// <summary>The window title, with the program version from the project file.</summary>
        public string Title { get; } = string.Format(CultureInfo.CurrentCulture, Strings.MainWindowTitleFormat, AppVersion.Semantic);

        public LibraryViewModel Library { get; }

        public SongDetailViewModel Detail { get; }

        /// <summary>What the latest download did, shown along the bottom for a few seconds.</summary>
        public NoticeViewModel Notice { get; }

        /// <summary>The status bar, always shown under both panels: the pack/unpack job, or the library's scan.</summary>
        public StatusBarViewModel StatusBar { get; }

        /// <summary>Persisted width of the library panel. The view applies it on open and writes it back on close.</summary>
        public double LibraryPaneWidth
        {
            get => _settings.Settings.LibraryPaneWidth;
            set => _settings.Settings.LibraryPaneWidth = value;
        }

        [RelayCommand]
        private Task OpenSettingsAsync() => _settingsWindow.ShowAsync();

        /// <summary>Some song has metadata changes that closing would lose.</summary>
        public bool HasUnsavedDrafts => _drafts.DirtyCount > 0;

        /// <summary>Asks whether to close and lose the unsaved metadata changes.</summary>
        public Task<bool> ConfirmDiscardDraftsAsync()
        {
            int count = _drafts.DirtyCount;
            string message = count == 1
                ? Strings.CloseUnsavedOne
                : string.Format(CultureInfo.CurrentCulture, Strings.CloseUnsavedFormat, count);
            return _confirmation.ConfirmAsync(Strings.CloseUnsavedTitle, message, Strings.DiscardAndCloseButton);
        }
    }
}
