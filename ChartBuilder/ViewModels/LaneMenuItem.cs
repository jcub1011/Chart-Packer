using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// One item of a lane menu: a group (an instrument, or the vocals) whose <see cref="Items"/> are its parts, or a
    /// part, which <see cref="Command"/> picks.
    /// </summary>
    public sealed class LaneMenuItem
    {
        private LaneMenuItem(string header, IReadOnlyList<LaneMenuItem>? items, ICommand? command, bool isEnabled, bool isChecked)
        {
            Header = header;
            Items = items;
            Command = command;
            IsEnabled = isEnabled;
            IsChecked = isChecked;
        }

        public string Header { get; }

        /// <summary>A group's parts; null for a part.</summary>
        public IReadOnlyList<LaneMenuItem>? Items { get; }

        public ICommand? Command { get; }

        /// <summary>Whether the part can be picked; for a group, whether any of its parts can.</summary>
        public bool IsEnabled { get; }

        /// <summary>Whether this is the part the lane shows.</summary>
        public bool IsChecked { get; }

        /// <summary>
        /// Every part a lane can show, grouped by instrument, then the vocals. A part <paramref name="canPick"/> refuses
        /// is shown disabled; <paramref name="isCurrent"/> marks the lane's own.
        /// </summary>
        public static IReadOnlyList<LaneMenuItem> Build(Func<LanePart, bool> canPick, Func<LanePart, bool> isCurrent, Action<LanePart> pick)
        {
            var groups = new List<LaneMenuItem>();
            foreach (var instrument in NewChartSettings.Instruments)
            {
                var parts = Enum.GetValues<ChartDifficulty>()
                    .Select(d => (LanePart.Highway(instrument, d), ChartLabels.DifficultyName(d)));
                groups.Add(Group(ChartLabels.InstrumentName(instrument), parts, canPick, isCurrent, pick));
            }

            var vocals = Enum.GetValues<VocalTrack>().Select(v => (LanePart.Vocals(v), VocalName(v)));
            groups.Add(Group(Strings.LaneVocalsGroup, vocals, canPick, isCurrent, pick));
            return groups;
        }

        /// <summary>An item that runs <paramref name="run"/>, checked when <paramref name="isChecked"/>.</summary>
        public static LaneMenuItem Choice(string header, bool isChecked, Action run) =>
            new(header, null, new RelayCommand(run), isEnabled: true, isChecked);

        /// <summary>An item that runs <paramref name="run"/>, greyed out unless <paramref name="isEnabled"/>.</summary>
        public static LaneMenuItem Action(string header, bool isEnabled, Action run) =>
            new(header, null, new RelayCommand(run), isEnabled, isChecked: false);

        /// <summary>A submenu of <paramref name="items"/>, greyed out when none of them can be picked.</summary>
        public static LaneMenuItem Submenu(string header, IReadOnlyList<LaneMenuItem> items) =>
            new(header, items, null, items.Any(static i => i.IsEnabled), isChecked: false);

        /// <summary>A line between groups of items.</summary>
        public static LaneMenuItem Separator { get; } = new(string.Empty, null, null, isEnabled: false, isChecked: false);

        public bool IsSeparator => ReferenceEquals(this, Separator);

        /// <summary>
        /// What a lane showing <paramref name="part"/> is headed, e.g. "Guitar" or "Harmony 2": a highway's difficulty is
        /// shown beside it, by its letters.
        /// </summary>
        public static string TitleOf(LanePart part) => part.Track is { } track
            ? ChartLabels.InstrumentName(track.Instrument)
            : VocalName(part.Vocal!.Value);

        private static string VocalName(VocalTrack vocal) => vocal switch
        {
            VocalTrack.Vocals => Strings.LaneVocals,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.LaneHarmonyFormat, (int)vocal),
        };

        private static LaneMenuItem Group(
            string header,
            IEnumerable<(LanePart Part, string Header)> parts,
            Func<LanePart, bool> canPick,
            Func<LanePart, bool> isCurrent,
            Action<LanePart> pick)
        {
            var items = parts
                .Select(p => new LaneMenuItem(p.Header, null, new RelayCommand(() => pick(p.Part)), canPick(p.Part), isCurrent(p.Part)))
                .ToList();
            return new LaneMenuItem(header, items, null, items.Any(i => i.IsEnabled), items.Any(i => i.IsChecked));
        }
    }
}
