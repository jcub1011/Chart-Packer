using System;
using Avalonia;
using Avalonia.Controls;
using ChartPacker.ViewModels;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Deduplicate window. Closing it stops the comparison; a deletion that's running stops after
    /// the copy it's on.
    /// </summary>
    public partial class DedupWindow : Window
    {
        public DedupWindow()
        {
            InitializeComponent();
            WindowTitleBar.Icon = WindowChrome.ImageOf(Icon);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // The icon is the owner's (WindowChrome.UseOwnerIcon), so the title bar shows it as the window gets it.
            // Null-checked, as a style can set the icon before InitializeComponent has made the title bar; the
            // constructor copies that one over.
            if (change.Property == IconProperty && WindowTitleBar is not null)
                WindowTitleBar.Icon = WindowChrome.ImageOf(Icon);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is DedupWindowViewModel viewModel)
                viewModel.CloseRequested += Close;
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            (DataContext as DedupWindowViewModel)?.OnClosed();
        }
    }
}
