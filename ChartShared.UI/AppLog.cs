using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Logging;
using ChartShared.Core;
using ChartShared.Core.Diagnostics;
using ChartShared.Core.Settings;
using ChartShared.UI.Resources;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using AvaloniaLogger = Avalonia.Logging.Logger;
using AvaloniaLogLevel = Avalonia.Logging.LogEventLevel;
using LogEventLevel = Serilog.Events.LogEventLevel;
using Logger = Serilog.Core.Logger;

namespace ChartShared.UI
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

        // Which thread logged each entry, and the class it came from, so what ran just before a crash can be followed
        // on each thread, e.g. "[14 Video playback] VideoPlayback: ...".
        private const string OutputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Thread}] {Source}: {Message:lj}{NewLine}{Exception}";

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
                    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception on thread {Thread}; the app is ending", ThreadLabel());
                }
            };
            TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "Unobserved task exception");

            Log.Information(
                "{App} {Version} starting: {OS}, .NET {Runtime} {Architecture}, process {ProcessId}",
                appName, AppVersion.Semantic, RuntimeInformation.OSDescription, Environment.Version, RuntimeInformation.ProcessArchitecture, Environment.ProcessId);
            var session = enabled ? BeginSession(appName) : null;
            try
            {
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
                session?.End();
                SetEnabled(false);
            }
        }

        /// <summary>
        /// Marks this session as running, and logs any earlier one that never shut down. A crash the runtime can't report
        /// (an ExecutionEngineException, an access violation in native code) logs nothing itself, so this is the only sign
        /// of it; the entries that session logged last show what it was doing.
        /// </summary>
        private static SessionMarker? BeginSession(string appName)
        {
            try
            {
                var session = SessionMarker.Begin(UserConfigPaths.LogFolder, appName, AppVersion.Semantic, DateTimeOffset.Now, Environment.ProcessId);
                foreach (var unclean in session.Unclean)
                {
                    Log.Warning(
                        "The {App} session started {Started} (version {Version}, process {ProcessId}) never shut down: it crashed, " +
                        "was ended, or the computer stopped. Its last entries, in the log for that day, show what it was doing",
                        appName, unclean.Started, unclean.Version, unclean.ProcessId);
                }

                return session;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Couldn't mark the session as running, so a crash won't be noticed at the next start");
                return null;
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
                        .Enrich.With<ThreadAndSourceEnricher>()
                        // Each entry is passed to the system as it's written (not buffered), so whatever was logged
                        // before a crash, even one that ends the process at once, is in the file.
                        .WriteTo.File(
                            Path.Combine(UserConfigPaths.LogFolder, s_appName + "-.log"),
                            outputTemplate: OutputTemplate,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: RetainedFiles,
                            buffered: false)
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

        /// <summary>
        /// Passes Avalonia's own warnings and errors (bindings that fail, layout that can't be done) on to the log, as
        /// well as to <paramref name="builder"/>'s sink, e.g. <c>LogToTrace</c>'s. Call it after that.
        /// </summary>
        public static AppBuilder LogToAppLog(this AppBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            AvaloniaLogger.Sink = new AvaloniaLogSink(AvaloniaLogger.Sink);
            return builder;
        }

        // The current thread's id, and its name when it has one, e.g. "14 Video playback".
        private static string ThreadLabel()
        {
            var thread = Thread.CurrentThread;
            return thread.Name is { Length: > 0 } name
                ? $"{thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture)} {name}"
                : thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Adds <c>Thread</c>, and <c>Source</c>: the logging class's name without its namespace, or "-".</summary>
        private sealed class ThreadAndSourceEnricher : ILogEventEnricher
        {
            public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
            {
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Thread", ThreadLabel()));
                string source = logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var context)
                    && context is ScalarValue { Value: string name }
                    ? name[(name.LastIndexOf('.') + 1)..]
                    : "-";
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Source", source));
            }
        }

        /// <summary>
        /// Avalonia's log, at Warning and up, to Serilog under the source <c>Avalonia/&lt;area&gt;</c>. A message
        /// repeated many times (one binding failing on every row, say) is only logged the first few times.
        /// </summary>
        private sealed class AvaloniaLogSink(ILogSink? inner) : ILogSink
        {
            private const int MaxRepeats = 20;

            private readonly Lock _gate = new();
            private readonly Dictionary<(string Area, string Template), int> _counts = [];

            public bool IsEnabled(AvaloniaLogLevel level, string area) =>
                level >= AvaloniaLogLevel.Warning || inner?.IsEnabled(level, area) == true;

            public void Log(AvaloniaLogLevel level, string area, object? source, string messageTemplate) =>
                Log(level, area, source, messageTemplate, []);

            public void Log(AvaloniaLogLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            {
                if (inner?.IsEnabled(level, area) == true)
                    inner.Log(level, area, source, messageTemplate, propertyValues);
                if (level < AvaloniaLogLevel.Warning)
                    return;

                int count;
                lock (_gate)
                {
                    _counts.TryGetValue((area, messageTemplate), out count);
                    _counts[(area, messageTemplate)] = ++count;
                }

                if (count > MaxRepeats)
                    return;

                var logger = Serilog.Log.ForContext(Constants.SourceContextPropertyName, "Avalonia/" + area)
                    .ForContext("AvaloniaSource", source?.GetType().Name);
                var serilogLevel = level == AvaloniaLogLevel.Warning ? LogEventLevel.Warning
                    : level == AvaloniaLogLevel.Error ? LogEventLevel.Error
                    : LogEventLevel.Fatal;
                logger.Write(serilogLevel, messageTemplate, propertyValues);
                if (count == MaxRepeats)
                    logger.Write(serilogLevel, "(That message has been logged {Count} times; it won't be again this session)", count);
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
