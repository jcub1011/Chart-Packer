using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartPacker.Core.Library;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>One item of the content filters, e.g. Guitar, that cycles between Either, Includes and Excludes.</summary>
    /// <param name="name">What it's shown as.</param>
    /// <param name="flag">Its bit in the group's flags; unused outside a group.</param>
    /// <param name="changed">Called when <see cref="State"/> changes.</param>
    public sealed partial class FilterChipViewModel(string name, int flag, Action changed) : ObservableObject
    {
        public string Name { get; } = name;

        public int Flag { get; } = flag;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIncluded), nameof(IsExcluded), nameof(AutomationName), nameof(ToolTip))]
        public partial FilterPresence State { get; set; }

        public bool IsIncluded => State == FilterPresence.Includes;

        public bool IsExcluded => State == FilterPresence.Excludes;

        /// <summary>The item and its state, e.g. "Guitar: Includes".</summary>
        public string AutomationName => string.Format(CultureInfo.CurrentCulture, Strings.FilterChipStateFormat, Name, StateName(State));

        public string ToolTip => AutomationName + Environment.NewLine + Strings.FilterChipHint;

        /// <summary>Either, then Includes, then Excludes, then Either again.</summary>
        [RelayCommand]
        private void Cycle() => State = State switch
        {
            FilterPresence.Either => FilterPresence.Includes,
            FilterPresence.Includes => FilterPresence.Excludes,
            _ => FilterPresence.Either,
        };

        /// <summary>Straight back to Either, as a right-click does.</summary>
        [RelayCommand]
        private void Reset() => State = FilterPresence.Either;

        partial void OnStateChanged(FilterPresence value) => changed();

        private static string StateName(FilterPresence state) => state switch
        {
            FilterPresence.Includes => Strings.FilterChipIncludes,
            FilterPresence.Excludes => Strings.FilterChipExcludes,
            _ => Strings.FilterChipEither,
        };
    }

    /// <summary>A group of <see cref="FilterChipViewModel"/>s, such as the instruments, with how its included chips combine.</summary>
    /// <param name="label">The group's heading.</param>
    /// <param name="chips">Each chip's name and bit.</param>
    /// <param name="changed">Called when a chip or <see cref="MatchAny"/> changes.</param>
    public sealed partial class FilterChipGroupViewModel(string label, IEnumerable<(string Name, int Flag)> chips, Action changed) : ObservableObject
    {
        private readonly Action _changed = changed;

        public string Label { get; } = label;

        /// <summary>The accessible name of the All/Any buttons.</summary>
        public string MatchName { get; } = string.Format(CultureInfo.CurrentCulture, Strings.FilterMatchName, label);

        public IReadOnlyList<FilterChipViewModel> Chips { get; } = [.. chips.Select(c => new FilterChipViewModel(c.Name, c.Flag, changed))];

        /// <summary>A song needs at least one included chip rather than all of them.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MatchAll))]
        public partial bool MatchAny { get; set; }

        /// <summary>The opposite of <see cref="MatchAny"/>, for the All button.</summary>
        public bool MatchAll
        {
            get => !MatchAny;
            set => MatchAny = !value;
        }

        public SetMatch Match => MatchAny ? SetMatch.Any : SetMatch.All;

        /// <summary>The bits of the included chips.</summary>
        public int Included => MaskOf(FilterPresence.Includes);

        /// <summary>The bits of the excluded chips.</summary>
        public int Excluded => MaskOf(FilterPresence.Excludes);

        /// <summary>Every chip back to Either, and back to matching all.</summary>
        public void Clear()
        {
            foreach (var chip in Chips)
                chip.State = FilterPresence.Either;
            MatchAny = false;
        }

        partial void OnMatchAnyChanged(bool value) => _changed();

        private int MaskOf(FilterPresence state) => Chips.Where(c => c.State == state).Aggregate(0, (mask, c) => mask | c.Flag);
    }
}
