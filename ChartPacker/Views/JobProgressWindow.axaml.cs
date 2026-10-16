using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChartPacker.ViewModels;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// The job's progress and its results, in one list that a search and the outcome chips narrow. It isn't modal;
    /// closing it leaves the job running.
    /// </summary>
    public partial class JobProgressWindow : Window
    {
        public JobProgressWindow()
        {
            InitializeComponent();
            WindowTitleBar.Icon = WindowChrome.ImageOf(Icon);

            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);

            // Handled ones too: a row's detail is selectable text, which takes the clicks to select a word.
            ResultList.AddHandler(DoubleTappedEvent, OnResultDoubleTapped, handledEventsToo: true);
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

        /// <summary>Selects the row's song in the main window's library, and brings that window forward.</summary>
        private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
        {
            if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not JobItemRow row
                || DataContext is not JobViewModel job)
                return;

            job.ShowInLibrary(row);
            Owner?.Activate();
        }

        private void OnClose(object? sender, RoutedEventArgs e) => Close();
    }
}
