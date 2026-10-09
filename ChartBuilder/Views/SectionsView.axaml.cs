using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartBuilder.ViewModels;

namespace ChartBuilder.Views
{
    /// <summary>
    /// Sections (charter-spec C8 items 3, 5 and 6), a tab in the Editor's left panel: the chart's sections and other text
    /// events. A row's field applies what's typed when Enter is pressed or the field is left, and Escape puts it back.
    /// </summary>
    public partial class SectionsView : UserControl
    {
        private SectionsViewModel? _viewModel;

        public SectionsView()
        {
            InitializeComponent();
            AddHandler(KeyDownEvent, OnFieldKeyDown);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_viewModel is not null)
                _viewModel.EditRequested -= OnEditRequested;
            _viewModel = DataContext as SectionsViewModel;
            if (_viewModel is not null)
                _viewModel.EditRequested += OnEditRequested;
        }

        // After the field has had the key: Enter applies it, Escape puts it back. Space is the field's, so typing a name
        // doesn't play or pause the song (the window's Space).
        private static void OnFieldKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is not TextBox box)
                return;
            switch (e.Key)
            {
                case Key.Enter:
                    BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateTarget();
                    e.Handled = true;
                    break;
                case Key.Space:
                    e.Handled = true;
                    break;
            }
        }

        // A row inserted to be renamed: brought into view, as the list only makes the rows on screen, and once it's laid out,
        // its name is selected to type over.
        private void OnEditRequested(object? sender, EventItem item) => Dispatcher.UIThread.Post(
            () =>
            {
                EventList.ScrollIntoView(item);
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        var box = EventList.ContainerFromItem(item)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == "NameBox");
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
