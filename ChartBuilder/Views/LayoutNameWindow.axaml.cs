using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>A layout template's name (issue 263), new or renamed. Closes with the name confirmed, trimmed, or null.</summary>
    public partial class LayoutNameWindow : Window
    {
        public LayoutNameWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                NameBox.Focus();
                NameBox.SelectAll();
            };
        }

        // The view model the window listens to for its name being confirmed, while it's the DataContext.
        private LayoutNameViewModel? _viewModel;

        // Whether the window has been told to close, so Enter and a click on Save together, or Save then Cancel, close it once.
        private bool _closing;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_viewModel is not null)
                _viewModel.Confirmed -= OnConfirmed;
            _viewModel = DataContext as LayoutNameViewModel;
            if (_viewModel is not null)
                _viewModel.Confirmed += OnConfirmed;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_viewModel is not null)
                _viewModel.Confirmed -= OnConfirmed;
            base.OnClosed(e);
        }

        private void OnConfirmed(object? sender, EventArgs e) => CloseWith(_viewModel?.Result);

        private void OnCancel(object? sender, RoutedEventArgs e) => CloseWith(null);

        private void CloseWith(string? name)
        {
            if (_closing)
                return;
            _closing = true;
            Close(name);
        }
    }
}
