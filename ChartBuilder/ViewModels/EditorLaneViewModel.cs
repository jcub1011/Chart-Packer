using System.Collections.Generic;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// One of the Editor's lanes: a part's highway, or a vocal part. Only its header so far, which picks the part and
    /// closes the lane; its body is a placeholder.
    /// </summary>
    public sealed partial class EditorLaneViewModel : ViewModelBase
    {
        private readonly LaneDockViewModel _dock;

        internal EditorLaneViewModel(LaneDockViewModel dock, DockLane lane)
        {
            _dock = dock;
            Lane = lane;
            Refresh();
        }

        /// <summary>The lane in the dock's layout.</summary>
        public DockLane Lane { get; }

        [ObservableProperty]
        public partial string Title { get; private set; } = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Placeholder))]
        public partial bool IsVocal { get; private set; }

        /// <summary>Stands in for the lane's editor.</summary>
        public string Placeholder => IsVocal ? Strings.LaneVocalsPlaceholder : Strings.LaneHighwayPlaceholder;

        /// <summary>The parts this lane can show instead: each part not shown in another lane.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LaneMenuItem> PartMenu { get; private set; } = [];

        /// <summary>The parts a new lane can show, as <see cref="LaneDockViewModel.AddMenu"/>.</summary>
        public IReadOnlyList<LaneMenuItem> AddMenu => _dock.AddMenu;

        [RelayCommand]
        private void Close() => _dock.Layout.Close(Lane);

        /// <summary>Takes in the lane's part, and what the other lanes show, after the layout changes.</summary>
        internal void Refresh()
        {
            var layout = _dock.Layout;
            Title = LaneMenuItem.TitleOf(Lane.Part);
            IsVocal = Lane.Part.IsVocal;
            PartMenu = LaneMenuItem.Build(
                canPick: p => p == Lane.Part || !layout.Contains(p),
                isCurrent: p => p == Lane.Part,
                pick: p => layout.SetPart(Lane, p));
            OnPropertyChanged(nameof(AddMenu));
        }
    }
}
