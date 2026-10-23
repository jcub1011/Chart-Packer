using Avalonia;
using Avalonia.Controls;
using ChartBuilder.ViewModels;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The difficulty analysis, in the Editor's side panel or its own window. While it's shown, the analysis is kept up to
    /// date; a scrub on the graph moves playback there.
    /// </summary>
    public partial class DifficultyView : UserControl
    {
        public static readonly StyledProperty<bool> CanPopOutProperty =
            AvaloniaProperty.Register<DifficultyView, bool>(nameof(CanPopOut), true);

        private DifficultyViewModel? _shownFor;

        public DifficultyView()
        {
            InitializeComponent();
        }

        /// <summary>Whether it offers to open the analysis in a window of its own: not when it's in that window.</summary>
        public bool CanPopOut
        {
            get => GetValue(CanPopOutProperty);
            set => SetValue(CanPopOutProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == CanPopOutProperty)
                PopOutButton.IsVisible = change.GetNewValue<bool>();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Show(DataContext as DifficultyViewModel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Show(null);
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (TopLevel.GetTopLevel(this) is not null)
                Show(DataContext as DifficultyViewModel);
        }

        // Tells the view model it's shown here, or no longer, so it's analyzed only while something shows it.
        private void Show(DifficultyViewModel? difficulty)
        {
            if (ReferenceEquals(difficulty, _shownFor))
                return;
            _shownFor?.SetShown(false);
            _shownFor = difficulty;
            difficulty?.SetShown(true);
        }
    }
}
