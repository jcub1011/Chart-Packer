using System;
using System.Collections.Generic;
using System.Linq;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Editor's lanes, docked in a <see cref="LaneDockLayout"/>. The view draws the layout and moves lanes in it;
    /// this keeps one <see cref="EditorLaneViewModel"/> per open lane, and the menu of parts to add.
    /// </summary>
    public sealed partial class LaneDockViewModel : ViewModelBase
    {
        private readonly Dictionary<int, EditorLaneViewModel> _lanes = [];

        /// <param name="editor">The editor the lanes' canvases edit; none for lanes with only their headers.</param>
        public LaneDockViewModel(LaneDockLayout layout, ChartEditorViewModel? editor = null)
        {
            Layout = layout;
            Editor = editor;
            layout.Changed += (_, _) => Refresh();
            Refresh();
        }

        public LaneDockLayout Layout { get; }

        /// <summary>The editor the lanes' canvases edit.</summary>
        public ChartEditorViewModel? Editor { get; }

        /// <summary>Every open lane's view model.</summary>
        public IEnumerable<EditorLaneViewModel> All => _lanes.Values;

        /// <summary>Raised after lanes are added, moved, closed or change part, once the lanes' view models are up to date.</summary>
        public event EventHandler? LayoutChanged;

        /// <summary>Whether any lane is open.</summary>
        [ObservableProperty]
        public partial bool HasLanes { get; private set; }

        /// <summary>The parts a new lane can show: each part no lane shows.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LaneMenuItem> AddMenu { get; private set; } = [];

        /// <summary>The view model of <paramref name="lane"/>, an open lane.</summary>
        public EditorLaneViewModel LaneFor(DockLane lane) => _lanes[lane.Id];

        /// <summary>
        /// Docks the lane <paramref name="laneId"/> against <paramref name="edge"/> of the lane <paramref name="targetId"/>,
        /// or of the whole dock when it's null. Does nothing when either isn't open, as when the layout changed during a drag.
        /// </summary>
        public void Move(int laneId, int? targetId, DockEdge edge)
        {
            if (Layout.Find(laneId) is not { } lane)
                return;

            DockLane? target = null;
            if (targetId is { } id && (target = Layout.Find(id)) is null)
                return;

            Layout.Move(lane, target, edge);
        }

        private void Refresh()
        {
            var open = Layout.Lanes.ToList();
            foreach (int id in _lanes.Keys.Except(open.Select(l => l.Id)).ToList())
                _lanes.Remove(id);

            HasLanes = open.Count > 0;
            AddMenu = LaneMenuItem.Build(canPick: p => !Layout.Contains(p), isCurrent: _ => false, pick: p => Layout.Add(p));

            foreach (var lane in open)
            {
                if (_lanes.TryGetValue(lane.Id, out var vm))
                    vm.Refresh();
                else
                    _lanes.Add(lane.Id, new EditorLaneViewModel(this, lane));
            }

            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
