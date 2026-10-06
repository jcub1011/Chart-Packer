using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChartBuilder.Services;
using ChartPacker.Core;
using ChartPacker.Core.Launching;

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
                // Composition root: services are wired by hand, as in Chart Packer. BuilderWindows wires each window
                // as it opens: the launcher first, then the Editor in its place.
                var settings = BuilderSettingsService.Load(UserConfigPaths.BuilderSettingsFile);
                var windows = new BuilderWindows(desktop, settings);
                windows.StartWith(BuilderArgs.Parse(desktop.Args ?? []));

                // Settings are saved as they change (the Settings window saves them, and the Editor saves its layout
                // as it closes), so there's nothing left to save on exit.
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
