using System;
using System.Globalization;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// A chip of the Validator's filter, e.g. Quick fix or Empty value, that's on or off, with how many listed songs
    /// it would match.
    /// </summary>
    /// <param name="id">What it matches: a type's ID, a metadata key, or a name of its own for the Fix chips.</param>
    /// <param name="name">What it's shown as.</param>
    /// <param name="changed">Called when <see cref="IsOn"/> changes.</param>
    public sealed partial class ValidatorFilterChipViewModel(string id, string name, Action changed) : ObservableObject
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        /// <summary>How many listed songs have an issue it matches.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label), nameof(ToolTip))]
        public partial int Count { get; set; }

        /// <summary>The name and count, e.g. "Empty value (14)".</summary>
        public string Label => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorFilterChipFormat, Name, Count);

        public string ToolTip => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorFilterChipToolTipFormat, Name, Count);

        /// <summary>Only issues it matches are shown, along with those of any other chip on in its group.</summary>
        [ObservableProperty]
        public partial bool IsOn { get; set; }

        partial void OnIsOnChanged(bool value) => changed();
    }
}
