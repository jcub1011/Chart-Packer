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
    /// <summary>Chart Builder's launcher: the home screen, which hands over to the Editor window (charter-spec §4.5).</summary>
    public sealed partial class MainWindowViewModel(IEditorWindowService editors, ISettingsWindowService settingsWindow) : ViewModelBase
    {
        /// <summary>What happened last, e.g. why the command line couldn't be used.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMessage))]
        public partial string? Message { get; set; }

        /// <summary>Whether <see cref="Message"/> is a problem.</summary>
        [ObservableProperty]
        public partial bool IsError { get; set; }

        public bool HasMessage => Message is not null;

        /// <summary>Opens what the command line asks for. Called once, when the window is first shown.</summary>
        public void Start(BuilderArgs args)
        {
            switch (args)
            {
                case { Problem: { } problem }:
                    ShowError(string.Format(CultureInfo.CurrentCulture, ProblemFormat(problem.Kind), problem.Argument));
                    break;
                case { Start: BuilderStart.NewChart }:
                    StartNewChart(args.AudioFiles);
                    break;
                case { Start: BuilderStart.OpenSong }:
                    ShowError(string.Format(CultureInfo.CurrentCulture, Strings.OpenSongUnavailableFormat, args.SongPath));
                    break;
                case { Start: BuilderStart.Calibrate }:
                    ShowError(Strings.CalibrateUnavailable);
                    break;
            }
        }

        /// <summary>Closes the launcher and opens an empty Editor window.</summary>
        [RelayCommand]
        private void NewChart() => StartNewChart([]);

        /// <summary>
        /// Closes the launcher and opens an empty Editor window, which says <paramref name="audioFiles"/>, e.g. ones
        /// dropped on the window, weren't added. Never throws: it's started from the command line, a drop or a
        /// command, so a failure is shown on the home screen instead.
        /// </summary>
        public void StartNewChart(IReadOnlyList<string> audioFiles)
        {
            try
            {
                editors.OpenEditor(audioFiles);
            }
            catch (Exception ex)
            {
                // Nothing above this catches it, so anything not caught here would take the app down.
                Trace.TraceError($"Opening the Editor failed: {ex}");
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
