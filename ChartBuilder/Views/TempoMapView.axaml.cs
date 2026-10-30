using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartBuilder.Resources;
using ChartBuilder.ViewModels;
using ChartShared.UI.Controls;

namespace ChartBuilder.Views
{
    /// <summary>
    /// Tempo map (charter-spec C1, C4, C14), a tab in the Editor's left panel: the chart's resolution and the audio's delay,
    /// the audio's proposals, and every tempo and time signature change. A row's field
    /// applies what's typed when Enter is pressed or the field is left, and Escape puts it back. Its time signature opens
    /// an editor of its own, and its right-click menu goes to it, moves it to the playhead or deletes it.
    /// </summary>
    public partial class TempoMapView : UserControl
    {
        private TempoMapViewModel? _viewModel;

        public TempoMapView()
        {
            InitializeComponent();
            AddHandler(KeyDownEvent, FieldKeys.OnKeyDown);
            AddHandler(KeyDownEvent, OnFieldEscape, RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnRowContextRequested, RoutingStrategies.Tunnel);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_viewModel is not null)
                _viewModel.EditRequested -= OnEditRequested;
            _viewModel = DataContext as TempoMapViewModel;
            if (_viewModel is not null)
                _viewModel.EditRequested += OnEditRequested;
        }

        /// <summary>A proposed tempo map's options close once it's proposed, so the proposal can be seen.</summary>
        private void OnProposeTempoMapClick(object? sender, RoutedEventArgs e) => ProposeTempoMapButton.Flyout?.Hide();

        // The time signature's editor opens on the signature as it is. Its Enter is seen on the way down, as the beats field
        // takes Enter for itself.
        private void OnSignatureFlyoutOpening(object? sender, EventArgs e)
        {
            if (sender is not Flyout { Target.DataContext: TempoChangeItem item } flyout)
                return;

            item.BeginSignatureEdit();
            if (flyout.Content is Control editor)
            {
                editor.RemoveHandler(KeyDownEvent, OnSignatureKeyDown);
                editor.AddHandler(KeyDownEvent, OnSignatureKeyDown, RoutingStrategies.Tunnel);
            }
        }

        // A preset, Apply or Remove closes the editor, once its command has run: closing it first would clear the command.
        private void OnSignatureChosen(object? sender, RoutedEventArgs e) => HideFlyoutOf(sender as Control);

        // Enter in the beats field applies what's in the editor, once the field has taken what was typed in it.
        private void OnSignatureKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not Control { DataContext: TempoChangeItem item } editor
                || (e.Source as Visual)?.FindAncestorOfType<NumericUpDown>(includeSelf: true) is null)
                return;

            // Not handled, so the field still takes it, and has what was typed by the time it's applied.
            Dispatcher.UIThread.Post(() =>
            {
                item.ApplySignatureCommand.Execute(null);
                HideFlyoutOf(editor);
            });
        }

        private static void HideFlyoutOf(Control? control)
        {
            if (control?.FindLogicalAncestorOfType<Popup>() is { PlacementTarget: Button { Flyout: { } flyout } })
                Dispatcher.UIThread.Post(flyout.Hide);
        }

        // The menu is a popup of its own, so it's filled as a row asks for it, on the way down, before it opens. Not as it
        // opens: until it's first open it isn't in the row's tree, so it hasn't the row as its DataContext.
        private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.ContextMenu is not null) is not { ContextMenu: { } menu } border)
                return;

            menu.Items.Clear();
            if (border.DataContext is not TempoChangeItem item)
                return;

            menu.Items.Add(MenuActions.Create(Strings.TempoMapGoTo, item.GoToCommand, "ArrowRightStroke"));
            menu.Items.Add(MenuActions.Create(Strings.TempoMapMoveToPlayhead, item.MoveToPlayheadCommand, "PlayheadStroke", item.CanRemove));
            menu.Items.Add(new Separator());
            if (item.RemovesEither)
            {
                menu.Items.Add(MenuActions.Create(Strings.TempoMapDeleteTempo, item.RemoveTempoCommand, "TrashStroke"));
                menu.Items.Add(MenuActions.Create(Strings.TempoMapDeleteSignature, item.RemoveSignatureCommand, "TrashStroke"));
                menu.Items.Add(MenuActions.Create(Strings.TempoMapDeleteBoth, item.RemoveBothCommand, "TrashStroke"));
            }
            else
            {
                // The song's start can't be deleted, but says so by being greyed.
                menu.Items.Add(MenuActions.Create(Strings.TempoMapDelete, item.RemoveCommand, "TrashStroke", item.RemovesOne));
            }
        }

        // A menu with nothing on it isn't shown at all.
        private void OnRowMenuOpening(object? sender, CancelEventArgs e)
        {
            if (sender is ContextMenu { ItemCount: 0 })
                e.Cancel = true;
        }

        // Escape in the BPM or tick box takes back what's typed, as a field's Escape does. Their text is the box's own, so
        // the row is filled again from the chart rather than a binding put back.
        private void OnFieldEscape(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || e.Source is not Visual source
                || source.FindAncestorOfType<NumericUpDown>(includeSelf: true) is not { DataContext: TempoChangeItem item } box)
            {
                return;
            }

            if (box.Name == "BpmBox")
                _viewModel?.RevertBpm(item);
            else if (box.Name == "TickBox")
                _viewModel?.RevertTick(item);
            else
                return;
            e.Handled = true;
        }

        // A change added to be typed into: brought into view, as the list only makes the rows on screen, and once it's laid
        // out, its BPM is selected to type over, or its time signature's editor opened on its beats.
        private void OnEditRequested(object? sender, TempoFieldRequest request) => Dispatcher.UIThread.Post(
            () =>
            {
                ChangeList.ScrollIntoView(request.Item);
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (ChangeList.ContainerFromItem(request.Item) is not { } row)
                            return;

                        if (request.Field == TempoField.Bpm)
                        {
                            if (row.GetVisualDescendants().OfType<NumericUpDown>().FirstOrDefault(b => b.Name == "BpmBox") is not { } box)
                                return;
                            box.BringIntoView();
                            if (box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } text)
                            {
                                text.Focus();
                                text.SelectAll();
                            }
                            else
                            {
                                box.Focus();
                            }

                            return;
                        }

                        if (row.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SignatureButton") is not { Flyout: Flyout flyout } button)
                            return;
                        button.BringIntoView();
                        flyout.ShowAt(button);
                        Dispatcher.UIThread.Post(
                            () =>
                            {
                                if ((flyout.Content as Control)?.GetVisualDescendants().OfType<NumericUpDown>().FirstOrDefault() is { } beats)
                                    beats.Focus();
                            },
                            DispatcherPriority.Loaded);
                    },
                    DispatcherPriority.Loaded);
            },
            DispatcherPriority.Loaded);
    }
}
