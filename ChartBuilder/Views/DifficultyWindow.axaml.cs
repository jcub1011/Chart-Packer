using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>The difficulty analysis in a window of its own, with room for the graph.</summary>
    public partial class DifficultyWindow : Window
    {
        public DifficultyWindow()
        {
            // Space plays or pauses, or whatever the user has bound it to; before the window's content is built, so what it
            // holds finds the dispatcher as it attaches. A press however long it's held plays or pauses once.
            var keys = new KeyDispatcher(AppKeys.Map, KeyScope.Global);
            keys.Attach(this);
            keys.On(SharedKeys.PlayPause, () =>
            {
                if (DataContext is not DifficultyViewModel difficulty)
                    return false;
                if (difficulty.Playback.PlayPauseCommand.CanExecute(null))
                    difficulty.Playback.PlayPauseCommand.Execute(null);
                return true;
            });

            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            WindowMemory.Remember(this);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }
    }
}
