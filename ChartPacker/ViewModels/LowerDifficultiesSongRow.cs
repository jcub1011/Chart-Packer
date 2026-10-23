using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>Where a song stands in the Generate lower difficulties window, which says which tab lists it.</summary>
    public enum LowerDifficultiesState
    {
        /// <summary>Its chart isn't read yet.</summary>
        Reading,

        /// <summary>It has a difficulty to generate, as the window's settings are.</summary>
        ToGenerate,

        /// <summary>It has nothing to generate, as the window's settings are.</summary>
        Skipped,

        /// <summary>It can't be read, generated or saved.</summary>
        Problem,

        /// <summary>Its new difficulties are in its draft.</summary>
        Done,
    }

    /// <summary>What happens to a difficulty of a part, as a pip of the grid shows it.</summary>
    public enum PipState
    {
        /// <summary>No notes, and none to be generated.</summary>
        Empty,

        /// <summary>Notes, kept.</summary>
        Charted,

        /// <summary>No notes; to be generated.</summary>
        Target,

        /// <summary>Notes, to be made again in their place.</summary>
        Replace,

        /// <summary>Generated, in the song's draft.</summary>
        Generated,
    }

    /// <summary>A difficulty of a part on a song's row, coloured as the games colour it.</summary>
    public sealed record DifficultyPip(ChartDifficulty Difficulty, PipState State)
    {
        public bool IsExpert => Difficulty == ChartDifficulty.Expert;

        public bool IsHard => Difficulty == ChartDifficulty.Hard;

        public bool IsMedium => Difficulty == ChartDifficulty.Medium;

        public bool IsEasy => Difficulty == ChartDifficulty.Easy;

        public bool IsCharted => State == PipState.Charted;

        public bool IsTarget => State == PipState.Target;

        public bool IsReplace => State == PipState.Replace;

        public bool IsGenerated => State == PipState.Generated;

        public bool IsEmpty => State == PipState.Empty;
    }

    /// <summary>A part on a song's row: its instrument's icon, and its difficulties, Expert first.</summary>
    /// <param name="ToolTip">The part's name, then what happens to each of its difficulties, a line each.</param>
    public sealed record PartPips(ChartInstrument Part, DifficultyGlyph Glyph, string ToolTip, IReadOnlyList<DifficultyPip> Pips)
    {
        /// <summary>The icon a part is shown with: its instrument's, as the inspector draws it.</summary>
        public static DifficultyGlyph GlyphOf(ChartInstrument part) => part switch
        {
            ChartInstrument.Bass or ChartInstrument.GhlBass => DifficultyGlyph.Bass,
            ChartInstrument.Rhythm or ChartInstrument.GhlRhythm => DifficultyGlyph.Rhythm,
            ChartInstrument.Drums => DifficultyGlyph.Drums,
            ChartInstrument.Keys => DifficultyGlyph.Keys,
            _ => DifficultyGlyph.Guitar,
        };

        /// <summary>The pips of <paramref name="plan"/>: each difficulty charted, to be generated or made again, or neither.</summary>
        /// <param name="generated">Whether the plan's targets are generated already.</param>
        public static PartPips Of(PartPlan plan, bool generated)
        {
            ArgumentNullException.ThrowIfNull(plan);
            var pips = Enum.GetValues<ChartDifficulty>().OrderDescending().Select(d => new DifficultyPip(d, StateOf(plan, d, generated))).ToList();
            var lines = pips.Select(p => string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesPipFormat, ChartLabels.DifficultyName(p.Difficulty), NameOf(p.State)));
            return new PartPips(plan.Part, GlyphOf(plan.Part), string.Join(Environment.NewLine, lines.Prepend(ChartLabels.InstrumentName(plan.Part))), pips);
        }

        private static PipState StateOf(PartPlan plan, ChartDifficulty difficulty, bool generated) =>
            plan.Targets.Contains(difficulty)
                ? generated ? PipState.Generated : plan.Charted.Contains(difficulty) ? PipState.Replace : PipState.Target
                : plan.Charted.Contains(difficulty) ? PipState.Charted : PipState.Empty;

        private static string NameOf(PipState state) => state switch
        {
            PipState.Charted => Strings.LowerDifficultiesPipCharted,
            PipState.Target => Strings.LowerDifficultiesPipTarget,
            PipState.Replace => Strings.LowerDifficultiesPipReplace,
            PipState.Generated => Strings.LowerDifficultiesPipGenerated,
            _ => Strings.LowerDifficultiesPipEmpty,
        };
    }

    /// <summary>
    /// A song in the Generate lower difficulties window: its name, its parts' difficulties as the window's settings would
    /// fill them (<see cref="SongDifficultySurvey.Plan"/>), and where it stands.
    /// </summary>
    public sealed partial class LowerDifficultiesSongRow : ObservableObject
    {
        private IReadOnlyList<PartPlan> _plan = [];
        private string? _problem;

        /// <param name="draft">A copy of the song's draft when the window opened, which its chart is read through; null for none.</param>
        public LowerDifficultiesSongRow(SongEntry entry, MetadataDraft? draft)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Entry = entry;
            Draft = draft;
            Title = entry.DisplayName;
            StatusText = Strings.LowerDifficultiesReading;
        }

        public SongEntry Entry { get; }

        public string Path => Entry.FullPath;

        /// <summary>A copy of the song's draft when the window opened; null for none.</summary>
        public MetadataDraft? Draft { get; }

        /// <summary>The song's metadata as read; null until it's read.</summary>
        public SongMetadataReport? Report { get; private set; }

        /// <summary>The song's <c>song.ini</c> as drafted; empty until it's read.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Pairs { get; private set; } = [];

        /// <summary>The song as the preview and generating read it, without targets; null until it's read.</summary>
        public GenerateDifficultiesSong? Song { get; private set; }

        /// <summary>Which of its parts' difficulties have notes; null until it's read.</summary>
        public SongDifficultySurvey? Survey { get; private set; }

        /// <summary>Each part as it's filled, or as it was once Done.</summary>
        public IReadOnlyList<PartPlan> Plan => _plan;

        [ObservableProperty]
        public partial string Title { get; private set; }

        [ObservableProperty]
        public partial string Artist { get; private set; } = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsProblem), nameof(IsDone), nameof(CanInclude), nameof(IsLeftOut))]
        public partial LowerDifficultiesState State { get; private set; } = LowerDifficultiesState.Reading;

        /// <summary>
        /// Whether Generate generates the song, when it has something to generate; ticked to start with. It's kept as the
        /// window's settings change, so a song left out stays left out.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLeftOut))]
        public partial bool IsIncluded { get; set; } = true;

        /// <summary>Whether the song has something to generate, so it can be ticked or left out.</summary>
        public bool CanInclude => State == LowerDifficultiesState.ToGenerate;

        /// <summary>Whether the song has something to generate but is left out, so its row is drawn faint.</summary>
        public bool IsLeftOut => CanInclude && !IsIncluded;

        /// <summary>Whether Generate generates the song.</summary>
        public bool WillGenerate => CanInclude && IsIncluded;

        public bool IsProblem => State == LowerDifficultiesState.Problem;

        public bool IsDone => State == LowerDifficultiesState.Done;

        /// <summary>Whether its new difficulties have been saved.</summary>
        [ObservableProperty]
        public partial bool IsSaved { get; private set; }

        /// <summary>What it'd have generated, why it's skipped or can't be, or that it's done.</summary>
        [ObservableProperty]
        public partial string StatusText { get; private set; }

        /// <summary>Each part's difficulties, as they'd be generated.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<PartPips> Parts { get; private set; } = [];

        /// <summary>Why the song can't be read, generated or saved; null when it can.</summary>
        public string? Problem => _problem;

        /// <summary>How many of its difficulties it'd generate, counting every part.</summary>
        public int TargetCount => _plan.Sum(static p => p.Targets.Count);

        /// <summary>Whether its name or artist has <paramref name="query"/> in it; an empty query matches every song.</summary>
        public bool Matches(string query) =>
            query.Length == 0
            || Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Artist.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        /// <summary>The song as read: its metadata, and its parts' difficulties, planned by <paramref name="scope"/>.</summary>
        public void Read(SongMetadataReport report, IReadOnlyList<KeyValuePair<string, string>> pairs, SongDifficultySurvey survey, LowerDifficultyScope scope)
        {
            ArgumentNullException.ThrowIfNull(report);
            ArgumentNullException.ThrowIfNull(pairs);
            ArgumentNullException.ThrowIfNull(survey);
            Report = report;
            Pairs = pairs;
            Survey = survey;
            var headline = SongHeadline.From(pairs);
            Title = headline.Name ?? Entry.DisplayName;
            Artist = headline.Artist ?? "";
            Song = new GenerateDifficultiesSong(Entry, Title, pairs, Draft, new Dictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>>());
            _problem = survey.Status switch
            {
                SongSurveyStatus.ChartEdits => Strings.LowerDifficultiesChartEdits,
                SongSurveyStatus.NoChart => Strings.LowerDifficultiesNoChart,
                SongSurveyStatus.ChartTooLarge => Strings.LowerDifficultiesChartTooLarge,
                SongSurveyStatus.Unreadable => (survey.Error ?? report.ReadError) is { } why
                    ? string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesUnreadableFormat, why)
                    : Strings.LowerDifficultiesUnreadable,
                _ when !report.CanEdit => Strings.LowerDifficultiesCantSave,
                _ => null,
            };
            Replan(scope);
        }

        /// <summary>Plans the song again by <paramref name="scope"/>, unless it's done, can't be, or isn't read yet.</summary>
        public void Replan(LowerDifficultyScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            if (State == LowerDifficultiesState.Done || Survey is not { } survey)
                return;
            if (_problem is { } problem)
            {
                _plan = [];
                Parts = [];
                StatusText = problem;
                State = LowerDifficultiesState.Problem;
                return;
            }

            _plan = survey.Plan(scope);
            Parts = [.. _plan.Select(static p => PartPips.Of(p, generated: false))];
            int replaced = _plan.Sum(static p => p.Targets.Count(p.Replaces));
            int added = TargetCount - replaced;
            if (added + replaced > 0)
            {
                StatusText = (added, replaced) switch
                {
                    (_, 0) => string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesNewFormat, added),
                    (0, _) => string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesReplacedFormat, replaced),
                    _ => string.Format(CultureInfo.CurrentCulture, Strings.LowerDifficultiesNewReplacedFormat, added, replaced),
                };
                State = LowerDifficultiesState.ToGenerate;
                return;
            }

            StatusText = survey.Parts.Count == 0 ? Strings.LowerDifficultiesSkippedNoParts
                : _plan.Count == 0 ? Strings.LowerDifficultiesSkippedFiltered
                : Strings.LowerDifficultiesSkippedCharted;
            State = LowerDifficultiesState.Skipped;
        }

        /// <summary>The song as the preview and generating read it, filling what <paramref name="scope"/> says.</summary>
        public GenerateDifficultiesSong? SongFor(LowerDifficultyScope scope) =>
            Song is { } song
                ? song with { Targets = _plan.Where(static p => p.Targets.Count > 0).ToDictionary(static p => p.Part, static p => p.Targets), Scope = scope }
                : null;

        /// <summary>Its new difficulties are in its draft.</summary>
        public void Generated()
        {
            Parts = [.. _plan.Select(static p => PartPips.Of(p, generated: true))];
            StatusText = Strings.LowerDifficultiesGenerated;
            State = LowerDifficultiesState.Done;
        }

        /// <summary>Its new difficulties are saved.</summary>
        public void Saved()
        {
            IsSaved = true;
            StatusText = Strings.LowerDifficultiesSaved;
        }

        /// <summary>It couldn't be generated, for <paramref name="problem"/>.</summary>
        public void Failed(string problem)
        {
            _problem = problem;
            _plan = [];
            Parts = [];
            StatusText = problem;
            State = LowerDifficultiesState.Problem;
        }
    }
}
