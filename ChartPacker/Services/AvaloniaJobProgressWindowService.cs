using System;
using Avalonia.Controls;
using ChartPacker.UI.Views;
using ChartPacker.ViewModels;
using ChartPacker.Views;

namespace ChartPacker.Services
{
    /// <summary>
    /// Shows one job progress window at a time. It isn't modal, so the main window stays usable while it
    /// is open, and it's owned by the main window, so it stays above it and closes with it.
    /// </summary>
    public sealed class AvaloniaJobProgressWindowService(Func<Window?> owner) : IJobProgressWindowService
    {
        private JobProgressWindow? _open;

        public void Show(JobViewModel job)
        {
            if (_open is not null)
            {
                _open.Activate();
                return;
            }

            if (owner() is not { } window)
                return;

            var dialog = new JobProgressWindow { DataContext = job };
            dialog.Closed += (_, _) => _open = null;
            _open = dialog;
            WindowChrome.UseOwnerIcon(dialog, window);
            dialog.Show(window);
        }
    }
}
