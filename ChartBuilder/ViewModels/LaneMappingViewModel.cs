using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>A lane a mapping can send a note to.</summary>
    public sealed record LaneChoice(int Lane, string Label);

    /// <summary>One source lane of a lane mapping, and where it goes.</summary>
    public sealed partial class LaneMappingRow : ViewModelBase
    {
        internal LaneMappingRow(string label, IReadOnlyList<LaneChoice> choices, int lane)
        {
            Label = label;
            Choices = choices;
            Target = choices.FirstOrDefault(c => c.Lane == lane) ?? choices[0];
        }

        public string Label { get; }

        public IReadOnlyList<LaneChoice> Choices { get; }

        [ObservableProperty]
        public partial LaneChoice Target { get; set; }
    }

    /// <summary>
    /// The lane mapping a paste between 5-fret and GHL asks for (charter-spec C15 item 3): where each lane of the passage
    /// goes, starting from the one last confirmed that way. Open notes stay open.
    /// </summary>
    public sealed class LaneMappingViewModel : ViewModelBase
    {
        public LaneMappingViewModel(LaneMapping start)
        {
            From = start.From;
            To = start.To;
            var choices = LanesOf(To).Select((name, lane) => new LaneChoice(lane, name)).ToList();
            Rows = [.. LanesOf(From).Select((name, lane) => new LaneMappingRow(name, choices, start.Map(lane)))];
        }

        public ChartInstrumentKind From { get; }

        public ChartInstrumentKind To { get; }

        public string Intro => string.Format(CultureInfo.CurrentCulture, Strings.LaneMappingIntroFormat, FamilyName(From), FamilyName(To));

        public IReadOnlyList<LaneMappingRow> Rows { get; }

        /// <summary>The mapping as the rows have it.</summary>
        public LaneMapping Mapping => new(From, To, [.. Rows.Select(static r => r.Target.Lane)]);

        private static string FamilyName(ChartInstrumentKind kind) => kind == ChartInstrumentKind.SixFret ? Strings.ReduceFamilyGhl : Strings.ReduceFamilyFiveFret;

        private static IReadOnlyList<string> LanesOf(ChartInstrumentKind kind) => kind == ChartInstrumentKind.SixFret
            ?
            [
                string.Format(CultureInfo.CurrentCulture, Strings.LaneNameWhiteFormat, 1),
                string.Format(CultureInfo.CurrentCulture, Strings.LaneNameWhiteFormat, 2),
                string.Format(CultureInfo.CurrentCulture, Strings.LaneNameWhiteFormat, 3),
                string.Format(CultureInfo.CurrentCulture, Strings.LaneNameBlackFormat, 1),
                string.Format(CultureInfo.CurrentCulture, Strings.LaneNameBlackFormat, 2),
                string.Format(CultureInfo.CurrentCulture, Strings.LaneNameBlackFormat, 3),
            ]
            : [Strings.LaneNameGreen, Strings.LaneNameRed, Strings.LaneNameYellow, Strings.LaneNameBlue, Strings.LaneNameOrange];
    }
}
