using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartShared.UI.ViewModels;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// A song inspector's metadata section (<see cref="MetadataEditorViewModel"/>), in Chart Packer's inspector and
    /// Chart Builder's Chart Inspector.
    /// </summary>
    public partial class MetadataEditorView : UserControl
    {
        // Narrower than this, a row's value goes on a second line under its key; wider than the other,
        // back beside it. The gap keeps a width near the edge, as a scroll bar coming and going moves it,
        // from switching back and forth. A row is the marker (14) plus the key (up to 220, shared) plus the
        // value, so side by side needs roughly this: a typical key leaves the value room, a longest key trims.
        private const double CompactBelow = 380;
        private const double WideFrom = 400;

        private MetadataEditorViewModel? _editor;

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

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_editor is not null)
                _editor.NewKeyFocusRequested -= OnNewKeyFocusRequested;
            _editor = DataContext as MetadataEditorViewModel;
            if (_editor is not null)
                _editor.NewKeyFocusRequested += OnNewKeyFocusRequested;
        }

        /// <summary>Puts each row's value on a second line under its key while the panel is too narrow for one.</summary>
        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            double width = e.NewSize.Width;
            if (width < CompactBelow)
                Classes.Set("compact", true);
            else if (width >= WideFrom)
                Classes.Set("compact", false);
        }

        /// <summary>Clicking away from a value's edit box keeps what was typed, like pressing Enter.</summary>
        private void OnEditLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: MetadataRowViewModel row })
                row.CommitCommand.Execute(null);
        }

        /// <summary>
        /// Back to the new row's key: after a key is added, empty for the next with the known keys dropped down, or with
        /// the key that was refused.
        /// </summary>
        private void OnNewKeyFocusRequested(object? sender, EventArgs e)
        {
            NewKeyBox.Focus();
            if (string.IsNullOrEmpty(NewKeyBox.Text))
                Dispatcher.UIThread.Post(OpenKeyDropDown, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Leaving the new row with nothing typed closes it, as there's nothing to lose; with a key or value typed it stays
        /// open. Checked once the focus has landed, as it moves between the row's own boxes too, and not while the key's
        /// dropdown is open, as picking from it takes the focus for a moment. Background priority leaves a pick time to
        /// write its key and give the focus back before then.
        /// </summary>
        private void OnDraftRowLostFocus(object? sender, RoutedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not MetadataEditorViewModel { IsAdding: true } vm
                    || DraftRow.IsKeyboardFocusWithin
                    || NewKeyBox.IsDropDownOpen)
                    return;

                if (string.IsNullOrWhiteSpace(vm.NewKey)
                    && string.IsNullOrWhiteSpace(vm.NewValue)
                    && string.IsNullOrWhiteSpace(NewKeyBox.Text))
                    vm.CancelAddCommand.Execute(null);
            }, DispatcherPriority.Background);
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
