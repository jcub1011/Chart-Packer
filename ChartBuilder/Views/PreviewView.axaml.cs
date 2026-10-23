using Avalonia;
using Avalonia.Controls;
using ChartBuilder.ViewModels;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The highway preview (charter-spec C18), in the Editor's side panel or its own window. While it's in a window, its
    /// scene is kept up to date; a scrub on the highway moves playback there.
    /// </summary>
    public partial class PreviewView : UserControl
    {
        public static readonly StyledProperty<bool> CanPopOutProperty =
            AvaloniaProperty.Register<PreviewView, bool>(nameof(CanPopOut), true);

        private PreviewViewModel? _shownFor;

        public PreviewView()
        {
            InitializeComponent();
            Highway.Scrubbed += (_, e) => (DataContext as PreviewViewModel)?.Playback.SeekTo(e.Seconds);
        }

        /// <summary>Whether it offers to open the preview in a window of its own: not when it's in that window.</summary>
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
            Show(DataContext as PreviewViewModel);
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
                Show(DataContext as PreviewViewModel);
        }

        // Tells the view model it's shown here, or no longer, so it builds its scene only while something shows it.
        private void Show(PreviewViewModel? preview)
        {
            if (ReferenceEquals(preview, _shownFor))
                return;
            _shownFor?.SetShown(false);
            _shownFor = preview;
            preview?.SetShown(true);
        }
    }
}
