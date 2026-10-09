using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.UI.ViewModels
{
    /// <summary>
    /// What a difficulty's flyout (<see cref="Views.DifficultyPicker"/>) shows and sets: Chart Packer's inspector's
    /// instrument icons (<see cref="InstrumentDifficultyViewModel"/>), and Chart Builder's Chart settings' ratings.
    /// </summary>
    public interface IDifficultyPicker
    {
        /// <summary>The part's name, heading the flyout.</summary>
        string Name { get; }

        /// <summary>The <c>song.ini</c> key the difficulty is kept under, shown under the name.</summary>
        string Key { get; }

        /// <summary>The part's notes on each difficulty, or harmony line; none when they aren't known.</summary>
        IReadOnlyList<NoteCountRow> NoteCounts { get; }

        bool HasNoteCounts { get; }

        /// <summary>The tiers offered as buttons: unset, then 0 to 6.</summary>
        IReadOnlyList<DifficultyPresetViewModel> Presets { get; }

        /// <summary>The number box's value, for a tier past the buttons.</summary>
        decimal? OtherValue { get; set; }

        /// <summary>Sets the number box's tier.</summary>
        IRelayCommand ApplyOtherCommand { get; }

        /// <summary>Puts back the difficulty as it was before the pending change.</summary>
        IRelayCommand RevertCommand { get; }

        /// <summary>Whether the difficulty has a pending change, which <see cref="RevertCommand"/> puts back.</summary>
        bool IsEdited { get; }
    }
}
