using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChartPacker.Core;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using ChartPacker.Views;

namespace ChartPacker
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Composition root: services are wired by hand (spec §4.2).
                var settings = SettingsService.Load(UserConfigPaths.SettingsFile);
                var window = new MainWindow();
                var folderPicker = new AvaloniaFolderPickerService(() => window);

                window.DataContext = new MainWindowViewModel(new LibraryViewModel(folderPicker, settings), settings);

                // Subscribed after MainWindow's own Closing handler, which captures the splitter width first.
                window.Closing += (_, _) => settings.Save();

                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
