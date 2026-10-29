using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ChartBuilder.Controls
{
    /// <summary>One of the Editor's lanes, for an <see cref="ViewModels.EditorLaneViewModel"/>. Its look is in EditorLane.axaml.</summary>
    public partial class EditorLane : UserControl
    {
        // How much wider than a layout needs the header's room has to grow, in pixels, before it comes back in place of
        // a narrower one, so a width on the edge doesn't flip between them.
        private const double LettersHysteresis = 8;

        // How wide the difficulty letters are, as last measured while they were shown. Starts at roughly four
        // buttons, so a narrow lane starts on the drop-down instead of flashing the letters first.
        private double _lettersWidth = 90;

        // How wide the difficulty drop-down is, as last measured while it was shown; its style fixes it at 60.
        private double _pickerWidth = 60;

        // How wide the tools that fold into the "more" menu are, as last measured while they were shown. Starts at
        // roughly four buttons, as the letters do.
        private double _toolsWidth = 112;

        public EditorLane()
        {
            InitializeComponent();
            HeaderGrid.SizeChanged += (_, _) => FitHeader();
            PartButton.SizeChanged += (_, _) => FitHeader();
            LaneTools.SizeChanged += (_, _) => FitHeader();
            DifficultyLetters.SizeChanged += (_, _) => FitHeader();
            DifficultyPicker.SizeChanged += (_, _) => FitHeader();
            OverflowTools.SizeChanged += (_, _) => FitHeader();
            MoreHost.SizeChanged += (_, _) => FitHeader();
        }

        private ViewModels.EditorLaneViewModel? _lane;
        private ViewModels.ChartEditorViewModel? _editor;

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            Unwatch();
            _lane = DataContext as ViewModels.EditorLaneViewModel;
            Watch();
        }

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Watch();
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Unwatch();
        }

        // The flyout follows which lane the keys act on, and whether it's open.
        private void Watch()
        {
            if (_editor is not null || VisualRoot is null || _lane?.Editor is not { } editor)
                return;
            _editor = editor;
            editor.Session.StateChanged += OnNavigationState;
            editor.Navigation.PropertyChanged += OnNavigationPropertyChanged;
            _lane.PropertyChanged += OnLaneViewChanged;
            ShowNavigation();
        }

        private void Unwatch()
        {
            if (_editor is null)
                return;
            _editor.Session.StateChanged -= OnNavigationState;
            _editor.Navigation.PropertyChanged -= OnNavigationPropertyChanged;
            if (_lane is not null)
                _lane.PropertyChanged -= OnLaneViewChanged;
            _editor = null;
            Navigation.IsVisible = false;
        }

        // Switching the lane between the highway and the timeline turns the flyout's arrows; the lane getting a canvas, or
        // being pointed at another track or part, changes whether the flyout is over the lane the keys act on.
        private void OnLaneViewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ViewModels.EditorLaneViewModel.View) or nameof(ViewModels.EditorLaneViewModel.HasCanvas)
                or nameof(ViewModels.EditorLaneViewModel.Track) or nameof(ViewModels.EditorLaneViewModel.Vocal)
                or nameof(ViewModels.EditorLaneViewModel.IsVocal))
                ShowNavigation();
        }

        private void OnNavigationState(object? sender, System.EventArgs e) => ShowNavigation();

        private void OnNavigationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.NavigationFlyoutViewModel.IsOpen))
                ShowNavigation();
        }

        private void ShowNavigation()
        {
            if (_editor is not { } editor || _lane is not { HasCanvas: true } lane)
            {
                Navigation.IsVisible = false;
                return;
            }

            var session = editor.Session;
            bool focused = lane.IsVocal ? session.FocusedVocal is { } vocal && vocal == lane.Vocal
                : session.FocusedVocal is null && session.Focused is { } key && key == lane.Track;
            Navigation.IsVisible = editor.Navigation.IsOpen && focused;
            Navigation.IsTimeline = lane.IsTimeline;
        }

        /// <summary>The header, which <see cref="LaneDockHost"/> drags the lane by.</summary>
        public Control DragHandle => Header;

        /// <summary>
        /// Fits the header to the lane's width, widest first: the difficulty letters and every tool; a drop-down in the
        /// letters' place; then the audio, edit, ghost and view tools folded into the "more" menu as well.
        /// </summary>
        private void FitHeader()
        {
            if (DifficultyLetters.IsVisible && DifficultyLetters.Bounds.Width > 0)
                _lettersWidth = DifficultyLetters.Bounds.Width;
            if (DifficultyPicker.IsVisible && DifficultyPicker.Bounds.Width > 0)
                _pickerWidth = DifficultyPicker.Bounds.Width;
            if (OverflowTools.IsVisible && OverflowTools.Bounds.Width > 0)
                _toolsWidth = OverflowTools.Bounds.Width;

            // The room left for the difficulties and the folding tools, beside the part and the tools that stay.
            var folding = OverflowTools.IsVisible ? OverflowTools : (Control)MoreHost;
            var area = DifficultyLetters.Parent as Control;
            double room = HeaderGrid.Bounds.Width - PartButton.Bounds.Width - (LaneTools.Bounds.Width - folding.Bounds.Width)
                - (area?.Margin.Left ?? 0);

            int current = DifficultyLetters.IsVisible ? 0 : OverflowTools.IsVisible ? 1 : 2;
            int fit = Fits(0, _lettersWidth + _toolsWidth) ? 0 : Fits(1, _pickerWidth + _toolsWidth) ? 1 : 2;

            DifficultyLetters.IsVisible = fit == 0;
            DifficultyPicker.IsVisible = fit > 0 && DifficultyLetters.ItemCount > 0;
            OverflowTools.IsVisible = fit < 2;
            MoreHost.IsVisible = fit == 2;

            // A layout no wider than the current one needs only its room; a wider one needs a little more.
            bool Fits(int layout, double needs) => room >= needs + (layout < current ? LettersHysteresis : 0);
        }

        /// <summary>Opens the lane's audio settings from the "more" menu, once the menu has closed.</summary>
        private void OnAudioMenuClick(object? sender, RoutedEventArgs e)
        {
            if (Resources["AudioFlyout"] is Flyout flyout)
                Dispatcher.UIThread.Post(() => flyout.ShowAt(MoreButton));
        }
    }
}
