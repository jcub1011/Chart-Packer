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
    /// seconds, except while the download is still running.
    /// </summary>
    public sealed partial class NoticeViewModel : ViewModelBase
    {
        private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(6);

        private readonly IFileRevealService _reveal;
        private readonly DispatcherTimer _hideTimer;
        private IReadOnlyList<string> _paths = [];

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

        /// <summary>The download is still running, so there's nothing to show yet.</summary>
        [ObservableProperty]
        public partial bool IsBusy { get; private set; }

        /// <summary>There's something saved to show in the file manager.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
        public partial bool CanReveal { get; private set; }

        /// <summary>Says a download is under way, until <see cref="ShowSaved"/> or <see cref="ShowError"/>.</summary>
        public void ShowBusy(string message) => Show(message, [], isError: false, isBusy: true);

        /// <summary>Says <paramref name="paths"/> were saved, with a button to show them.</summary>
        public void ShowSaved(string message, IReadOnlyList<string> paths) => Show(message, paths, isError: false, isBusy: false);

        /// <summary>Says a download failed; <paramref name="paths"/> are anything it saved before it did.</summary>
        public void ShowError(string message, IReadOnlyList<string>? paths = null) => Show(message, paths ?? [], isError: true, isBusy: false);

        private void Show(string message, IReadOnlyList<string> paths, bool isError, bool isBusy)
        {
            _paths = paths;
            Message = message;
            IsError = isError;
            IsBusy = isBusy;
            CanReveal = paths.Count > 0;
            IsVisible = true;

            _hideTimer.Stop();
            if (!isBusy)
                _hideTimer.Start();
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

            IsVisible = false;
        }
    }
}
