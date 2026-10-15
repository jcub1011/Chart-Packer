using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace ChartPacker.UI.Views
{
    /// <summary>A difficulty's flyout (<see cref="ViewModels.IDifficultyPicker"/>). Its look is in DifficultyPicker.axaml.</summary>
    public partial class DifficultyPicker : UserControl
    {
        public DifficultyPicker()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Choosing a difficulty closes the flyout. The flyout is a popup of its own, so the button it belongs to is found
        /// through the popup it's shown in.
        /// </summary>
        /// <remarks>
        /// A button raises Click before it runs its command, and closing the flyout takes the button out of the tree, which
        /// clears its bound command. So the flyout closes once the command has run.
        /// </remarks>
        private void OnChosen(object? sender, RoutedEventArgs e)
        {
            if (sender is ILogical control && control.FindLogicalAncestorOfType<Popup>() is { PlacementTarget: Button { Flyout: { } flyout } })
                Dispatcher.UIThread.Post(flyout.Hide);
        }
    }
}
