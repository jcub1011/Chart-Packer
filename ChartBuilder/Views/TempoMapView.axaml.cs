using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartBuilder.ViewModels;

namespace ChartBuilder.Views
{
    /// <summary>
    /// Tempo map (charter-spec C1, C4, C14), a tab in the Editor's left panel: the chart's resolution and the audio's delay,
    /// the audio's proposals, and every tempo and time signature change. A row's field
    /// applies what's typed when Enter is pressed or the field is left, and Escape puts it back.
    /// </summary>
    public partial class TempoMapView : UserControl
    {
        private TempoMapViewModel? _viewModel;

        public TempoMapView()
        {
            InitializeComponent();
            AddHandler(KeyDownEvent, FieldKeys.OnKeyDown);
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

        // A change added to be typed into: brought into view, as the list only makes the rows on screen, and once it's laid
        // out, its field is selected to type over.
        private void OnEditRequested(object? sender, TempoFieldRequest request) => Dispatcher.UIThread.Post(
            () =>
            {
                ChangeList.ScrollIntoView(request.Item);
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        string name = request.Field == TempoField.Bpm ? "BpmBox" : "NumeratorBox";
                        var box = ChangeList.ContainerFromItem(request.Item)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == name);
                        if (box is null)
                            return;
                        box.BringIntoView();
                        box.Focus();
                        box.SelectAll();
                    },
                    DispatcherPriority.Loaded);
            },
            DispatcherPriority.Loaded);
    }
}
