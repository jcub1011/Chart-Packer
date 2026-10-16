using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Threading;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using ChartShared.UI.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels
{
    /// <summary>
    /// One way to fix a finding, as its right-click menu offers it: on this finding, on every finding of its rule in its
    /// part, or on every finding of its rule.
    /// </summary>
    /// <param name="Label">What it does; null for a rule's only way, so the menu offers Fix, Fix All For and Fix All without a list.</param>
    /// <param name="Fix">Fixes the finding this way.</param>
    /// <param name="FixAllInPart">Fixes every finding of its rule in its part this way, as one step; null when there's no such fix.</param>
    /// <param name="FixAll">Fixes every finding of its rule this way, as one step; null when there's no such fix.</param>
    public sealed record FindingFix(string? Label, Action Fix, Action? FixAllInPart, Action? FixAll);

    /// <summary>
    /// A chart finding, as the issues list shows it: its message, with pills for its rule, its part and its place, Go to
    /// where whoever lists it can take you there, and a right-click menu of its fixes where they can fix it.
    /// </summary>
    public sealed class FindingItem
    {
        private readonly Func<Finding, string?> _placeOf;
        private readonly Action<Finding>? _goTo;
        private readonly Func<Finding, IReadOnlyList<FindingFix>>? _fixesOf;
        private string? _placeText;
        private string? _trackText;
        private string? _instrumentText;
        private IRelayCommand? _goToCommand;

        /// <param name="placeOf">
        /// Where the finding is in the song, in measures and beats (<see cref="ChartLabels.PlaceText"/>), or null for nowhere
        /// in particular; worked out only once the row is shown, or a search gets as far as its place.
        /// </param>
        /// <param name="fixesOf">
        /// The ways to fix a finding, worked out each time its menu opens, so they're of the chart as it is then; none when
        /// it has none. Null where the list can't fix anything, so its rows have no menu.
        /// </param>
        public FindingItem(Finding finding, Func<Finding, string?> placeOf, Action<Finding>? goTo = null, Func<Finding, IReadOnlyList<FindingFix>>? fixesOf = null)
        {
            ArgumentNullException.ThrowIfNull(finding);
            ArgumentNullException.ThrowIfNull(placeOf);
            Finding = finding;
            _placeOf = placeOf;
            _goTo = goTo;
            _fixesOf = fixesOf;
        }

        public Finding Finding { get; }

        public string RuleId => Finding.RuleId;

        public string Message => Finding.Message;

        /// <summary>
        /// The track it's in, as its pill shows it: the part and difficulty ("Guitar · Expert"), the MIDI track it names
        /// ("Harmony 2"), "Chart file" for what reading the file left out, or "Whole chart".
        /// </summary>
        public string TrackText => _trackText ??= Finding.Location is { } location
            ? location.Difficulty is { } difficulty
                ? string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesTrackFormat, ChartLabels.InstrumentName(location.Part), ChartLabels.DifficultyName(difficulty))
                : ChartLabels.InstrumentName(location.Part)
            : InstrumentText;

        /// <summary>The track it's in without its difficulty, as the Instrument filter's chips name it.</summary>
        internal string InstrumentText => _instrumentText ??= Finding.Location is { } location ? ChartLabels.InstrumentName(location.Part)
            : ChartLabels.TrackName(Finding.Track) is { } track ? track
            : Finding.RuleId == ChrRules.Chr086 ? SharedStrings.ChartIssuesChartFile
            : SharedStrings.ChartIssuesWholeChart;

        /// <summary>
        /// Where it is, as its pill shows it: its place in the song, or the song.ini key it's about; empty when it's
        /// nowhere in particular.
        /// </summary>
        public string PlaceText => _placeText ??= _placeOf(Finding) ?? Finding.Key ?? "";

        public bool HasPlace => PlaceText.Length > 0;

        public ChartDifficulty? Difficulty => Finding.Location?.Difficulty;

        public bool HasDifficulty => Difficulty is not null;

        public bool IsEasy => Difficulty == ChartDifficulty.Easy;

        public bool IsMedium => Difficulty == ChartDifficulty.Medium;

        public bool IsHard => Difficulty == ChartDifficulty.Hard;

        public bool IsExpert => Difficulty == ChartDifficulty.Expert;

        /// <summary>The group of the catalogue its rule is in; null for a rule outside it.</summary>
        public ChrCategory? Category => ChrRules.CategoryOf(RuleId);

        public bool IsError => Finding.Severity == Severity.Error;

        public bool IsWarning => Finding.Severity == Severity.Warning;

        public bool IsInfo => Finding.Severity == Severity.Info;

        /// <summary>Whether it can be gone to in the chart.</summary>
        public bool CanGoTo => _goTo is not null;

        public IRelayCommand GoToCommand => _goToCommand ??= new RelayCommand(() => _goTo?.Invoke(Finding));

        /// <summary>Whether its row has a menu of fixes: where the list can fix findings, whether or not this one has a fix.</summary>
        public bool OffersFixes => _fixesOf is not null;

        /// <summary>The ways to fix it now, in the order to offer them; none when it has none.</summary>
        public IReadOnlyList<FindingFix> Fixes() => _fixesOf?.Invoke(Finding) ?? [];

        /// <summary>The menu item that fixes every finding of its rule in its part: "Fix All For Guitar"; empty for one in no part.</summary>
        public string FixAllInPartText => Finding.Location is { } location
            ? string.Format(CultureInfo.CurrentCulture, SharedStrings.FindingFixAllForFormat, ChartLabels.InstrumentName(location.Part))
            : "";
    }

    /// <summary>A heading row of the issues list, over its errors, its warnings or its info findings, with how many it shows.</summary>
    public sealed partial class FindingGroup(Severity severity) : ViewModelBase
    {
        public Severity Severity { get; } = severity;

        public string Title { get; } = severity switch
        {
            Severity.Error => SharedStrings.ChartIssuesErrorsSection,
            Severity.Warning => SharedStrings.ChartIssuesWarningsSection,
            _ => SharedStrings.ChartIssuesInfosSection,
        };

        public bool IsError => Severity == Severity.Error;

        public bool IsWarning => Severity == Severity.Warning;

        public bool IsInfo => Severity == Severity.Info;

        /// <summary>How many findings it heads that the search and filter show.</summary>
        [ObservableProperty]
        public partial int Count { get; private set; }

        internal void SetCount(int count) => Count = count;
    }

    /// <summary>
    /// A chart's findings, as a list in sections by how serious they are (GitHub issue #53): errors, then warnings, then
    /// info, each in the order of their tracks, reading the chart file first, then the chart as a whole, then each part
    /// and difficulty, with what reading the file left out of it. The list is flat, headings among the findings, so a virtualizing list shows it; it's changed in
    /// place as the findings, the search or the filter change, so only the rows that changed are made again.
    /// </summary>
    /// <remarks>For the UI thread only.</remarks>
    public sealed partial class ChartIssuesViewModel : ViewModelBase
    {
        // How long the search waits for typing to stop before the list is filtered.
        private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

        // The sections, in the order they're listed.
        private static readonly Severity[] Sections = [Severity.Error, Severity.Warning, Severity.Info];

        private readonly List<FindingItem> _items = [];

        // Each finding's track's place in the list, within its section: reading the file, the whole chart, then the parts
        // in the order their findings come.
        private readonly Dictionary<FindingItem, int> _trackOrder = [];

        // Each Instrument chip's place among them, kept after its findings go, so a chip still on stays where it was.
        private readonly Dictionary<string, int> _instrumentOrder = new(StringComparer.Ordinal);

        private readonly TimeSpan _searchDelay;

        // Filters the list once typing in the search stops; made when it's first needed.
        private DispatcherTimer? _searchTimer;

        // The search the list is filtered by, trimmed.
        private string _query = "";

        // Clear filters is turning chips off, so the list is filtered once, when it's done.
        private bool _clearingFilters;

        public ChartIssuesViewModel()
            : this(SearchDelay)
        {
        }

        /// <param name="searchDelay">How long the search waits for typing to stop; zero to filter as it changes.</param>
        public ChartIssuesViewModel(TimeSpan searchDelay)
        {
            _searchDelay = searchDelay;
            ErrorChip = new CountedFilterChipViewModel(nameof(Severity.Error), SharedStrings.ChartIssuesErrorsSection, SharedStrings.ChartIssuesFilterChipToolTipFormat, OnFilterChanged);
            WarningChip = new CountedFilterChipViewModel(nameof(Severity.Warning), SharedStrings.ChartIssuesWarningsSection, SharedStrings.ChartIssuesFilterChipToolTipFormat, OnFilterChanged);
            InfoChip = new CountedFilterChipViewModel(nameof(Severity.Info), SharedStrings.ChartIssuesInfosSection, SharedStrings.ChartIssuesFilterChipToolTipFormat, OnFilterChanged);
            SeverityChips = [ErrorChip, WarningChip, InfoChip];
        }

        /// <summary>The rows: each section's <see cref="FindingGroup"/>, then the <see cref="FindingItem"/>s it shows.</summary>
        public AvaloniaList<object> Rows { get; } = [];

        /// <summary>The findings, in the order they were given, shown or not.</summary>
        public IReadOnlyList<FindingItem> Items => _items;

        /// <summary>The chart file the findings are of, as the headline names it, e.g. notes.mid.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Headline))]
        public partial string FileName { get; set; } = "";

        /// <summary>The window's title: the song the findings are of.</summary>
        [ObservableProperty]
        public partial string Title { get; set; } = SharedStrings.ChartIssuesWindowTitle;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary), nameof(Headline), nameof(HasErrors), nameof(ErrorCountText), nameof(IsClean))]
        public partial int ErrorCount { get; private set; }

        public bool HasErrors => ErrorCount > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary), nameof(Headline), nameof(HasWarnings), nameof(WarningCountText), nameof(IsClean))]
        public partial int WarningCount { get; private set; }

        public bool HasWarnings => WarningCount > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary), nameof(Headline), nameof(HasInfos), nameof(InfoCountText))]
        public partial int InfoCount { get; private set; }

        public bool HasInfos => InfoCount > 0;

        /// <summary>Neither errors nor warnings, so the footer shows a tick.</summary>
        public bool IsClean => ErrorCount + WarningCount == 0;

        /// <summary>The footer's count of warnings, as "1 Warning" or "5 Warnings".</summary>
        public string WarningCountText => WarningCount == 1
            ? SharedStrings.ChartIssuesWarningOne
            : string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesWarningsFormat, WarningCount);

        /// <summary>The footer's count of errors, as "1 Error" or "2 Errors".</summary>
        public string ErrorCountText => ErrorCount == 1
            ? SharedStrings.ChartIssuesErrorOne
            : string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesErrorsFormat, ErrorCount);

        /// <summary>The footer's count of info findings, as "3 Informational".</summary>
        public string InfoCountText => string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesInfosFormat, InfoCount);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowNoMatches), nameof(ShownSummaryText))]
        public partial bool HasFindings { get; private set; }

        /// <summary>The counts by severity: "0 errors · 2 warnings · 1 info".</summary>
        public string Summary => string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesSummaryFormat, ErrorCount, WarningCount, InfoCount);

        /// <summary>The findings in a line, as a song's details show them: "2 warnings and 1 error in notes.mid".</summary>
        public string Headline
        {
            get
            {
                string file = string.IsNullOrEmpty(FileName) ? SharedStrings.ChartIssuesTheChart : FileName;
                string headline = ErrorCount + WarningCount == 0
                    ? string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesNoneInFormat, file)
                    : string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesHeadlineFormat, WarningCount, ErrorCount, file);
                return InfoCount > 0 ? string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesHeadlineInfoFormat, headline, InfoCount) : headline;
            }
        }

        /// <summary>Shows only errors (with the other severity chips on, those too).</summary>
        public CountedFilterChipViewModel ErrorChip { get; }

        public CountedFilterChipViewModel WarningChip { get; }

        public CountedFilterChipViewModel InfoChip { get; }

        /// <summary>The Errors, Warnings and Informational chips, always there.</summary>
        public IReadOnlyList<CountedFilterChipViewModel> SeverityChips { get; }

        /// <summary>A chip for each instrument or track the findings are in, and any turned on since they stopped being.</summary>
        public AvaloniaList<CountedFilterChipViewModel> InstrumentChips { get; } = [];

        public bool HasInstrumentChips => InstrumentChips.Count > 0;

        /// <summary>A chip for each difficulty the findings are in, and any turned on since they stopped being.</summary>
        public AvaloniaList<CountedFilterChipViewModel> DifficultyChips { get; } = [];

        public bool HasDifficultyChips => DifficultyChips.Count > 0;

        /// <summary>A chip for each type of finding there is, by the catalogue's group, and any turned on since there stopped being.</summary>
        public AvaloniaList<CountedFilterChipViewModel> TypeChips { get; } = [];

        public bool HasTypeChips => TypeChips.Count > 0;

        /// <summary>How many chips are on, for the badge on the filter button.</summary>
        public int ActiveFilterCount => AllChips.Count(c => c.IsOn);

        public bool HasActiveFilters => ActiveFilterCount > 0;

        /// <summary>Narrows the list to the findings whose description, rule, track or place contains it.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ClearSearchCommand), nameof(ClearFiltersCommand))]
        public partial string? SearchText { get; set; }

        /// <summary>The list is filtered by a search: once typing stops, so this changes along with the list.</summary>
        public bool HasSearch => _query.Length > 0;

        /// <summary>The search or a chip leaves some findings out.</summary>
        public bool IsNarrowed => HasActiveFilters || HasSearch;

        /// <summary>How many findings the search and filter show.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowNoMatches), nameof(ShownSummaryText))]
        public partial int ShownCount { get; private set; }

        /// <summary>How many of the findings the search and filter show: "Showing 4 of 9 findings".</summary>
        public string ShownSummaryText => string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesShownSummaryFormat, ShownCount, _items.Count);

        /// <summary>There are findings, but the search and filter show none of them.</summary>
        public bool ShowNoMatches => HasFindings && ShownCount == 0;

        private IEnumerable<CountedFilterChipViewModel> AllChips => SeverityChips.Concat(InstrumentChips).Concat(DifficultyChips).Concat(TypeChips);

        /// <summary>
        /// Lists <paramref name="sources"/>' findings, in the order given within each track. A finding listed already
        /// keeps its row when <paramref name="reuse"/> allows, so it isn't made again; pass false when what a row shows
        /// of it may have changed, such as its place after a tempo change. The search and filter stay as they are.
        /// </summary>
        /// <param name="findingOf">The finding each source is.</param>
        /// <param name="make">A row for a source not listed already.</param>
        public void Show<T>(IReadOnlyList<T> sources, Func<T, Finding> findingOf, Func<T, FindingItem> make, bool reuse = true)
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentNullException.ThrowIfNull(findingOf);
            ArgumentNullException.ThrowIfNull(make);

            // The findings' rows listed now, by finding, to keep.
            var kept = new Dictionary<Finding, List<FindingItem>>();
            if (reuse)
            {
                foreach (var item in _items)
                {
                    if (!kept.TryGetValue(item.Finding, out var same))
                        kept[item.Finding] = same = [];
                    same.Add(item);
                }
            }

            _items.Clear();
            _trackOrder.Clear();
            var tracks = new Dictionary<(ChartInstrument, ChartDifficulty?), int>();
            int errors = 0, warnings = 0, infos = 0;
            foreach (var source in sources)
            {
                var finding = findingOf(source);
                FindingItem item;
                if (kept.TryGetValue(finding, out var same) && same.Count > 0)
                {
                    item = same[^1];
                    same.RemoveAt(same.Count - 1);
                }
                else
                {
                    item = make(source);
                }

                _items.Add(item);
                switch (finding.Severity)
                {
                    case Severity.Error:
                        errors++;
                        break;
                    case Severity.Warning:
                        warnings++;
                        break;
                    default:
                        infos++;
                        break;
                }

                // What the load left out of the file as a whole (CHR086) first, then the chart as a whole, then each
                // part and difficulty in the order their findings come, what the load left out of one among them.
                int order;
                if (finding.Location is { } location)
                {
                    var track = (location.Part, location.Difficulty);
                    if (!tracks.TryGetValue(track, out order))
                        tracks[track] = order = 2 + tracks.Count;
                }
                else
                {
                    order = finding.RuleId == ChrRules.Chr086 ? 0 : 1;
                }

                _trackOrder[item] = order;
            }

            ErrorCount = errors;
            WarningCount = warnings;
            InfoCount = infos;
            HasFindings = _items.Count > 0;
            RefreshFilterChips();
            RefreshRows();
        }

        /// <summary>Lists nothing, with the search cleared and every chip off, for another chart's findings.</summary>
        public void Clear()
        {
            TurnFiltersOff();
            _instrumentOrder.Clear();
            Show<Finding>([], static f => f, static f => new FindingItem(f, static _ => null));
            NotifyFilterState();
        }

        /// <summary>Lists the findings the search and filter show, in their sections, changing only the rows that changed.</summary>
        private void RefreshRows()
        {
            var headings = new Dictionary<Severity, FindingGroup>();
            foreach (var row in Rows)
            {
                if (row is FindingGroup heading)
                    headings[heading.Severity] = heading;
            }

            var next = new List<object>(_items.Count + Sections.Length);
            int shown = 0;
            foreach (var severity in Sections)
            {
                // Ordered by track; OrderBy is stable, so each track's findings keep the order they were given in.
                var members = _items
                    .Where(i => (i.IsError ? Severity.Error : i.IsWarning ? Severity.Warning : Severity.Info) == severity && IsShown(i))
                    .OrderBy(i => _trackOrder[i])
                    .ToList();
                if (members.Count == 0)
                    continue;

                if (!headings.TryGetValue(severity, out var heading))
                    heading = new FindingGroup(severity);
                heading.SetCount(members.Count);
                next.Add(heading);
                next.AddRange(members);
                shown += members.Count;
            }

            ListRows.Replace(Rows, next);
            ShownCount = shown;
            OnPropertyChanged(nameof(ShownSummaryText));
        }

        /// <summary>
        /// Whether the search and filter show <paramref name="item"/>: it contains the search, and it matches a chip on
        /// in each group that has one on.
        /// </summary>
        private bool IsShown(FindingItem item) =>
            MatchesSearch(item)
            && Passes(SeverityChips, SeverityId(item))
            && Passes(InstrumentChips, InstrumentId(item))
            && Passes(DifficultyChips, item.Difficulty?.ToString())
            && Passes(TypeChips, TypeId(item));

        /// <summary>Whether a group of chips lets <paramref name="id"/> through: it's on, or none of them is.</summary>
        private static bool Passes(IEnumerable<CountedFilterChipViewModel> chips, string? id)
        {
            bool anyOn = false;
            foreach (var chip in chips)
            {
                if (!chip.IsOn)
                    continue;
                if (chip.Id == id)
                    return true;
                anyOn = true;
            }

            return !anyOn;
        }

        /// <summary>Whether the finding's description, rule, track or place contains the search, ignoring case.</summary>
        private bool MatchesSearch(FindingItem item)
        {
            if (_query.Length == 0)
                return true;

            return Contains(item.Message) || Contains(item.RuleId) || Contains(item.TrackText) || Contains(item.PlaceText);

            bool Contains(string text) => text.Contains(_query, StringComparison.CurrentCultureIgnoreCase);
        }

        private static string SeverityId(FindingItem item) =>
            item.IsError ? nameof(Severity.Error) : item.IsWarning ? nameof(Severity.Warning) : nameof(Severity.Info);

        // The Instrument chip's ID is the name it shows: a part's, a track's, Whole chart or Chart file.
        private static string InstrumentId(FindingItem item) => item.InstrumentText;

        private static string TypeId(FindingItem item) => item.Category?.ToString() ?? OtherType;

        private const string OtherType = "Other";

        partial void OnSearchTextChanged(string? value)
        {
            _searchTimer?.Stop();

            // Clear filters shows every finding once, when it's done.
            if (_clearingFilters)
            {
                _query = value?.Trim() ?? "";
                return;
            }

            // A search cleared shows every finding at once; one typed waits for the typing to stop, so the list is
            // filtered once rather than on every key.
            if (string.IsNullOrWhiteSpace(value) || _searchDelay <= TimeSpan.Zero)
            {
                ApplySearch();
            }
            else
            {
                if (_searchTimer is null)
                {
                    _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = _searchDelay };
                    _searchTimer.Tick += (_, _) => ApplySearch();
                }

                _searchTimer.Start();
            }
        }

        /// <summary>Filters the list by the search as it is now, unless that's what it's filtered by already.</summary>
        private void ApplySearch()
        {
            _searchTimer?.Stop();
            string query = SearchText?.Trim() ?? "";
            if (query == _query)
                return;

            _query = query;
            RefreshRows();
            NotifyFilterState();
        }

        private bool HasSearchText() => !string.IsNullOrWhiteSpace(SearchText);

        // Only with something to clear, so Escape in an empty search is left to close the window.
        [RelayCommand(CanExecute = nameof(HasSearchText))]
        private void ClearSearch() => SearchText = null;

        /// <summary>A chip was turned on or off: the list shows the findings that now match.</summary>
        private void OnFilterChanged()
        {
            if (_clearingFilters)
                return;

            RefreshRows();
            NotifyFilterState();

            // A chip turned off once no finding matches it goes.
            RefreshFilterChips();
        }

        /// <summary>What says whether the search or a chip narrows the list has changed.</summary>
        private void NotifyFilterState()
        {
            OnPropertyChanged(nameof(ActiveFilterCount));
            OnPropertyChanged(nameof(HasActiveFilters));
            OnPropertyChanged(nameof(HasSearch));
            OnPropertyChanged(nameof(IsNarrowed));
            ClearFiltersCommand.NotifyCanExecuteChanged();
        }

        // A search still being typed can be cleared too.
        private bool CanClearFilters() => IsNarrowed || HasSearchText();

        /// <summary>Clears the search and turns every chip off, so every finding shows.</summary>
        [RelayCommand(CanExecute = nameof(CanClearFilters))]
        private void ClearFilters()
        {
            TurnFiltersOff();
            OnFilterChanged();
        }

        /// <summary>Clears the search, at once, and turns every chip off, without filtering the list again.</summary>
        private void TurnFiltersOff()
        {
            _clearingFilters = true;
            try
            {
                SearchText = null;
                foreach (var chip in AllChips.ToList())
                    chip.IsOn = false;
            }
            finally
            {
                _clearingFilters = false;
            }
        }

        /// <summary>
        /// Counts the findings each chip matches. An instrument, difficulty or type the findings have gets a chip, in the
        /// order the chart lists them; one they no longer have loses it, unless it's on, so a filter in use never goes
        /// out of sight.
        /// </summary>
        private void RefreshFilterChips()
        {
            ErrorChip.Count = ErrorCount;
            WarningChip.Count = WarningCount;
            InfoChip.Count = InfoCount;

            var instruments = new Dictionary<string, int>(StringComparer.Ordinal);
            var difficulties = new Dictionary<string, int>(StringComparer.Ordinal);
            var types = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in _items)
            {
                string instrument = InstrumentId(item);
                instruments[instrument] = instruments.GetValueOrDefault(instrument) + 1;
                _instrumentOrder.TryAdd(instrument, InstrumentOrder(item));
                if (item.Difficulty is { } difficulty)
                    difficulties[difficulty.ToString()] = difficulties.GetValueOrDefault(difficulty.ToString()) + 1;
                string type = TypeId(item);
                types[type] = types.GetValueOrDefault(type) + 1;
            }

            // An instrument chip on with none of its findings left keeps its place among the rest.
            SyncChips(InstrumentChips, instruments, static id => id, id => _instrumentOrder.GetValueOrDefault(id, int.MaxValue));
            SyncChips(DifficultyChips, difficulties, static id => ChartLabels.DifficultyName(Enum.Parse<ChartDifficulty>(id)), static id => (int)Enum.Parse<ChartDifficulty>(id));
            SyncChips(TypeChips, types, TypeName, static id => Enum.TryParse<ChrCategory>(id, out var category) ? (int)category : int.MaxValue);
            OnPropertyChanged(nameof(HasInstrumentChips));
            OnPropertyChanged(nameof(HasDifficultyChips));
            OnPropertyChanged(nameof(HasTypeChips));
        }

        // Chart file, the whole chart, the parts in the chart's order, the vocal parts, then any other track.
        private static int InstrumentOrder(FindingItem item) =>
            item.Finding.Location is { } location ? 2 + (int)location.Part
            : VocalTracks.Of(item.Finding.Track) is { } vocal ? 1000 + (int)vocal
            : item.InstrumentText == SharedStrings.ChartIssuesChartFile ? 0
            : item.InstrumentText == SharedStrings.ChartIssuesWholeChart ? 1
            : 2000;

        private static string TypeName(string id) => Enum.TryParse<ChrCategory>(id, out var category)
            ? category switch
            {
                ChrCategory.Timing => SharedStrings.ChartIssuesTypeTiming,
                ChrCategory.Frets => SharedStrings.ChartIssuesTypeFrets,
                ChrCategory.Drums => SharedStrings.ChartIssuesTypeDrums,
                ChrCategory.Phrases => SharedStrings.ChartIssuesTypePhrases,
                ChrCategory.Song => SharedStrings.ChartIssuesTypeSong,
                ChrCategory.Vocals => SharedStrings.ChartIssuesTypeVocals,
                _ => SharedStrings.ChartIssuesTypeLoad,
            }
            : SharedStrings.ChartIssuesTypeOther;

        /// <summary>
        /// Brings <paramref name="chips"/> in line with <paramref name="counts"/>: a chip per ID, sorted by
        /// <paramref name="orderOf"/> then by ID, and none for an ID no finding has unless it's on.
        /// </summary>
        private void SyncChips(AvaloniaList<CountedFilterChipViewModel> chips, Dictionary<string, int> counts, Func<string, string> nameOf, Func<string, int> orderOf)
        {
            var listed = new HashSet<string>(counts.Comparer);
            for (int i = chips.Count - 1; i >= 0; i--)
            {
                var chip = chips[i];
                chip.Count = counts.GetValueOrDefault(chip.Id);
                if (chip.Count == 0 && !chip.IsOn)
                    chips.RemoveAt(i);
                else
                    listed.Add(chip.Id);
            }

            foreach (var (id, count) in counts)
            {
                if (listed.Contains(id))
                    continue;

                int at = 0;
                while (at < chips.Count && Compare(chips[at].Id, id) < 0)
                    at++;
                chips.Insert(at, new CountedFilterChipViewModel(id, nameOf(id), SharedStrings.ChartIssuesFilterChipToolTipFormat, OnFilterChanged) { Count = count });
            }

            int Compare(string a, string b)
            {
                int byOrder = orderOf(a).CompareTo(orderOf(b));
                return byOrder != 0 ? byOrder : string.CompareOrdinal(a, b);
            }
        }
    }
}
