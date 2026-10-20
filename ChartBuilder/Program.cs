using Avalonia;
using ChartBuilder.Services;
using ChartShared.Core;
using ChartShared.Core.Launching;
using ChartShared.UI;
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
            // The difficulty report runs without a window, and touches neither the settings nor the log.
            if (DifficultyReportArgs.IsReport(args))
                return DifficultyReportCommand.Run(args);

            // Read ahead of App, which loads the settings again, so that startup is logged too.
            bool logging = BuilderSettingsService.Load(UserConfigPaths.BuilderSettingsFile).Settings.LoggingEnabled;
            return AppLog.Run("chartbuilder", logging, () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args));
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // Room on the GPU for the lanes' waveform pictures and the timeline's beside the window itself: within
                // Avalonia's own ~28 MB they were dropped and uploaded again every frame. A ceiling Skia keeps under, not
                // an allocation.
                .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256 * 1024 * 1024 })
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace()
                .LogToAppLog();
    }
}
