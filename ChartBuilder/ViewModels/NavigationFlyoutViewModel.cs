using System;
using System.Globalization;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The small flyout in the top right of the focused lane that Ctrl+G and Ctrl+F open: a box for the measure to go to
    /// (Ctrl+G), and the passages found matching the selected notes with a button to go to the next and the previous
    /// (Ctrl+F), and how many there are. It stays open until it's closed.
    /// </summary>
    public sealed partial class NavigationFlyoutViewModel : ViewModelBase
    {
        private readonly EditorSession _session;

        // Found by scanning every note, so kept until the chart changes rather than read afresh with each key typed.
        private long _lastMeasure;

        public NavigationFlyoutViewModel(EditorSession session)
        {
            _session = session;
            _lastMeasure = FindLastMeasure();
            session.Find.Changed += (_, _) => OnFindChanged();
            session.ChartChanged += (_, e) =>
            {
                // A drag raises previews many times a second; the change it ends with is the one that counts.
                if (!e.IsPreview)
                    OnChartChanged();
            };
        }

        /// <summary>Raised when the measure box should take focus, as Ctrl+G opens the flyout.</summary>
        public event EventHandler? MeasureFocusRequested;

        /// <summary>Whether the flyout is open, in the lane that has focus.</summary>
        [ObservableProperty]
        public partial bool IsOpen { get; private set; }

        /// <summary>
        /// Whether the flyout was opened to find (Ctrl+F) rather than to go to a measure (Ctrl+G): it shows the one or the
        /// other, never both.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowsGoTo))]
        public partial bool IsFinding { get; private set; }

        /// <summary>Whether the flyout shows the measure box.</summary>
        public bool ShowsGoTo => !IsFinding;

        /// <summary>What's typed in the measure box; it keeps the last measure typed when the flyout is closed and opened again, measure 1 at first.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Measure))]
        [NotifyCanExecuteChangedFor(nameof(GoCommand))]
        public partial string MeasureText { get; set; } = "1";

        /// <summary>The last measure that can be gone to: the one the chart's last note is in.</summary>
        public long LastMeasure => _lastMeasure;

        /// <summary>The measure typed, or null when it isn't a whole number from 1 to <see cref="LastMeasure"/>.</summary>
        public long? Measure =>
            long.TryParse(MeasureText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long m) && m >= 1 && m <= LastMeasure ? m : null;

        /// <summary>The measure box's tooltip, with the range that can be typed.</summary>
        public string MeasureToolTip => string.Format(CultureInfo.CurrentCulture, Strings.NavMeasureToolTipFormat, LastMeasure);

        /// <summary>Whether there are passages found to go to.</summary>
        public bool HasMatches => _session.Find.HasMatches;

        /// <summary>How many passages were found, and which the cursor was last taken to, e.g. "2 of 5".</summary>
        public string MatchText => HasMatches
            ? string.Format(CultureInfo.CurrentCulture, Strings.NavMatchCountFormat, _session.Find.Current + 1, _session.Find.Matches.Count)
            : Strings.NavNoMatches;

        /// <summary>Opens the flyout on the measure box, with the measure last typed there selected (Ctrl+G).</summary>
        public void OpenGoTo()
        {
            IsFinding = false;
            IsOpen = true;
            MeasureFocusRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Opens the flyout and finds the passages matching the selected notes, or goes to the next found (Ctrl+F).</summary>
        public void OpenFind()
        {
            IsFinding = true;
            IsOpen = true;
            _session.Find.Start();
            OnFindChanged();
        }

        /// <summary>Goes to the next passage found (F3), or the previous when <paramref name="direction"/> is negative; the commands are off with none found.</summary>
        public void StepFind(int direction)
        {
            IsFinding = true;
            IsOpen = true;
            _session.Find.Step(direction);
            OnFindChanged();
        }

        private long FindLastMeasure() => Math.Max(1, MeasureBeat.At(_session.TempoMap, _session.ChartEndTick).Measure);

        // Notes added or removed, or the tempo changed, move the last measure and so the range the box allows.
        private void OnChartChanged()
        {
            long last = FindLastMeasure();
            if (last == _lastMeasure)
                return;
            _lastMeasure = last;
            OnPropertyChanged(nameof(LastMeasure));
            OnPropertyChanged(nameof(Measure));
            OnPropertyChanged(nameof(MeasureToolTip));
            GoCommand.NotifyCanExecuteChanged();
        }

        private void OnFindChanged()
        {
            OnPropertyChanged(nameof(HasMatches));
            OnPropertyChanged(nameof(MatchText));
        }

        [RelayCommand(CanExecute = nameof(CanGo))]
        private void Go()
        {
            if (Measure is { } measure)
                _session.Tools.GoToMeasure(measure);
        }

        private bool CanGo() => Measure is not null;

        [RelayCommand]
        private void Next() => _session.Find.Step(1);

        [RelayCommand]
        private void Previous() => _session.Find.Step(-1);

        [RelayCommand]
        private void Close() => IsOpen = false;
    }
}
