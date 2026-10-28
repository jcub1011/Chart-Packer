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
            WindowChrome.ShowIcon(this, WindowTitleBar);
            WindowMemory.Remember(this);

            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);

            // Handled ones too: a row's detail is selectable text, which takes the clicks to select a word.
            ResultList.AddHandler(DoubleTappedEvent, OnResultDoubleTapped, handledEventsToo: true);
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
