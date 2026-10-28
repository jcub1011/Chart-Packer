using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ChartShared.Core.Diagnostics;
using ChartShared.UI.Resources;
using Serilog;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// Says an exception nothing caught has stopped what the user was doing (<see cref="CrashHandler"/>), and lets them
    /// keep working or quit. Its details can be copied, the log folder opened, and the error reported on GitHub.
    /// Closing it any other way keeps working.
    /// </summary>
    public partial class CrashDialog : Window
    {
        // How long Copy details says Copied.
        private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(2);

        private readonly string _appName = string.Empty;
        private readonly Exception? _exception;
        private readonly string _details = string.Empty;
        private DispatcherTimer? _copied;

        public CrashDialog()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
            ShowCopyLabel();
            DialogAction.Set(LogFolderButton, SharedStrings.OpenLogFolderButton, "InputFolderStroke");
            DialogAction.Set(ReportButton, SharedStrings.CrashReportButton, "LinkStroke");
            DialogAction.Set(QuitButton, SharedStrings.CrashQuitButton, "ClearStroke");
            DialogAction.Set(KeepWorkingButton, SharedStrings.CrashKeepWorkingButton, "CheckStroke");
            DetailsToggleText.Text = SharedStrings.CrashShowDetails;

            // With logging off there's no log to open.
            LogFolderButton.IsVisible = AppLog.IsEnabled;
        }

        /// <param name="appName">The app's name, for the title and the report.</param>
        /// <param name="exception">What nothing caught.</param>
        public CrashDialog(string appName, Exception exception)
            : this()
        {
            _appName = appName;
            _exception = exception;
            _details = CrashReport.Details(
                appName,
                AppVersion.Semantic,
                RuntimeInformation.OSDescription,
                $"{Environment.Version} {RuntimeInformation.ProcessArchitecture}",
                DateTimeOffset.Now,
                exception);

            Title = string.Format(CultureInfo.CurrentCulture, SharedStrings.CrashDialogTitleFormat, appName);
            TitleText.Text = Title;
            TypeText.Text = exception.GetType().FullName;
            MessageText.Text = exception.Message;
            DetailsText.Text = _details;
        }

        /// <summary>Whether the user chose Quit; otherwise they keep working.</summary>
        public bool QuitChosen { get; private set; }

        /// <summary>Whether it closed because the window it was shown over did, before the user chose.</summary>
        public bool OwnerClosed { get; private set; }

        private static ILogger Logger => Log.ForContext<CrashDialog>();

        /// <summary>Says how many more errors came in while the dialog was open.</summary>
        public void ShowExtra(int count)
        {
            MoreErrorsText.Text = count == 1
                ? SharedStrings.CrashOneMoreError
                : string.Format(CultureInfo.CurrentCulture, SharedStrings.CrashMoreErrorsFormat, count);
            MoreErrorsText.IsVisible = count > 0;
        }

        private void OnDetailsToggled(object? sender, RoutedEventArgs e)
        {
            bool open = DetailsToggle.IsChecked == true;
            DetailsPanel.IsVisible = open;
            DetailsToggleText.Text = open ? SharedStrings.CrashHideDetails : SharedStrings.CrashShowDetails;
        }

        private async void OnCopy(object? sender, RoutedEventArgs e)
        {
            if (await CopyDetailsAsync())
                ShowCopied();
        }

        private void OnOpenLogFolder(object? sender, RoutedEventArgs e) => AppLog.OpenFolder();

        // The issue asks for the details, so they're put on the clipboard first, to be pasted in.
        private async void OnReport(object? sender, RoutedEventArgs e)
        {
            if (_exception is null)
                return;

            var url = CrashReport.IssueUrl(_appName, AppVersion.Semantic, _exception);
            try
            {
                if (await CopyDetailsAsync())
                    ShowCopied();
                if (Launcher is not { } launcher || !await launcher.LaunchUriAsync(url))
                    Logger.Warning("Couldn't open a new issue at {Url}", CrashReport.NewIssueUrl);
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would reach the dispatcher, and this dialog again.
                Logger.Warning(ex, "Couldn't open a new issue at {Url}", CrashReport.NewIssueUrl);
            }
        }

        private void OnQuit(object? sender, RoutedEventArgs e)
        {
            QuitChosen = true;
            Close();
        }

        private void OnKeepWorking(object? sender, RoutedEventArgs e) => Close();

        // The clipboard is the window's, so copying is done here. False when the details couldn't be copied.
        private async Task<bool> CopyDetailsAsync()
        {
            if (Clipboard is not { } clipboard)
                return false;

            try
            {
                await clipboard.SetTextAsync(_details);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Couldn't copy the error's details");
                return false;
            }
        }

        // Copy details says Copied for a moment, so the click is seen to have done something.
        private void ShowCopied()
        {
            DialogAction.Set(CopyButton, SharedStrings.CrashCopiedButton, "CheckStroke");
            if (_copied is null)
            {
                _copied = new DispatcherTimer { Interval = CopiedFor };
                _copied.Tick += (_, _) =>
                {
                    _copied.Stop();
                    ShowCopyLabel();
                };
            }

            _copied.Stop();
            _copied.Start();
        }

        private void ShowCopyLabel() => DialogAction.Set(CopyButton, SharedStrings.CrashCopyDetailsButton, "CopyStroke");

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            OwnerClosed = e.CloseReason == WindowCloseReason.OwnerWindowClosing;
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _copied?.Stop();
            base.OnClosed(e);
        }
    }
}
