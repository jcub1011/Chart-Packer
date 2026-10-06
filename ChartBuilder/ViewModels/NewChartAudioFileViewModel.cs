using System;
using System.Collections.Generic;
using ChartPacker.Core.Scanning;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>An audio file in the new-chart wizard's list, with the stem it's to be stored as.</summary>
    public sealed partial class NewChartAudioFileViewModel : ViewModelBase
    {
        private readonly Action<NewChartAudioFileViewModel> _remove;
        private readonly Action _stemChanged;

        public NewChartAudioFileViewModel(string path, string? stem, Action<NewChartAudioFileViewModel> remove, Action stemChanged)
        {
            Path = path;
            _stem = stem;
            _remove = remove;
            _stemChanged = stemChanged;
        }

        /// <summary>The file.</summary>
        public string Path { get; }

        public string FileName => System.IO.Path.GetFileName(Path);

        /// <summary>The stems a file can be stored as.</summary>
        public static IReadOnlyList<string> StemChoices => AudioStems.Stems;

        private string? _stem;

        /// <summary>The stem it's stored as; <see langword="null"/> until one is chosen.</summary>
        public string? Stem
        {
            get => _stem;
            set
            {
                if (SetProperty(ref _stem, value))
                    _stemChanged();
            }
        }

        /// <summary>The file's length, format and sample rate, once read.</summary>
        [ObservableProperty]
        public partial string? Details { get; set; }

        /// <summary>The file is still being read and decoded.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsOk))]
        public partial bool IsChecking { get; set; } = true;

        /// <summary>Why the file can't be used, e.g. it doesn't decode; <see langword="null"/> when it can.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblem))]
        [NotifyPropertyChangedFor(nameof(IsOk))]
        public partial string? Problem { get; set; }

        public bool HasProblem => Problem is not null;

        public bool IsOk => !IsChecking && Problem is null;

        /// <summary>What the file's tags say, once read; used to fill in the song's details.</summary>
        public AudioFileInfo? Info { get; set; }

        [RelayCommand]
        private void Remove() => _remove(this);
    }
}
