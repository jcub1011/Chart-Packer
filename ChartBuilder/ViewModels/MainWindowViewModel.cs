using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker.Core.Launching;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>Chart Builder's main window: the home screen until the chart editor comes (charter-spec §4.5).</summary>
    public sealed partial class MainWindowViewModel(INewChartWindowService newChartWindow, ISettingsWindowService settingsWindow) : ViewModelBase
    {
        /// <summary>What happened last, e.g. the song a New chart made, or why the command line couldn't be used.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMessage))]
        public partial string? Message { get; set; }

        /// <summary>Whether <see cref="Message"/> is a problem.</summary>
        [ObservableProperty]
        public partial bool IsError { get; set; }

        public bool HasMessage => Message is not null;

        /// <summary>Opens what the command line asks for. Called once, when the window is first shown.</summary>
        public Task StartAsync(BuilderArgs args)
        {
            switch (args)
            {
                case { Problem: { } problem }:
                    ShowError(string.Format(CultureInfo.CurrentCulture, ProblemFormat(problem.Kind), problem.Argument));
                    break;
                case { Start: BuilderStart.NewChart }:
                    return NewChartAsync(args.AudioFiles);
                case { Start: BuilderStart.OpenSong }:
                    ShowError(string.Format(CultureInfo.CurrentCulture, Strings.OpenSongUnavailableFormat, args.SongPath));
                    break;
                case { Start: BuilderStart.Calibrate }:
                    ShowError(Strings.CalibrateUnavailable);
                    break;
            }

            return Task.CompletedTask;
        }

        /// <summary>Opens the New chart wizard with no audio files yet.</summary>
        [RelayCommand]
        private Task NewChartAsync() => NewChartAsync([]);

        /// <summary>
        /// Opens the New chart wizard with <paramref name="audioFiles"/>, e.g. ones dropped on the window, and says
        /// where the song was made. Never throws: it's started without being awaited, from the command line or a
        /// drop, so a failure is shown on the home screen instead.
        /// </summary>
        public async Task NewChartAsync(IReadOnlyList<string> audioFiles)
        {
            try
            {
                if (await newChartWindow.ShowAsync(audioFiles) is not { } created)
                    return;

                IsError = false;
                Message = string.Format(CultureInfo.CurrentCulture, Strings.ChartCreatedFormat, created);
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would be lost without a word.
                Trace.TraceError($"The New chart wizard failed: {ex}");
                ShowError(string.Format(CultureInfo.CurrentCulture, Strings.UnexpectedErrorFormat, ex.Message));
            }
        }

        [RelayCommand]
        private Task OpenSettingsAsync() => settingsWindow.ShowAsync();

        private void ShowError(string message)
        {
            IsError = true;
            Message = message;
        }

        private static string ProblemFormat(BuilderArgsProblemKind kind) => kind switch
        {
            BuilderArgsProblemKind.UnknownOption => Strings.ArgsUnknownOptionFormat,
            _ => Strings.ArgsUnexpectedArgumentFormat,
        };
    }
}
