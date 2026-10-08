using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChartPacker.Services;
using ChartPacker.UI.Views;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Validator window. Closing it keeps any unsaved edits: they're drafts of the songs, which the main window
    /// shows and saves too.
    /// </summary>
    public partial class ValidatorWindow : Window
    {
        // The song under the pointer when it was pressed was already the picked one, so a click on it opens or
        // closes its issues rather than only picking it.
        private ValidatorSongViewModel? _pressedPicked;

        private ColumnDefinition SongsColumn => ValidatorRoot.ColumnDefinitions[0];

        private ColumnDefinition InspectorColumn => ValidatorRoot.ColumnDefinitions[2];

        public ValidatorWindow()
        {
            InitializeComponent();
            // The splitter position is view state, so it's applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                RestoreWindowState();
            };

            // Tunnelling, so it's seen before the list picks the song pressed.
            SongList.AddHandler(PointerPressedEvent, OnSongListPointerPressed, RoutingStrategies.Tunnel);
            SongList.AddHandler(TappedEvent, OnSongListTapped);
            SongList.AddHandler(KeyDownEvent, OnSongListKeyDown);
        }

        private void OnSongListPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var song = SongAt(e.Source);
            _pressedPicked = song is not null && ReferenceEquals(song, (DataContext as ValidatorWindowViewModel)?.SelectedSong) ? song : null;
        }

        /// <summary>
        /// A click on a song's lines, outside its box, opens or closes its issues once it's picked; the first click
        /// only picks it, which opens them. Clicks in the open list of issues leave it as it is.
        /// </summary>
        private void OnSongListTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not ValidatorWindowViewModel viewModel || !IsOnSongHeader(e.Source))
                return;

            var song = SongAt(e.Source);
            if (song is not null && ReferenceEquals(song, _pressedPicked))
                viewModel.ToggleExpanded(song);
        }

        private void OnSongListKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || DataContext is not ValidatorWindowViewModel { SelectedSong: { } song } viewModel)
                return;

            viewModel.ToggleExpanded(song);
            e.Handled = true;
        }

        private static ValidatorSongViewModel? SongAt(object? source) =>
            (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ValidatorSongViewModel;

        // On the song's own lines, not its check box or its list of issues.
        private static bool IsOnSongHeader(object? source)
        {
            for (var visual = source as Visual; visual is not null and not ListBoxItem; visual = visual.GetVisualParent())
            {
                if (visual is CheckBox)
                    return false;

                if (visual is Control control && control.Classes.Contains("song-header"))
                    return true;
            }

            return false;
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (DataContext is ValidatorWindowViewModel viewModel)
                CaptureWindowState(viewModel);

            base.OnClosing(e);
        }

        private void CaptureWindowState(ValidatorWindowViewModel viewModel)
        {
            double left = SongsColumn.ActualWidth;
            double right = InspectorColumn.ActualWidth;
            double total = left + right;
            if (!double.IsFinite(total) || total <= 0)
                return;

            double fraction = left / total;
            if (double.IsFinite(fraction))
                viewModel.ValidatorListFraction = Math.Clamp(fraction, AppSettings.MinValidatorListFraction, AppSettings.MaxValidatorListFraction);
        }

        private void RestoreWindowState()
        {
            if (DataContext is not ValidatorWindowViewModel viewModel)
                return;

            double fraction = viewModel.ValidatorListFraction;
            if (!double.IsFinite(fraction))
                return;

            fraction = Math.Clamp(fraction, AppSettings.MinValidatorListFraction, AppSettings.MaxValidatorListFraction);
            SongsColumn.Width = new GridLength(fraction, GridUnitType.Star);
            InspectorColumn.Width = new GridLength(1 - fraction, GridUnitType.Star);
        }
    }
}
