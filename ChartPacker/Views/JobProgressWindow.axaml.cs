using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ChartPacker.Views
{
    /// <summary>
    /// The job's progress and its results, grouped by outcome. It isn't modal; closing it leaves the job
    /// running.
    /// </summary>
    public partial class JobProgressWindow : Window
    {
        public JobProgressWindow()
        {
            InitializeComponent();

            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private void OnClose(object? sender, RoutedEventArgs e) => Close();
    }
}
