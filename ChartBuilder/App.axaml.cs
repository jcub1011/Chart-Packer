using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartPacker.Core;
using ChartPacker.Core.Launching;
using ChartPacker.Services;
using ChartPacker.ViewModels.Settings;

namespace ChartBuilder
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
                // Composition root: services are wired by hand, as in Chart Packer.
                var settings = BuilderSettingsService.Load(UserConfigPaths.BuilderSettingsFile);
                var window = new MainWindow();
                var newChartWindow = new AvaloniaNewChartWindowService(() => window, settings);
                var settingsWindow = new AvaloniaSettingsWindowService(
                    () => window,
                    settingsPicker => new SettingsWindowViewModel(BuilderSettingsCatalog.Create(settings), settingsPicker, settings.Save));
                var viewModel = new MainWindowViewModel(newChartWindow, settingsWindow);
                window.DataContext = viewModel;

                // What the command line asks for (charter-spec §4.8) opens over the window once it's showing.
                var args = BuilderArgs.Parse(desktop.Args ?? []);
                EventHandler? opened = null;
                opened = (_, _) =>
                {
                    window.Opened -= opened;
                    _ = viewModel.StartAsync(args);
                };
                window.Opened += opened;

                // Settings are saved as they change (the Settings window and the wizard save them), so there's
                // nothing left to save on closing.
                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
