using Avalonia;
using Avalonia.Controls;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>The highway preview in a window of its own (charter-spec C18 item 1), which can go full screen.</summary>
    public partial class PreviewWindow : Window
    {
        public PreviewWindow()
        {
            RegisterKeys();
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

        /// <summary>
        /// The window's keys are the app's <see cref="KeyMap"/>: Space plays or pauses, F11 goes full screen and back, and Esc
        /// leaves it. Before the window's content is built, so what it holds finds the dispatcher as it attaches.
        /// </summary>
        private void RegisterKeys()
        {
            var keys = new KeyDispatcher(AppKeys.Map, BuilderKeys.Preview);
            keys.Attach(this);

            keys.On(SharedKeys.PlayPause, () =>
            {
                if (DataContext is not PreviewViewModel preview)
                    return false;
                if (preview.Playback.PlayPauseCommand.CanExecute(null))
                    preview.Playback.PlayPauseCommand.Execute(null);
                return true;
            });
            keys.On(BuilderKeys.PreviewFullScreen, () =>
            {
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                return true;
            });
            keys.On(BuilderKeys.PreviewExitFullScreen, () =>
            {
                if (WindowState != WindowState.FullScreen)
                    return false;
                WindowState = WindowState.Normal;
                return true;
            });
        }
    }
}
