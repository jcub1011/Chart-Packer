using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ChartShared.Core.Diagnostics;
using ChartShared.UI.Views;
using Serilog;

namespace ChartShared.UI
{
    /// <summary>
    /// Catches an exception nothing else did on the UI thread and shows the <see cref="CrashDialog"/>, so the app carries
    /// on rather than ending with the user's work unsaved. <see cref="CrashGate"/> decides when one is let end the app
    /// after all; then <see cref="AppLog"/> logs it as it ends. Exceptions on other threads can't be caught this way, and
    /// still end the app.
    /// </summary>
    /// <remarks>Started with <see cref="SimulatedCrash.Option"/>, the app throws once it has started, to show the dialog.</remarks>
    public static class CrashHandler
    {
        private static readonly CrashGate s_gate = new();
        private static CrashDialog? s_open;
        private static bool s_installed;
        private static bool s_quitChosen;
        private static bool s_exiting;

        private static ILogger Logger => Log.ForContext(typeof(CrashHandler));

        /// <summary>
        /// Starts catching the UI thread's exceptions, as the app starts; called again, it only returns the arguments. With
        /// <see cref="SimulatedCrash.Option"/> among <paramref name="desktop"/>'s arguments, a
        /// <see cref="SimulatedCrashException"/> is thrown once the app has nothing else to do, its windows open.
        /// </summary>
        /// <param name="appName">The app's name, for the dialog's title and the report.</param>
        /// <returns>The app's arguments without <see cref="SimulatedCrash.Option"/>, for it to read.</returns>
        public static IReadOnlyList<string> Install(IClassicDesktopStyleApplicationLifetime desktop, string appName)
        {
            ArgumentNullException.ThrowIfNull(desktop);
            var args = SimulatedCrash.Take(desktop.Args ?? [], out bool simulate);
            if (s_installed)
                return args;

            s_installed = true;
            Dispatcher.UIThread.UnhandledException += (_, e) => e.Handled = Handle(desktop, appName, e.Exception);
            desktop.Exit += (_, e) =>
            {
                s_exiting = true;
                s_gate.ShuttingDown();
                // The last window closing ends the app with 0, however the user's choice to quit got there.
                if (s_quitChosen)
                    e.ApplicationExitCode = 1;
            };
            if (simulate)
                Dispatcher.UIThread.Post(static () => throw new SimulatedCrashException(), DispatcherPriority.ApplicationIdle);
            return args;
        }

        // Whether the exception is handled: shown, or counted in the dialog open. False lets it end the app.
        private static bool Handle(IClassicDesktopStyleApplicationLifetime desktop, string appName, Exception exception)
        {
            switch (s_gate.Next())
            {
                case CrashResponse.Show:
                    Logger.Error(exception, "Unhandled exception on the UI thread; the crash dialog is shown");
                    if (Show(desktop, appName, exception))
                        return true;

                    s_gate.Failed();
                    return false;
                case CrashResponse.Absorb:
                    // An error on every tick would fill the log; the dialog still counts them all.
                    if (s_gate.Extra <= CrashGate.MaxLogged)
                        Logger.Error(exception, "Unhandled exception on the UI thread while the crash dialog is open");
                    if (s_gate.Extra == CrashGate.MaxLogged)
                        Logger.Warning("Any more while the crash dialog is open are counted there, not logged");
                    s_open?.ShowExtra(s_gate.Extra);
                    return true;
                default:
                    // AppLog logs it as the app ends.
                    return false;
            }
        }

        // Shows the dialog over the window in front. False when it couldn't be.
        private static bool Show(IClassicDesktopStyleApplicationLifetime desktop, string appName, Exception exception)
        {
            try
            {
                var dialog = new CrashDialog(appName, exception);
                void OnLaidOut(object? sender, EventArgs e)
                {
                    dialog.LayoutUpdated -= OnLaidOut;
                    s_gate.Drawn();
                }
                dialog.LayoutUpdated += OnLaidOut;

                // In the background no window is active; Windows is in the order they opened, so the last still showing is
                // the one in front, such as a dialog open over the main window.
                var owner = desktop.Windows.LastOrDefault(w => w.IsActive)
                    ?? desktop.Windows.LastOrDefault(w => w.IsVisible)
                    ?? desktop.MainWindow;
                if (owner is { IsVisible: true })
                {
                    WindowChrome.UseOwnerIcon(dialog, owner);
                    _ = ShowAsync(desktop, appName, exception, dialog, dialog.ShowDialog(owner));
                }
                else
                {
                    // No window to show it over, e.g. one that failed as it opened.
                    dialog.Icon = owner?.Icon;
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    var closed = new TaskCompletionSource();
                    dialog.Closed += (_, _) => closed.TrySetResult();
                    dialog.Show();
                    _ = ShowAsync(desktop, appName, exception, dialog, closed.Task);
                }

                s_open = dialog;
                s_open.ShowExtra(s_gate.Extra);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Couldn't show the crash dialog");
                return false;
            }
        }

        private static async Task ShowAsync(
            IClassicDesktopStyleApplicationLifetime desktop, string appName, Exception exception, CrashDialog dialog, Task closed)
        {
            await closed;
            s_open = null;

            // Its window closed under it, before the user chose. Once that window has closed (or been kept open), it's
            // shown again over what's left; with nothing left, the app is ending.
            if (dialog.OwnerClosed)
            {
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                if (!s_exiting && desktop.Windows.Any(w => w.IsVisible))
                {
                    Logger.Information("The crash dialog's window closed under it, so it's shown again");
                    if (Show(desktop, appName, exception))
                        return;
                }
            }

            // Back to working, even to quit: the windows may yet ask about unsaved changes, and an error then is shown too.
            s_gate.Closed();
            if (dialog.QuitChosen)
                Quit(desktop, appName);
        }

        // Each window is closed as its close button would close it, so unsaved changes are still asked about, and the last
        // to close ends the app. Not TryShutdown: an exception out of that ends the app however it's handled.
        private static void Quit(IClassicDesktopStyleApplicationLifetime desktop, string appName)
        {
            Logger.Information("Quitting after an unhandled exception");
            s_quitChosen = true;
            try
            {
                // Owned windows close with their owners.
                foreach (var window in desktop.Windows.Where(w => w.Owner is null).ToList())
                    window.Close();
            }
            catch (Exception ex) when (s_exiting)
            {
                Logger.Error(ex, "Unhandled exception as the app exited");
                return;
            }
            catch (Exception ex)
            {
                if (s_gate.QuitFailed())
                {
                    Logger.Warning("Quitting failed, so the crash dialog is shown for why");
                    if (Handle(desktop, appName, ex))
                        return;
                }
                else
                {
                    Logger.Warning("Quitting failed again, so the app ends");
                }

                // To the dispatcher, where the gate leaves it unhandled, to end the app.
                Dispatcher.UIThread.Post(() => ExceptionDispatchInfo.Throw(ex));
                return;
            }

            // A window cancelled its close to ask first. If the user goes on, its close ends the app; if they cancel, it
            // carries on, and when it does exit it's still with 1: the two can't be told apart from here.
            if (!s_exiting)
                Logger.Information("Quitting was put off: a window is asking about unsaved changes first");
        }
    }
}
