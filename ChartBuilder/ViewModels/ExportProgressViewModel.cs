using System;
using System.Globalization;
using ChartBuilder.Resources;
using ChartShared.Core.Jobs;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The export progress dialog: what Save and pack is doing after the export dialog closes —
    /// transcoding each stem, then packing — with a determinate bar and a Cancel button.
    /// Transcoding owns the first 85% of the bar, packing the rest.
    /// </summary>
    public sealed partial class ExportProgressViewModel : ViewModelBase
    {
        private const double PackingStartsAt = 85;

        /// <summary>What the dialog says is happening.</summary>
        [ObservableProperty]
        public partial string StatusText { get; private set; } = Strings.ExportProgressStarting;

        /// <summary>How far the bar has got, from 0 to 100.</summary>
        [ObservableProperty]
        public partial double ProgressValue { get; private set; }

        /// <summary>Cancels the export; the dialog closes once the work stops.</summary>
        public event EventHandler? CancelRequested;

        /// <summary>Takes in an <see cref="ExportProgress"/> report, on the UI thread.</summary>
        public void Report(ExportProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);

            if (progress.Phase == ExportPhase.Transcoding)
            {
                int percent = (int)Math.Round(Math.Clamp(progress.Fraction, 0, 1) * 100);
                StatusText = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.ExportProgressTranscodingFormat,
                    progress.CurrentStem,
                    Math.Min(progress.StemIndex + 1, Math.Max(progress.StemCount, 1)),
                    Math.Max(progress.StemCount, 1),
                    percent);
                ProgressValue = Math.Clamp(progress.Fraction, 0, 1) * PackingStartsAt;
            }
            else
            {
                int percent = (int)Math.Round(Math.Clamp(progress.Fraction, 0, 1) * 100);
                StatusText = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.ExportProgressPackingFormat,
                    progress.CurrentStem,
                    percent);
                ProgressValue = PackingStartsAt + (Math.Clamp(progress.Fraction, 0, 1) * (100 - PackingStartsAt));
            }
        }

        [RelayCommand]
        private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
