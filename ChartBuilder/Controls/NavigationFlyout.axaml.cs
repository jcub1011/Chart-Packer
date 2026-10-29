using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using ChartBuilder.ViewModels;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// Go to measure and Find, over the top right of the focused lane, for a <see cref="NavigationFlyoutViewModel"/>. Its
    /// look is in NavigationFlyout.axaml. Enter in the measure box goes; Esc closes it.
    /// </summary>
    public partial class NavigationFlyout : UserControl
    {
        /// <summary>
        /// Whether the lane it's over runs time left to right (the timeline) rather than up the screen (the highway): the
        /// next match's arrow points the way later is drawn, and the previous match's the way earlier is.
        /// </summary>
        public static readonly StyledProperty<bool> IsTimelineProperty =
            AvaloniaProperty.Register<NavigationFlyout, bool>(nameof(IsTimeline));

        private NavigationFlyoutViewModel? _viewModel;

        public bool IsTimeline
        {
            get => GetValue(IsTimelineProperty);
            set => SetValue(IsTimelineProperty, value);
        }

        static NavigationFlyout()
        {
            IsTimelineProperty.Changed.AddClassHandler<NavigationFlyout>((flyout, _) => flyout.PointArrows());
        }

        public NavigationFlyout()
        {
            InitializeComponent();
            PointArrows();
            MeasureBox.KeyDown += OnMeasureKeyDown;
            KeyDown += OnKeyDown;
        }

        // The chevrons point down; turned to up and down for the highway, where later is up, and to left and right for the timeline.
        private void PointArrows()
        {
            PreviousIcon.RenderTransform = new RotateTransform(IsTimeline ? 90 : 0);
            NextIcon.RenderTransform = new RotateTransform(IsTimeline ? -90 : 180);
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_viewModel is not null)
                _viewModel.MeasureFocusRequested -= OnMeasureFocusRequested;
            _viewModel = DataContext as NavigationFlyoutViewModel;
            if (_viewModel is not null)
                _viewModel.MeasureFocusRequested += OnMeasureFocusRequested;
        }

        // Ctrl+G opens the flyout on the measure, so the number can be typed straight away.
        private void OnMeasureFocusRequested(object? sender, System.EventArgs e)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!IsEffectivelyVisible)
                    return;
                MeasureBox.Focus();
                MeasureBox.SelectAll();
            });
        }

        private void OnMeasureKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _viewModel is not { } vm)
                return;
            e.Handled = true;
            if (vm.GoCommand.CanExecute(null))
                vm.GoCommand.Execute(null);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _viewModel is not { } vm)
                return;
            e.Handled = true;
            vm.CloseCommand.Execute(null);
        }
    }
}
