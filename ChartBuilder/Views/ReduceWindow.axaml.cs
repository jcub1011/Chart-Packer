using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ChartBuilder.ViewModels;
using ChartShared.UI.Services;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The Reduce dialog (charter-spec C9 item 2). It closes once its view model is done; closed any other way, a run is
    /// cancelled, adding nothing. It asks, over itself, before Reduce all replaces a difficulty with notes. On the defaults
    /// in Settings there's no draft to show, so it's the rules alone, narrower.
    /// </summary>
    /// <remarks>
    /// Showing the preview widens the window by as much as the preview takes, as far as the screen allows, and hiding it
    /// narrows it again. While it's shown, Space plays or pauses and N goes to the next change (Shift+N the one before),
    /// tunnelled so a focused button or box takes them as that, but not while typing in a field.
    /// </remarks>
    public partial class ReduceWindow : Window
    {
        // How much wider the window grows for the preview: two highways side by side, or Reduce all's four.
        private const double PreviewWidth = 560;
        private const double PreviewWidthAll = 740;

        // The narrowest the window, the draft's summary and the preview go while the preview is shown.
        private const double MinWidthWithPreview = 1000;
        private const double MinHeightWithPreview = 600;
        private const double SummaryWidth = 360;
        private const double MinPreviewWidth = 360;

        private ReducePreviewViewModel? _preview;
        private bool _isOpen;
        private bool _isSpaceHeld;

        // Whether the window's size allows for the preview, and how much wider it was made for it, or the preview took.
        private bool _isSizedForPreview;
        private double _previewTook;

        public ReduceWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                _isOpen = true;

                // Sized for the preview before it was shown, so it's centred on its owner as a whole; once it has a frame, it's
                // made to fit its screen too.
                Dispatcher.UIThread.Post(() => FitOnScreen(null), DispatcherPriority.Loaded);
            };
            Closed += (_, _) => (DataContext as ReduceViewModel)?.Cancel();
            Highway.Scrubbed += (_, e) => _preview?.SeekTo(e.Seconds);

            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
            Deactivated += (_, _) => _isSpaceHeld = false;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_preview is not null)
                _preview.PropertyChanged -= OnPreviewChanged;
            _preview = null;

            if (DataContext is ReduceViewModel vm)
            {
                vm.Done += (_, _) => Close();
                vm.Confirmations = new AvaloniaConfirmationService(() => this);
                if (vm.IsDefaults)
                {
                    Body.ColumnDefinitions[0].Width = GridLength.Star;
                    SetupPane.BorderThickness = default;
                    MinWidth = 380;
                    Width = 420;
                    Height = 560;
                }

                _preview = vm.Preview;
                if (_preview is not null)
                    _preview.PropertyChanged += OnPreviewChanged;
            }

            ApplyPreview();
        }

        private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReducePreviewViewModel.IsExpanded))
                ApplyPreview();
        }

        /// <summary>
        /// Shows the preview's columns or hides them, as the view model says, and the window grows or shrinks by as much:
        /// before it's shown, so it opens centred at its whole width; once it's open, about its middle, kept on its screen.
        /// </summary>
        private void ApplyPreview()
        {
            bool shown = _preview is { IsExpanded: true };
            if (shown == PreviewPane.IsVisible)
                return;

            var columns = Body.ColumnDefinitions;
            if (PreviewPane.IsVisible && WindowState == WindowState.Normal)
                _previewTook = PreviewPane.Bounds.Width + PreviewSplitter.Bounds.Width;
            PreviewPane.IsVisible = shown;
            PreviewSplitter.IsVisible = shown;
            if (shown)
            {
                columns[1].Width = new GridLength(SummaryWidth);
                columns[1].MinWidth = 260;
                columns[3].Width = GridLength.Star;
                columns[3].MinWidth = MinPreviewWidth;
            }
            else
            {
                columns[1].Width = GridLength.Star;
                columns[1].MinWidth = 0;
                columns[3].Width = new GridLength(0);
                columns[3].MinWidth = 0;
            }

            SizeForPreview();
        }

        /// <summary>
        /// Grows the window by as much as the preview takes, or shrinks it again, if it isn't already: before it's shown, so
        /// it opens centred at its whole width; once it's open, about its middle, kept on its screen. Maximized or full
        /// screen it's left as it is, and sized when it's back to its own size.
        /// </summary>
        private void SizeForPreview()
        {
            bool shown = PreviewPane.IsVisible;
            if (shown == _isSizedForPreview || (_isOpen && WindowState != WindowState.Normal))
                return;

            _isSizedForPreview = shown;
            var center = _isOpen ? WindowPlacement.CenterOf(this) : null;
            if (shown)
            {
                bool all = DataContext is ReduceViewModel { IsAll: true };
                _previewTook = all ? PreviewWidthAll : PreviewWidth;
                MinWidth = MinWidthWithPreview;
                Width += _previewTook;
                Height = Math.Max(Height, MinHeightWithPreview);
            }
            else
            {
                MinWidth = 660;
                Width = Math.Max(MinWidth, Width - _previewTook);
            }

            if (_isOpen)
                FitOnScreen(center);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // Back to its own size, the window takes in the preview shown or hidden while it was maximized or full screen.
            if (change.Property == WindowStateProperty && WindowState == WindowState.Normal && _isOpen)
                Dispatcher.UIThread.Post(SizeForPreview, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// The window centred on <paramref name="center"/>, or on where it is, its size made to fit its screen's working area,
        /// and moved as little as keeps it there.
        /// </summary>
        private void FitOnScreen(PixelPoint? center)
        {
            if (WindowState != WindowState.Normal
                || (center ?? WindowPlacement.CenterOf(this)) is not { } middle
                || (WindowPlacement.ScreenOf(this, middle) ?? Screens.ScreenFromWindow(this)) is not { } screen)
                return;

            double scaling = screen.Scaling;
            var area = screen.WorkingArea;
            var size = WindowPlacement.FitSize(
                PixelSize.FromSize(new Size(Width, Height), scaling),
                PixelSize.FromSize(new Size(MinWidth, MinHeight), scaling),
                area);
            Width = size.Width / scaling;
            Height = size.Height / scaling;
            Position = WindowPlacement.Centered(middle, size, area);
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (_preview is not { IsExpanded: true } preview || IsTyping(e))
                return;

            if (e.Key == Key.N && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            {
                e.Handled = true;
                if (e.KeyModifiers == KeyModifiers.Shift)
                    preview.PreviousChangeCommand.Execute(null);
                else
                    preview.NextChangeCommand.Execute(null);
                return;
            }

            if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None)
                return;
            e.Handled = true;
            if (_isSpaceHeld)
                return;
            _isSpaceHeld = true;
            preview.PlayPauseCommand.Execute(null);
        }

        // The key-up of a Space taken as playback is taken too: a focused button clicks as Space is let go.
        private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || !_isSpaceHeld)
                return;

            _isSpaceHeld = false;
            e.Handled = true;
        }

        // A field takes what's typed: a text box, or a number box's.
        private bool IsTyping(KeyEventArgs e) => e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox;
    }
}
