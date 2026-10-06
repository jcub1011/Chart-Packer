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
                    // The audio files given aren't used until the editor can set a chart up from them.
                    NewChart();
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
        public void NewChart() => editors.OpenEditor();

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
