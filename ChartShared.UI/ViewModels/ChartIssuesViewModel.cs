using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Collections;
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
    /// A chart finding, as the issues list shows it: its message, rule and place, with Go to where whoever lists it can
    /// take you there, and a right-click menu of its fixes where they can fix it.
    /// </summary>
    public sealed class FindingItem
    {
        private readonly Func<Finding, string?> _whereOf;
        private readonly Action<Finding>? _goTo;
        private readonly Func<Finding, IReadOnlyList<FindingFix>>? _fixesOf;
        private string? _where;
        private bool _hasWhere;
        private IRelayCommand? _goToCommand;

        /// <param name="whereOf">Where the finding is, in words; worked out only once the row is shown.</param>
        /// <param name="fixesOf">
        /// The ways to fix a finding, worked out each time its menu opens, so they're of the chart as it is then; none when
        /// it has none. Null where the list can't fix anything, so its rows have no menu.
        /// </param>
        public FindingItem(Finding finding, Func<Finding, string?> whereOf, Action<Finding>? goTo = null, Func<Finding, IReadOnlyList<FindingFix>>? fixesOf = null)
        {
            ArgumentNullException.ThrowIfNull(finding);
            ArgumentNullException.ThrowIfNull(whereOf);
            Finding = finding;
            _whereOf = whereOf;
            _goTo = goTo;
            _fixesOf = fixesOf;
        }

        public Finding Finding { get; }

        public string RuleId => Finding.RuleId;

        public string Message => Finding.Message;

        /// <summary>Where it is: the part and difficulty, and the measure and beat; empty when it's nowhere in particular.</summary>
        public string Where
        {
            get
            {
                if (!_hasWhere)
                {
                    _where = _whereOf(Finding) ?? "";
                    _hasWhere = true;
                }

                return _where!;
            }
        }

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

    /// <summary>What a group of the issues list is of: reading the chart file, the chart as a whole, or one part's track.</summary>
    public enum FindingGroupKind
    {
        Parsing,
        General,
        Part,
    }

    /// <summary>Which group a finding is listed in.</summary>
    public readonly record struct FindingGroupKey(FindingGroupKind Kind, ChartInstrument? Part = null, ChartDifficulty? Difficulty = null)
    {
        /// <summary>
        /// The group of <paramref name="finding"/>: what the load left out of the file (CHR086) under parsing, one with a
        /// place in a part under that part and difficulty, and the rest under the chart as a whole.
        /// </summary>
        public static FindingGroupKey Of(Finding finding)
        {
            ArgumentNullException.ThrowIfNull(finding);
            if (finding.RuleId == ChrRules.Chr086)
                return new FindingGroupKey(FindingGroupKind.Parsing);
            return finding.Location is { } location
                ? new FindingGroupKey(FindingGroupKind.Part, location.Part, location.Difficulty)
                : new FindingGroupKey(FindingGroupKind.General);
        }

        /// <summary>The group's heading: "Reading the chart file", "Whole chart", or the part and difficulty.</summary>
        public string Title => Kind switch
        {
            FindingGroupKind.Parsing => SharedStrings.ChartIssuesParsingGroup,
            FindingGroupKind.General => SharedStrings.ChartIssuesGeneralGroup,
            _ => Difficulty is { } difficulty
                ? string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesTrackGroupFormat, ChartLabels.InstrumentName(Part!.Value), ChartLabels.DifficultyName(difficulty))
                : ChartLabels.InstrumentName(Part!.Value),
        };
    }

    /// <summary>A group's heading row in the issues list, with how many findings of each severity it holds.</summary>
    public sealed partial class FindingGroup(FindingGroupKey key) : ViewModelBase
    {
        public FindingGroupKey Key { get; } = key;

        public string Title { get; } = key.Title;

        /// <summary>Its counts by severity, as "2 errors · 5 warnings · 1 info", leaving out those there are none of.</summary>
        [ObservableProperty]
        public partial string Counts { get; private set; } = "";

        internal void SetCounts(int errors, int warnings, int infos)
        {
            var parts = new List<string>(3);
            if (errors > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesErrorsFormat, errors));
            if (warnings > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesWarningsFormat, warnings));
            if (infos > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesInfosFormat, infos));
            Counts = string.Join(SharedStrings.ChartIssuesCountsSeparator, parts);
        }
    }

    /// <summary>
    /// A chart's findings, as a list grouped by where they are (GitHub issue #53): reading the chart file first, then the
    /// chart as a whole, then each part and difficulty. The list is flat, headings among the findings, so a virtualizing
    /// list shows it; it's changed in place as the findings change, so only the rows that changed are made again.
    /// </summary>
    /// <remarks>For the UI thread only.</remarks>
    public sealed partial class ChartIssuesViewModel : ViewModelBase
    {
        private readonly List<FindingItem> _items = [];

        /// <summary>The rows: each group's <see cref="FindingGroup"/>, then its <see cref="FindingItem"/>s.</summary>
        public AvaloniaList<object> Rows { get; } = [];

        /// <summary>The findings, in the order they were given.</summary>
        public IReadOnlyList<FindingItem> Items => _items;

        /// <summary>The chart file the findings are of, as the headline names it, e.g. notes.mid.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Headline))]
        public partial string FileName { get; set; } = "";

        /// <summary>The window's title: the song the findings are of.</summary>
        [ObservableProperty]
        public partial string Title { get; set; } = SharedStrings.ChartIssuesWindowTitle;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary), nameof(Headline), nameof(HasErrors))]
        public partial int ErrorCount { get; private set; }

        public bool HasErrors => ErrorCount > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary), nameof(Headline))]
        public partial int WarningCount { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary), nameof(Headline))]
        public partial int InfoCount { get; private set; }

        [ObservableProperty]
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

        /// <summary>
        /// Lists <paramref name="sources"/>' findings, in the order given within each group. A finding listed already
        /// keeps its row when <paramref name="reuse"/> allows, so it isn't made again; pass false when what a row shows
        /// of it may have changed, such as its place after a tempo change.
        /// </summary>
        /// <param name="findingOf">The finding each source is.</param>
        /// <param name="make">A row for a source not listed already.</param>
        public void Show<T>(IReadOnlyList<T> sources, Func<T, Finding> findingOf, Func<T, FindingItem> make, bool reuse = true)
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentNullException.ThrowIfNull(findingOf);
            ArgumentNullException.ThrowIfNull(make);

            // What's listed now, to keep: the findings' rows, by finding, and the groups' headings.
            var kept = new Dictionary<Finding, List<FindingItem>>();
            var headings = new Dictionary<FindingGroupKey, FindingGroup>();
            foreach (var row in Rows)
            {
                if (row is FindingGroup heading)
                {
                    headings[heading.Key] = heading;
                }
                else if (reuse && row is FindingItem item)
                {
                    if (!kept.TryGetValue(item.Finding, out var same))
                        kept[item.Finding] = same = [];
                    same.Add(item);
                }
            }

            // The groups, parsing and the chart as a whole first, then the parts in the order their findings come.
            var groups = new Dictionary<FindingGroupKey, List<FindingItem>>();
            var order = new List<FindingGroupKey> { new(FindingGroupKind.Parsing), new(FindingGroupKind.General) };
            _items.Clear();
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

                var key = FindingGroupKey.Of(finding);
                if (!groups.TryGetValue(key, out var members))
                {
                    groups[key] = members = [];
                    if (key.Kind == FindingGroupKind.Part)
                        order.Add(key);
                }

                members.Add(item);
            }

            var next = new List<object>(_items.Count + order.Count);
            foreach (var key in order)
            {
                if (!groups.TryGetValue(key, out var members))
                    continue;
                if (!headings.TryGetValue(key, out var heading))
                    heading = new FindingGroup(key);
                int groupErrors = 0, groupWarnings = 0;
                foreach (var member in members)
                {
                    groupErrors += member.IsError ? 1 : 0;
                    groupWarnings += member.IsWarning ? 1 : 0;
                }

                heading.SetCounts(groupErrors, groupWarnings, members.Count - groupErrors - groupWarnings);
                next.Add(heading);
                next.AddRange(members);
            }

            ListRows.Replace(Rows, next);
            ErrorCount = errors;
            WarningCount = warnings;
            InfoCount = infos;
            HasFindings = _items.Count > 0;
        }

        /// <summary>Lists nothing.</summary>
        public void Clear() => Show<Finding>([], static f => f, static f => new FindingItem(f, static _ => null));
    }
}
