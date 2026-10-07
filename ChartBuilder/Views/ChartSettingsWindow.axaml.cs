using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartPacker.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// Chart settings (charter-spec C1): one page of sections, which the list on the left scrolls to. Closing it any way
    /// but OK changes nothing.
    /// </summary>
    public partial class ChartSettingsWindow : Window
    {
        public ChartSettingsWindow()
        {
            InitializeComponent();
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                if (ViewModel is not { } vm)
                    return;

                vm.CloseRequested += (_, _) => Close();
                vm.PropertyChanged += OnViewModelChanged;
            };
        }

        private ChartSettingsViewModel? ViewModel => DataContext as ChartSettingsViewModel;

        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The section's heading goes to the top of the page, as far as the page scrolls.
            if (e.PropertyName == nameof(ChartSettingsViewModel.SelectedSection)
                && ViewModel?.SelectedSection is { } section
                && this.FindControl<Control>(section.Id + "Section") is { } heading
                && PageScroller.Content is Visual content
                && heading.TranslatePoint(default, content) is { } at)
                PageScroller.Offset = new Vector(PageScroller.Offset.X, at.Y);
        }
    }
}
