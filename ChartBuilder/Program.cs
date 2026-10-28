using Avalonia;
using ChartBuilder.Services;
using ChartShared.Core;
using ChartShared.Core.Launching;
using ChartShared.UI;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChartBuilder
{
    internal sealed class Program
    {
        /// <summary>Empties the taskbar's Jump List and exits; the installer's uninstaller runs it.</summary>
        private const string ClearJumpListArgument = "--clear-jump-list";

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static int Main(string[] args)
        {
            // The difficulty report runs without a window, and touches neither the settings nor the log.
            if (DifficultyReportArgs.IsReport(args))
                return DifficultyReportCommand.Run(args);

            // The uninstaller's: the Jump List outlives the app unless it's emptied. Like the report, it makes no window.
            if (OperatingSystem.IsWindows() && args is [ClearJumpListArgument])
                return ClearJumpList();

            // Read ahead of App, which loads the settings again, so that startup is logged too.
            bool logging = BuilderSettingsService.Load(UserConfigPaths.BuilderSettingsFile).Settings.LoggingEnabled;
            return AppLog.Run("chartbuilder", logging, () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args));
        }

        [SupportedOSPlatform("windows")]
        private static int ClearJumpList()
        {
            try
            {
                WindowsJumpList.Clear();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // Uninstalling goes on without it: the entries are only a convenience.
            }

            return 0;
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
