using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChartPacker.Core;
using ChartPacker.Core.Settings;
using ChartPacker.Resources;
using Serilog;
using Serilog.Core;

namespace ChartPacker.UI
{
    /// <summary>
    /// Sets up Serilog for one of the apps: a daily log file in <see cref="UserConfigPaths.LogFolder"/>, and a log entry
    /// for any exception nothing else caught. Both apps' <c>Main</c> run their whole lifetime inside <see cref="Run"/>.
    /// Logging can be turned off in Settings (<see cref="CreateSetting"/>), and then nothing is written.
    /// </summary>
    /// <remarks>Configured in code only, so nothing is bound by reflection (Native AOT).</remarks>
    public static class AppLog
    {
        /// <summary>The number of daily log files kept per app.</summary>
        public const int RetainedFiles = 7;

        private static readonly Lock s_gate = new();
        private static string s_appName = "app";
        private static Logger? s_logger;

        /// <summary>Whether log files are being written.</summary>
        public static bool IsEnabled
        {
            get
            {
                lock (s_gate)
                    return s_logger is not null;
            }
        }

        /// <summary>
        /// Starts logging to <c>&lt;appName&gt;-&lt;date&gt;.log</c> when <paramref name="enabled"/>, runs
        /// <paramref name="run"/>, and flushes the log when it returns or throws. Returns what <paramref name="run"/> returns.
        /// </summary>
        public static int Run(string appName, bool enabled, Func<int> run)
        {
            s_appName = appName;
            SetEnabled(enabled);

            // An exception that escapes run() is logged below, before the log is flushed, so it isn't logged again here.
            Exception? escaped = null;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (!ReferenceEquals(e.ExceptionObject, escaped))
                {
                    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
                }
            };
            TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "Unobserved task exception");

            try
            {
                Log.Information("{App} {Version} starting", appName, AppVersion.Semantic);
                int code = run();
                Log.Information("{App} exiting with code {ExitCode}", appName, code);
                return code;
            }
            catch (Exception ex)
            {
                escaped = ex;
                Log.Fatal(ex, "{App} stopped by an unhandled exception", appName);
                throw;
            }
            finally
            {
                SetEnabled(false);
            }
        }

        /// <summary>
        /// Starts or stops writing the log file. Stopping flushes and closes the file, so it can be deleted, and
        /// everything logged after it is dropped.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            lock (s_gate)
            {
                if (enabled == s_logger is not null)
                    return;

                if (enabled)
                {
                    s_logger = new LoggerConfiguration()
#if DEBUG
                        .MinimumLevel.Debug()
#else
                        .MinimumLevel.Information()
#endif
                        .Enrich.WithProperty("App", s_appName)
                        .WriteTo.File(
                            Path.Combine(UserConfigPaths.LogFolder, s_appName + "-.log"),
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: RetainedFiles)
                        .CreateLogger();
                    Log.Logger = s_logger;
                    Log.Information("Logging on");
                }
                else
                {
                    Log.Information("Logging off");

                    // Swapped out before it's disposed: anything logged on another thread meanwhile goes nowhere,
                    // rather than to a closed file.
                    var closing = s_logger!;
                    Log.Logger = Logger.None;
                    s_logger = null;
                    closing.Dispose();
                }
            }
        }

        /// <summary>Opens the log folder in the system's file manager, creating it first if no log has been written yet.</summary>
        public static void OpenFolder()
        {
            string folder = UserConfigPaths.LogFolder;
            try
            {
                Directory.CreateDirectory(folder);

                // Windows opens a folder through the shell; macOS and Linux through their own openers.
                var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(folder) { UseShellExecute = true }
                    : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, ArgumentList = { folder } };
                Process.Start(start)?.Dispose();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Couldn't open the log folder {Folder}", folder);
            }
        }

        /// <summary>
        /// The Settings row that turns logging on and off, with a button that opens the log folder. Both apps list it.
        /// <paramref name="get"/> and <paramref name="set"/> read and write the app's saved setting; the row turns
        /// logging on or off as it's applied.
        /// </summary>
        public static ToggleSetting CreateSetting(SettingCategory category, Func<bool> get, Action<bool> set) => new()
        {
            Id = category.Id + ".enabled",
            Category = category,
            Label = SharedStrings.LoggingEnabledLabel,
            Description = string.Format(CultureInfo.CurrentCulture, SharedStrings.LoggingEnabledDescriptionFormat, RetainedFiles),
            Keywords = SettingKeywords.Split(SharedStrings.LoggingEnabledKeywords),
            DefaultValue = true,
            Get = get,
            Set = enabled =>
            {
                set(enabled);
                SetEnabled(enabled);
            },
            Actions = [new SettingAction(SharedStrings.OpenLogFolderButton, OpenFolder, "InputFolderStroke")],
        };
    }
}
