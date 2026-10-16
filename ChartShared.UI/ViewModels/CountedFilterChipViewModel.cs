using System;
using System.Globalization;
using ChartShared.UI.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartShared.UI.ViewModels
{
    /// <summary>
    /// A chip of a filter, e.g. the Validator's Quick fix or the chart issues' Guitar, that's on or off, with how many
    /// listed things it would match.
    /// </summary>
    /// <param name="id">What it matches: a type's ID, a metadata key, a part, or a name of its own.</param>
    /// <param name="name">What it's shown as.</param>
    /// <param name="toolTipFormat">Its tooltip: {0} is <paramref name="name"/>, {1} how many it matches.</param>
    /// <param name="changed">Called when <see cref="IsOn"/> changes.</param>
    public sealed partial class CountedFilterChipViewModel(string id, string name, string toolTipFormat, Action changed) : ObservableObject
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        /// <summary>How many listed things it matches.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label), nameof(ToolTip))]
        public partial int Count { get; set; }

        /// <summary>The name and count, e.g. "Empty value (14)".</summary>
        public string Label => string.Format(CultureInfo.CurrentCulture, SharedStrings.FilterChipFormat, Name, Count);

        public string ToolTip => string.Format(CultureInfo.CurrentCulture, toolTipFormat, Name, Count);

        /// <summary>Only what it matches is shown, along with what any other chip on in its group matches.</summary>
        [ObservableProperty]
        public partial bool IsOn { get; set; }

        partial void OnIsOnChanged(bool value) => changed();
    }
}
