using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChartShared.UI.ViewModels;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// A song's audio tracks with their switches and volumes (<see cref="TrackRowViewModel"/>), the tracks a staged change
    /// removes, and Add track: in Chart Packer's inspector and Chart Builder's Chart settings. The view around it binds
    /// each property; the commands are given the row they're for, or for Add track, the stem.
    /// </summary>
    public partial class TrackListView : UserControl
    {
        public static readonly StyledProperty<IEnumerable<TrackRowViewModel>?> TracksProperty =
            AvaloniaProperty.Register<TrackListView, IEnumerable<TrackRowViewModel>?>(nameof(Tracks));

        public static readonly StyledProperty<IEnumerable<RemovedTrackRow>?> RemovedTracksProperty =
            AvaloniaProperty.Register<TrackListView, IEnumerable<RemovedTrackRow>?>(nameof(RemovedTracks));

        public static readonly StyledProperty<IEnumerable<StemOption>?> AddTrackOptionsProperty =
            AvaloniaProperty.Register<TrackListView, IEnumerable<StemOption>?>(nameof(AddTrackOptions));

        public static readonly StyledProperty<bool> CanEditProperty =
            AvaloniaProperty.Register<TrackListView, bool>(nameof(CanEdit));

        public static readonly StyledProperty<bool> ActionsInContextMenuProperty =
            AvaloniaProperty.Register<TrackListView, bool>(nameof(ActionsInContextMenu));

        public static readonly StyledProperty<bool> ShowWaveformsProperty =
            AvaloniaProperty.Register<TrackListView, bool>(nameof(ShowWaveforms), true);

        public static readonly StyledProperty<double> PositionSecondsProperty =
            AvaloniaProperty.Register<TrackListView, double>(nameof(PositionSeconds));

        public static readonly StyledProperty<double> LengthSecondsProperty =
            AvaloniaProperty.Register<TrackListView, double>(nameof(LengthSeconds));

        public static readonly StyledProperty<ICommand?> ReplaceTrackCommandProperty =
            AvaloniaProperty.Register<TrackListView, ICommand?>(nameof(ReplaceTrackCommand));

        public static readonly StyledProperty<ICommand?> RemoveTrackCommandProperty =
            AvaloniaProperty.Register<TrackListView, ICommand?>(nameof(RemoveTrackCommand));

        public static readonly StyledProperty<ICommand?> RestoreTrackCommandProperty =
            AvaloniaProperty.Register<TrackListView, ICommand?>(nameof(RestoreTrackCommand));

        public static readonly DirectProperty<TrackListView, bool> ShowsActionButtonsProperty =
            AvaloniaProperty.RegisterDirect<TrackListView, bool>(nameof(ShowsActionButtons), o => o.ShowsActionButtons);

        // Narrower than this, a row's waveform and volume go on a second line; wider than the other, back on one. The gap
        // keeps a width near the edge, as a scroll bar coming and going moves it, from switching back and forth.
        private const double CompactBelow = 480;
        private const double WideFrom = 500;

        private bool _showsActionButtons;

        public TrackListView()
        {
            InitializeComponent();
            AddHandler(ContextRequestedEvent, OnRowContextRequested, RoutingStrategies.Tunnel);
        }

        /// <summary>The tracks, a row each.</summary>
        public IEnumerable<TrackRowViewModel>? Tracks
        {
            get => GetValue(TracksProperty);
            set => SetValue(TracksProperty, value);
        }

        /// <summary>The tracks a staged change removes, struck through, each with a way to keep it.</summary>
        public IEnumerable<RemovedTrackRow>? RemovedTracks
        {
            get => GetValue(RemovedTracksProperty);
            set => SetValue(RemovedTracksProperty, value);
        }

        /// <summary>The Add track menu: a stem each.</summary>
        public IEnumerable<StemOption>? AddTrackOptions
        {
            get => GetValue(AddTrackOptionsProperty);
            set => SetValue(AddTrackOptionsProperty, value);
        }

        /// <summary>Whether tracks can be added, replaced and removed now.</summary>
        public bool CanEdit
        {
            get => GetValue(CanEditProperty);
            set => SetValue(CanEditProperty, value);
        }

        /// <summary>Whether Replace, Remove and Keep are on each row's right-click menu rather than buttons on it.</summary>
        public bool ActionsInContextMenu
        {
            get => GetValue(ActionsInContextMenuProperty);
            set => SetValue(ActionsInContextMenuProperty, value);
        }

        /// <summary>Whether each track's waveform is shown.</summary>
        public bool ShowWaveforms
        {
            get => GetValue(ShowWaveformsProperty);
            set => SetValue(ShowWaveformsProperty, value);
        }

        /// <summary>Where playback is, in seconds, which the waveforms show as played.</summary>
        public double PositionSeconds
        {
            get => GetValue(PositionSecondsProperty);
            set => SetValue(PositionSecondsProperty, value);
        }

        /// <summary>How long the waveforms' time axis is, in seconds: the song's.</summary>
        public double LengthSeconds
        {
            get => GetValue(LengthSecondsProperty);
            set => SetValue(LengthSecondsProperty, value);
        }

        /// <summary>Replaces a row's track; given the row.</summary>
        public ICommand? ReplaceTrackCommand
        {
            get => GetValue(ReplaceTrackCommandProperty);
            set => SetValue(ReplaceTrackCommandProperty, value);
        }

        /// <summary>Removes a row's track; given the row.</summary>
        public ICommand? RemoveTrackCommand
        {
            get => GetValue(RemoveTrackCommandProperty);
            set => SetValue(RemoveTrackCommandProperty, value);
        }

        /// <summary>Keeps a removed track after all; given its <see cref="RemovedTrackRow"/>.</summary>
        public ICommand? RestoreTrackCommand
        {
            get => GetValue(RestoreTrackCommandProperty);
            set => SetValue(RestoreTrackCommandProperty, value);
        }

        /// <summary>Whether the rows show Replace, Remove and Keep as buttons: while they can be edited, and not on their menus.</summary>
        public bool ShowsActionButtons
        {
            get => _showsActionButtons;
            private set => SetAndRaise(ShowsActionButtonsProperty, ref _showsActionButtons, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == CanEditProperty || change.Property == ActionsInContextMenuProperty)
            {
                ShowsActionButtons = CanEdit && !ActionsInContextMenu;

                // A row with its actions on its right-click menu lights up as it's hovered, and says so.
                Classes.Set("menuActions", CanEdit && ActionsInContextMenu);
            }
            else if (change.Property == AddTrackOptionsProperty && AddButton?.Flyout is MenuFlyout flyout)
                flyout.ItemsSource = AddTrackOptions;
        }

        /// <summary>Puts each row's waveform and volume on a second line while the list is too narrow for one.</summary>
        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            double width = e.NewSize.Width;
            if (width < CompactBelow)
                Classes.Set("compact", true);
            else if (width >= WideFrom)
                Classes.Set("compact", false);
        }

        // The menu is a popup of its own, out of reach of bindings to this control, so its actions are set as a row asks for
        // it, on the way down, before the menu opens. Not as it opens: the first time it opens it isn't yet in the row's
        // tree, so it hasn't the row as its DataContext, and its actions would do nothing.
        private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.ContextMenu is not null) is not { ContextMenu: { } menu } border)
                return;
            if (border.DataContext is TrackRowViewModel row)
                SetUpMenu(menu, row, ("replace", ReplaceTrackCommand), ("remove", RemoveTrackCommand));
            else if (border.DataContext is RemovedTrackRow removed)
                SetUpMenu(menu, removed, ("restore", RestoreTrackCommand));
        }

        // A menu with nothing on it isn't shown at all.
        private void OnRowMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (sender is ContextMenu menu && !menu.Items.OfType<MenuItem>().Any(i => i.IsVisible))
                e.Cancel = true;
        }

        private void SetUpMenu(ContextMenu menu, object row, params (string Tag, ICommand? Command)[] actions)
        {
            bool shown = CanEdit && ActionsInContextMenu;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Tag is not string tag || actions.FirstOrDefault(a => a.Tag == tag) is not { Tag: not null } action)
                    continue;
                item.Command = action.Command;
                item.CommandParameter = row;
                item.IsVisible = shown && action.Command is not null;
            }
        }
    }
}
