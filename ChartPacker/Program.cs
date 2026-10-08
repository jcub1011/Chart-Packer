using Avalonia;
using ChartPacker.Core;
using ChartPacker.Services;
using ChartPacker.UI;
using System;

namespace ChartPacker
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
            bool logging = SettingsService.Load(UserConfigPaths.SettingsFile).Settings.LoggingEnabled;
            return AppLog.Run("chartpacker", logging, () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args));
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
