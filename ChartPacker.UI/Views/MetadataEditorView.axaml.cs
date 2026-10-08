using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartPacker.UI.ViewModels;

namespace ChartPacker.UI.Views
{
    /// <summary>
    /// A song inspector's metadata section (<see cref="MetadataEditorViewModel"/>), in Chart Packer's inspector and
    /// Chart Builder's Chart Inspector.
    /// </summary>
    public partial class MetadataEditorView : UserControl
    {
        public MetadataEditorView()
        {
            InitializeComponent();

            // The new-row key box acts as a dropdown of known keys: it opens when the editor appears
            // and whenever the box is clicked, not only on Down/F4 as AutoCompleteBox does by itself.
            NewKeyBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && e.NewValue is true)
                    Dispatcher.UIThread.Post(OpenKeyDropDown, DispatcherPriority.Loaded);
            };
            NewKeyBox.AddHandler(PointerReleasedEvent, OnKeyBoxPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            NewKeyBox.AddHandler(KeyDownEvent, OnKeyBoxKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Choosing a difficulty in an instrument's flyout closes it. The flyout is a popup of its own,
        /// so the button it belongs to is found through the popup it's shown in.
        /// </summary>
        /// <remarks>
        /// A button raises Click before it runs its command, and closing the flyout takes the button
        /// out of the tree, which clears its bound command. So the flyout closes once the command has run.
        /// </remarks>
        private void OnDifficultyChosen(object? sender, RoutedEventArgs e)
        {
            if (sender is ILogical control && control.FindLogicalAncestorOfType<Popup>() is { PlacementTarget: Button { Flyout: { } flyout } })
                Dispatcher.UIThread.Post(flyout.Hide);
        }

        /// <summary>Clicking away from a value's edit box keeps what was typed, like pressing Enter.</summary>
        private void OnEditLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: MetadataRowViewModel row })
                row.CommitCommand.Execute(null);
        }

        private void OpenKeyDropDown()
        {
            if (NewKeyBox.IsEffectivelyVisible)
                NewKeyBox.IsDropDownOpen = true;
        }

        private void OnKeyBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Only clicks on the text box itself: a click on a suggestion bubbles here too, just as
            // the dropdown closes, and must not open it again.
            if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
                OpenKeyDropDown();
        }

        /// <summary>
        /// With the dropdown closed, Enter moves on to the value and Escape cancels the new row. With it
        /// open, AutoCompleteBox keeps both keys to pick or dismiss a suggestion.
        /// </summary>
        private void OnKeyBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (NewKeyBox.IsDropDownOpen || DataContext is not MetadataEditorViewModel vm)
                return;

            if (e.Key == Key.Enter)
            {
                NewValueBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CancelAddCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
