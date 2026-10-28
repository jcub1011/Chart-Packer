using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>The highway preview in a window of its own (charter-spec C18 item 1), which can go full screen.</summary>
    public partial class PreviewWindow : Window
    {
        public PreviewWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            WindowMemory.Remember(this);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        // Full screen is the highway alone, without the title bar.
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WindowStateProperty && WindowTitleBar is { } titleBar)
                titleBar.IsVisible = change.GetNewValue<WindowState>() != WindowState.FullScreen;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled)
                return;
            switch (e.Key)
            {
                case Key.F11:
                    WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                    e.Handled = true;
                    break;
                case Key.Escape when WindowState == WindowState.FullScreen:
                    WindowState = WindowState.Normal;
                    e.Handled = true;
                    break;
                case Key.Space when e.KeyModifiers == KeyModifiers.None && DataContext is PreviewViewModel preview:
                    if (preview.Playback.PlayPauseCommand.CanExecute(null))
                        preview.Playback.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }
}
