using System;
using System.Collections.Generic;
using Avalonia.Threading;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// The short note along the bottom of the main window that says what a download did: where it
    /// saved, with a button to show it, or why it couldn't. It goes away on its own after a few
    /// seconds, except while a download is still running: then it goes back to saying so.
    /// </summary>
    public sealed partial class NoticeViewModel : ViewModelBase
    {
        private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(6);

        private readonly IFileRevealService _reveal;
        private readonly DispatcherTimer _hideTimer;
        private readonly List<BusyNotice> _running = [];
        private IReadOnlyList<string> _paths = [];

        // The running download whose message is shown; null while a result is shown, or nothing is.
        private BusyNotice? _shown;

        public NoticeViewModel(IFileRevealService reveal)
        {
            _reveal = reveal;
            _hideTimer = new DispatcherTimer { Interval = ShowFor };
            _hideTimer.Tick += (_, _) => Dismiss();
        }

        /// <summary>The menu item's name for showing a file in the file manager.</summary>
        public static string RevealLabel => FileRevealService.RevealLabel;

        [ObservableProperty]
        public partial string? Message { get; private set; }

        [ObservableProperty]
        public partial bool IsVisible { get; private set; }

        [ObservableProperty]
        public partial bool IsError { get; private set; }

        /// <summary>A download is still running, so there's nothing to show yet.</summary>
        [ObservableProperty]
        public partial bool IsBusy { get; private set; }

        /// <summary>There's something saved to show in the file manager.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
        public partial bool CanReveal { get; private set; }

        /// <summary>
        /// Says a download is under way, until the result is disposed. A result already shown stays
        /// for its time first, so it can still be acted on.
        /// </summary>
        public BusyNotice BeginBusy(string message)
        {
            var busy = new BusyNotice(this, message);
            _running.Add(busy);
            if (!_hideTimer.IsEnabled)
                ShowBusy(busy);
            return busy;
        }

        /// <summary>Says <paramref name="paths"/> were saved, with a button to show them.</summary>
        public void ShowSaved(string message, IReadOnlyList<string> paths) => ShowResult(message, paths, isError: false);

        /// <summary>Says a download failed; <paramref name="paths"/> are anything it saved before it did.</summary>
        public void ShowError(string message, IReadOnlyList<string>? paths = null) => ShowResult(message, paths ?? [], isError: true);

        private void ShowResult(string message, IReadOnlyList<string> paths, bool isError)
        {
            _shown = null;
            Show(message, paths, isError, isBusy: false);
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void ShowBusy(BusyNotice busy)
        {
            _shown = busy;
            _hideTimer.Stop();
            Show(busy.Message, [], isError: false, isBusy: true);
        }

        private void Show(string message, IReadOnlyList<string> paths, bool isError, bool isBusy)
        {
            _paths = paths;
            Message = message;
            IsError = isError;
            IsBusy = isBusy;
            CanReveal = paths.Count > 0;
            IsVisible = true;
        }

        /// <summary>Goes back to the latest download still running, or hides when there's none.</summary>
        private void ShowRunningOrHide()
        {
            if (_running.Count > 0)
            {
                ShowBusy(_running[^1]);
                return;
            }

            _shown = null;
            IsBusy = false;
            IsVisible = false;
        }

        private void Update(BusyNotice busy)
        {
            if (ReferenceEquals(_shown, busy))
                Message = busy.Message;
        }

        private void End(BusyNotice busy)
        {
            if (!_running.Remove(busy))
                return;

            // It ended without saying how; whatever result was shown stays.
            if (ReferenceEquals(_shown, busy))
                ShowRunningOrHide();
        }

        [RelayCommand(CanExecute = nameof(CanReveal))]
        private void Reveal()
        {
            _reveal.Reveal(_paths);
            Dismiss();
        }

        [RelayCommand]
        private void Dismiss()
        {
            _hideTimer.Stop();
            if (IsBusy)
                return;

            ShowRunningOrHide();
        }

        /// <summary>A download the notice says is running, from <see cref="BeginBusy"/>. Disposing it says it's over.</summary>
        public sealed class BusyNotice : IDisposable
        {
            private readonly NoticeViewModel _notice;

            internal BusyNotice(NoticeViewModel notice, string message)
            {
                _notice = notice;
                Message = message;
            }

            public string Message { get; private set; }

            /// <summary>Changes what the notice says about the download, as it moves on.</summary>
            public void Update(string message)
            {
                Message = message;
                _notice.Update(this);
            }

            public void Dispose() => _notice.End(this);
        }
    }
}
