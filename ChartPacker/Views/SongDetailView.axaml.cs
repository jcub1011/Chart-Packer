using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    public partial class SongDetailView : UserControl
    {
        public SongDetailView()
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
            if (NewKeyBox.IsDropDownOpen || DataContext is not SongDetailViewModel vm)
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
