using Avalonia;
using ChartBuilder.Services;
using ChartPacker;
using ChartPacker.Core;
using System;

namespace ChartBuilder
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static int Main(string[] args)
        {
            // Read ahead of App, which loads the settings again, so that startup is logged too.
            bool logging = BuilderSettingsService.Load(UserConfigPaths.BuilderSettingsFile).Settings.LoggingEnabled;
            return AppLog.Run("chartbuilder", logging, () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args));
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
    }
}
