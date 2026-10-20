using Avalonia.Controls;
using Avalonia.Input;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>The difficulty analysis in a window of its own, with room for the graph.</summary>
    public partial class DifficultyWindow : Window
    {
        public DifficultyWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || DataContext is not DifficultyViewModel difficulty)
                return;
            if (difficulty.Playback.PlayPauseCommand.CanExecute(null))
                difficulty.Playback.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
