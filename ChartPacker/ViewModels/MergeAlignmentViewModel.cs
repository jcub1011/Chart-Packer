using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A link a song can be lined up by, as its cell offers it; or none, for an offset set by hand.</summary>
    public sealed record MergeAlignmentOptionViewModel(AlignmentChoice? Choice, string Label);

    /// <summary>One song's cell of the alignment row: how far it moves, and what it's lined up by.</summary>
    public sealed partial class MergeAlignmentCellViewModel(int source, bool hasChart, Action<MergeAlignmentCellViewModel, AlignmentChoice?> chosen, Action<MergeAlignmentCellViewModel, int> typed) : ObservableObject
    {
        // Set while the window shows what it worked out, so that isn't taken for the user's choice.
        private bool _updating;

        public int Source { get; } = source;

        /// <summary>The song has a chart to line up by.</summary>
        public bool HasChart { get; } = hasChart;

        /// <summary>The kept audio's song, which the others line up with and which doesn't move.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanMove))]
        [NotifyPropertyChangedFor(nameof(CanLink))]
        public partial bool IsReference { get; set; }

        public bool CanMove => !IsReference;

        /// <summary>There's more than setting it by hand to choose from.</summary>
        public bool CanLink => !IsReference && Options.Count > 1;

        public AvaloniaList<MergeAlignmentOptionViewModel> Options { get; } = [];

        [ObservableProperty]
        public partial MergeAlignmentOptionViewModel? SelectedOption { get; set; }

        /// <summary>How far the song moves, in milliseconds, later when positive; the offset box.</summary>
        [ObservableProperty]
        public partial decimal? Offset { get; set; } = 0;

        /// <summary>How far its link moves it, from which song; or why it isn't lined up.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNote))]
        public partial string? Note { get; set; }

        public bool HasNote => Note is not null;

        /// <summary>What to check about its link before taking it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWarning))]
        public partial string? Warning { get; set; }

        public bool HasWarning => Warning is not null;

        /// <summary>Shows what the window worked out: the links it can be lined up by, the one it is, and how far it moves.</summary>
        internal void Show(IReadOnlyList<MergeAlignmentOptionViewModel> options, AlignmentChoice? choice, int offset, string? note, string? warning)
        {
            _updating = true;
            try
            {
                if (!Options.Select(o => o.Choice).SequenceEqual(options.Select(o => o.Choice)))
                {
                    Options.Clear();
                    Options.AddRange(options);
                    OnPropertyChanged(nameof(CanLink));
                }

                SelectedOption = Options.FirstOrDefault(o => Equals(o.Choice, choice)) ?? Options.FirstOrDefault();
                Offset = offset;
                Note = note;
                Warning = warning;
            }
            finally
            {
                _updating = false;
            }
        }

        partial void OnSelectedOptionChanged(MergeAlignmentOptionViewModel? value)
        {
            if (!_updating && value is not null)
                chosen(this, value.Choice);
        }

        partial void OnOffsetChanged(decimal? value)
        {
            if (!_updating && value is { } offset)
                typed(this, (int)Math.Clamp(Math.Round(offset), int.MinValue, int.MaxValue));
        }
    }

    public sealed partial class MergeWindowViewModel
    {
        // The songs read for lining up, by source, and every link between them: worked out once, when first asked for.
        private IReadOnlyList<AlignmentLink>? _links;

        // What each song is lined up by, and how far it's moved by hand on top, against the audio of _alignedTo.
        private readonly Dictionary<int, AlignmentChoice> _choices = [];
        private readonly Dictionary<int, int> _nudges = [];
        private int? _alignedTo;

        // What AlignmentProblemOf last found, for the plan of that signature.
        private (string Signature, string? Problem)? _alignmentProblem;

        /// <summary>One cell per source, lined up with the headers.</summary>
        public AvaloniaList<MergeAlignmentCellViewModel> Alignment { get; } = [];

        /// <summary>The links between the songs are being worked out.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AutoAlignCommand))]
        public partial bool IsAligning { get; set; }

        /// <summary>Why auto-aligning didn't line every song up, or failed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAlignmentMessage))]
        public partial string? AlignmentMessage { get; set; }

        public bool HasAlignmentMessage => AlignmentMessage is not null;

        private void BuildAlignment(SongMergeAnalysis analysis)
        {
            for (int i = 0; i < analysis.Sources.Count; i++)
                Alignment.Add(new MergeAlignmentCellViewModel(i, analysis.Sources[i].Chart is not null, OnAlignmentChosen, OnAlignmentTyped));
        }

        /// <summary>
        /// Lines the songs up against <paramref name="audio"/>'s, now the audio kept: a song lined up with other audio isn't
        /// lined up with this, so each goes back to where its own chart has it.
        /// </summary>
        private void AlignTo(int audio)
        {
            if (_alignedTo == audio)
                return;

            _alignedTo = audio;
            _choices.Clear();
            _nudges.Clear();
            AlignmentMessage = null;
            ShowAlignment();
        }

        /// <summary>How far each song moves, by source, for the plan; <see langword="null"/> when none does.</summary>
        private Dictionary<int, int>? SourceOffsets()
        {
            if (_alignedTo is not { } reference)
                return null;

            var offsets = AlignmentOffsets(reference);
            var moved = Enumerable.Range(0, offsets.Length).Where(i => offsets[i] != 0).ToDictionary(i => i, i => offsets[i]);
            return moved.Count > 0 ? moved : null;
        }

        private int[] AlignmentOffsets(int reference) => SourceAligner.OffsetsOf(Alignment.Count, reference, _choices, _nudges);

        private bool CanAutoAlign() => !IsAligning;

        /// <summary>Lines every song up it can, by the links between them, replacing what was chosen or typed.</summary>
        [RelayCommand(CanExecute = nameof(CanAutoAlign))]
        private async Task AutoAlignAsync()
        {
            if (_analysis is not { } analysis || _alignedTo is not { } reference)
                return;

            AlignmentMessage = null;
            IsAligning = true;
            try
            {
                var token = _cts?.Token ?? default;
                _links ??= await Task.Run(() => SourceAligner.Links([.. analysis.Sources.Select(s => ChartOf(s.Chart))], token), token);
                if (_alignedTo != reference || _closed)
                    return;

                _choices.Clear();
                _nudges.Clear();
                foreach (var (source, choice) in SourceAligner.Solve(reference, _links))
                    _choices[source] = choice;

                if (analysis.Sources[reference].Chart is null)
                    AlignmentMessage = Strings.MergeAlignmentReferenceNoChart;
                else if (Enumerable.Range(0, Alignment.Count).Any(i => i != reference && Alignment[i].HasChart && !_choices.ContainsKey(i)))
                    AlignmentMessage = Strings.MergeAlignmentNoLinks;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChartFormatException ex)
            {
                AlignmentMessage = string.Format(CultureInfo.CurrentCulture, Strings.MergeAlignmentFailedFormat, ex.Message);
            }
            finally
            {
                IsAligning = false;
            }

            ShowAlignment();
            Refresh();
        }

        /// <summary>Moves every song back to where its own chart has it.</summary>
        [RelayCommand]
        private void ResetAlignment()
        {
            _choices.Clear();
            _nudges.Clear();
            AlignmentMessage = null;
            ShowAlignment();
            Refresh();
        }

        // A chart that can't be read for lining up just isn't lined up by.
        private static AlignmentChart? ChartOf(MidiChart? chart)
        {
            if (chart is null)
                return null;
            try
            {
                return AlignmentChart.Of(chart);
            }
            catch (ChartFormatException)
            {
                return null;
            }
        }

        /// <summary>The user chose a link for a song, or to set it by hand.</summary>
        private void OnAlignmentChosen(MergeAlignmentCellViewModel cell, AlignmentChoice? choice)
        {
            if (_alignedTo is not { } reference)
                return;

            // Set by hand, it stays where the link had it; linked anew, it goes where the link puts it.
            int at = AlignmentOffsets(reference)[cell.Source];
            if (choice is null)
            {
                _choices.Remove(cell.Source);
                _nudges[cell.Source] = at;
            }
            else
            {
                _choices[cell.Source] = choice;
                _nudges.Remove(cell.Source);
            }

            ShowAlignment();
            Refresh();
        }

        /// <summary>The user typed how far a song moves: what's more or less than its link puts it is its nudge.</summary>
        private void OnAlignmentTyped(MergeAlignmentCellViewModel cell, int offset)
        {
            if (_alignedTo is not { } reference || cell.Source == reference)
                return;

            int linked = AlignmentOffsets(reference)[cell.Source] - _nudges.GetValueOrDefault(cell.Source);
            int nudge = (int)Math.Clamp((long)offset - linked, int.MinValue, int.MaxValue);
            if (nudge == 0)
                _nudges.Remove(cell.Source);
            else
                _nudges[cell.Source] = nudge;

            ShowAlignment();
            Refresh();
        }

        /// <summary>Shows each song's offset and link as chosen, and the links each could be lined up by.</summary>
        private void ShowAlignment()
        {
            if (_alignedTo is not { } reference)
                return;

            // A choice that no longer reaches the reference, as one lined up through a song now set by hand, is dropped.
            foreach (int source in _choices.Keys.Where(s => !SourceAligner.IsLinked(s, reference, _choices, new HashSet<int>())).ToList())
                _choices.Remove(source);

            var offsets = AlignmentOffsets(reference);
            foreach (var cell in Alignment)
            {
                cell.IsReference = cell.Source == reference;
                if (cell.IsReference)
                {
                    cell.Show([], null, 0, null, null);
                    continue;
                }

                var options = new List<MergeAlignmentOptionViewModel> { new(null, Strings.MergeAlignmentByHand) };
                foreach (var link in (_links ?? []).Where(l => l.To == cell.Source && SourceAligner.IsLinked(l.From, reference, _choices, new HashSet<int> { cell.Source })))
                {
                    if (link.Pattern.Matched > 0)
                        options.Add(Option(new AlignmentChoice(link, AlignmentMethod.NotePattern)));
                    options.Add(Option(new AlignmentChoice(link, AlignmentMethod.FirstNote)));
                }

                var choice = _choices.GetValueOrDefault(cell.Source);
                cell.Show(options, choice, offsets[cell.Source], NoteOf(cell, choice, options.Count), WarningOf(choice));
            }
        }

        private MergeAlignmentOptionViewModel Option(AlignmentChoice choice)
        {
            var estimate = choice.Link.Estimate(choice.Method);
            string how = choice.Method == AlignmentMethod.NotePattern
                ? string.Format(CultureInfo.CurrentCulture, Strings.MergeAlignmentPatternFormat, estimate.Match)
                : Strings.MergeAlignmentFirstNotes;
            return new MergeAlignmentOptionViewModel(choice, string.Format(CultureInfo.CurrentCulture, Strings.MergeAlignmentOptionFormat, Sources[choice.Link.From].Title, PartNameOf(choice.Link.PartId), how));
        }

        /// <param name="optionCount">How many options the cell offers, setting it by hand included.</param>
        private string? NoteOf(MergeAlignmentCellViewModel cell, AlignmentChoice? choice, int optionCount)
        {
            if (choice is not null)
                return string.Format(CultureInfo.CurrentCulture, Strings.MergeAlignmentLinkFormat, choice.OffsetMs, Sources[choice.Link.From].Title);
            if (!cell.HasChart)
                return Strings.MergeAlignmentNoChart;
            return _links is not null && optionCount <= 1 ? Strings.MergeAlignmentUnreached : null;
        }

        private static string? WarningOf(AlignmentChoice? choice)
        {
            if (choice is null)
                return null;

            var estimate = choice.Link.Estimate(choice.Method);
            var warnings = new List<string>();
            if (choice.Method == AlignmentMethod.FirstNote)
                warnings.Add(Strings.MergeAlignmentFirstNotesHint);
            if (estimate.IsAmbiguous)
                warnings.Add(Strings.MergeAlignmentAmbiguous);
            else if (estimate.IsNarrowed)
                warnings.Add(Strings.MergeAlignmentNarrowed);
            if (estimate.HasDrift)
                warnings.Add(string.Format(CultureInfo.CurrentCulture, Strings.MergeAlignmentDriftFormat, estimate.DriftMs));
            return warnings.Count > 0 ? string.Join(" ", warnings) : null;
        }

        private static string PartNameOf(string partId) =>
            MidiPartCatalog.Known.FirstOrDefault(p => p.Id == partId) is { } part ? ChartLabels.PartName(part) : partId;

        /// <summary>
        /// Why the plan's alignment can't be merged: the parts it moves before the song starts. <see langword="null"/> when it can.
        /// Kept for the plan last asked about, as dragging an offset asks again at every step and the charts would be read each time.
        /// </summary>
        private string? AlignmentProblemOf(SongMergePlan plan)
        {
            if (plan.SourceOffsets is null)
                return null;

            string signature = Signature(plan);
            if (_alignmentProblem is { } known && known.Signature == signature)
                return known.Problem;

            string? problem;
            try
            {
                var early = SongMerger.PartsBeforeStart(plan);
                problem = early.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.MergeAlignmentBeforeStartFormat, string.Join(", ", early.Select(PartNameOf)));
            }
            catch (Exception ex) when (ex is ChartFormatException or ArgumentException)
            {
                problem = ex.Message;
            }

            _alignmentProblem = (signature, problem);
            return problem;
        }
    }
}
