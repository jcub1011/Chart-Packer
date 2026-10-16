using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartShared.Core.Jobs;
using ChartShared.UI.Views;

namespace ChartBuilder.Services
{
    /// <summary>Shows the export progress dialog while Save and pack works: transcoding, then packing, with Cancel.</summary>
    public interface IExportProgressDialogService
    {
        /// <summary>
        /// Shows <paramref name="viewModel"/> while <paramref name="work"/> runs, reporting into it.
        /// </summary>
        /// <returns>Whether the work finished: false when the user cancelled it (nothing was written).</returns>
        /// <exception cref="IOException">The work failed this way.</exception>
        /// <exception cref="UnauthorizedAccessException">The work failed this way.</exception>
        /// <exception cref="InvalidDataException">The work failed this way.</exception>
        Task<bool> RunAsync(ExportProgressViewModel viewModel, Func<IProgress<ExportProgress>, CancellationToken, Task> work);
    }

    /// <summary>Shows an <see cref="ExportProgressWindow"/> over the owner window while the export runs.</summary>
    public sealed class AvaloniaExportProgressDialogService(Func<Window?> owner) : IExportProgressDialogService
    {
        public async Task<bool> RunAsync(ExportProgressViewModel viewModel, Func<IProgress<ExportProgress>, CancellationToken, Task> work)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            ArgumentNullException.ThrowIfNull(work);

            using var cts = new CancellationTokenSource();
            // Progress<T> posts to the UI thread, as this runs on it.
            var progress = new Progress<ExportProgress>(viewModel.Report);
            Task workTask = work(progress, cts.Token);

            if (owner() is not { } window)
            {
                // Headless (tests): run without a dialog; cancellation still flows through.
                try
                {
                    await workTask.ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            var dialog = new ExportProgressWindow { DataContext = viewModel };
            WindowChrome.UseOwnerIcon(dialog, window);
            void RequestCancel(object? sender, EventArgs e) => cts.Cancel();
            void CloseCancel(object? sender, EventArgs e) => cts.Cancel();
            viewModel.CancelRequested += RequestCancel;
            dialog.Closed += CloseCancel;

            bool cancelled = false;
            Exception? error = null;
            try
            {
                // On the UI thread throughout, so closing the dialog below is safe.
                Task<bool?> showTask = dialog.ShowDialog<bool?>(window);
                try
                {
                    await workTask;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (dialog.IsVisible)
                    dialog.Close(!(cancelled || error is not null));
                await showTask;
            }
            finally
            {
                viewModel.CancelRequested -= RequestCancel;
                dialog.Closed -= CloseCancel;
            }

            if (error is not null)
                ExceptionDispatchInfo.Capture(error).Throw();
            return !cancelled;
        }
    }
}
