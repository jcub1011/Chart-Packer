using System;
using System.Globalization;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// A row of the Pending changes list (charter-spec C17 item 2): a <c>song.ini</c> change saving makes, which can be
    /// reverted, and restored once it has been, unless the chart is read by it.
    /// </summary>
    public sealed class PendingChangeItem
    {
        public PendingChangeItem(PendingSongIniChange change, bool isReverted, Action<string> revert, Action<string> restore)
        {
            Change = change;
            IsReverted = isReverted;
            Text = change.Value is { } value
                ? string.Format(CultureInfo.CurrentCulture, Strings.PendingChangeFormat, change.Key, value)
                : string.Format(CultureInfo.CurrentCulture, Strings.PendingChangeRemovedFormat, change.Key);
            Reason = ReasonOf(change.Reason);
            RevertCommand = new RelayCommand(() => revert(change.Key), () => CanRevert && !IsReverted);
            RestoreCommand = new RelayCommand(() => restore(change.Key), () => IsReverted);
        }

        public PendingSongIniChange Change { get; }

        /// <summary>The change, as <c>key = value</c>, or that the key is removed.</summary>
        public string Text { get; }

        /// <summary>Why it's made, for its tooltip.</summary>
        public string Reason { get; }

        public bool CanRevert => Change.CanRevert;

        /// <summary>Whether the user left it out of saves.</summary>
        public bool IsReverted { get; }

        /// <summary>Whether it's made by the next save.</summary>
        public bool IsApplied => !IsReverted;

        /// <summary>Why it can't be reverted, or null when it can.</summary>
        public string? NotRevertableReason => CanRevert ? null : Strings.PendingChangeNotRevertableToolTip;

        public IRelayCommand RevertCommand { get; }

        public IRelayCommand RestoreCommand { get; }

        private static string ReasonOf(PendingChangeReason reason) => reason switch
        {
            PendingChangeReason.ImportedValue => Strings.PendingReasonImportedValue,
            PendingChangeReason.Charter => Strings.PendingReasonCharter,
            PendingChangeReason.Delay => Strings.PendingReasonDelay,
            PendingChangeReason.SongLength => Strings.PendingReasonSongLength,
            PendingChangeReason.PartRating => Strings.PendingReasonPartRating,
            PendingChangeReason.DrumLayout => Strings.PendingReasonDrumLayout,
            PendingChangeReason.ImportSettings => Strings.PendingReasonImportSettings,
            PendingChangeReason.MultiplierNote => Strings.PendingReasonMultiplierNote,
            PendingChangeReason.DelayFromDownbeat => Strings.PendingReasonDelayFromDownbeat,
            PendingChangeReason.SuggestedRating => Strings.PendingReasonSuggestedRating,
            _ => "",
        };
    }
}
