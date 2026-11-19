using System;
using Avalonia.Threading;
using ChartShared.Core.Charting.Editing;

namespace ChartBuilder.ViewModels
{
    // The Alignment tool's button and options in the toolbar (charter-spec C3 item 7): the options, the offset, the speed
    // and whether every vocal part or every difficulty is aligned, show while the tool is picked.
    public sealed partial class ChartEditorViewModel
    {
        // How long after the offset or speed is last typed or scrubbed it's put on the undo stack, so a scrub is one step.
        private static readonly TimeSpan AlignFieldDelay = TimeSpan.FromMilliseconds(600);

        private DispatcherTimer? _alignApply;

        // Whether an offset or speed typed or scrubbed is shown but not yet on the undo stack.
        private bool _alignPending;

        /// <summary>Whether the Alignment tool is picked. It can only be while a lane has focus.</summary>
        public bool IsAlignTool
        {
            get => Session.Tool == EditorTool.Align;
            set => SetTool(EditorTool.Align, value);
        }

        /// <summary>Whether the Alignment tool aligns the vocal parts, a vocal lane having focus, rather than an instrument.</summary>
        public bool AlignsVocals => Session.FocusedVocal is not null;

        /// <summary>
        /// How far the first note is moved, in milliseconds, later when positive. Setting it shows the notes moved at once,
        /// and puts the change on the undo stack once it's left alone for a moment.
        /// </summary>
        public decimal? AlignOffsetMs
        {
            get => (decimal)Math.Round(Session.Alignment.Offset * 1000, 1);
            set
            {
                if (value is not { } ms || ms == AlignOffsetMs)
                    return;
                PreviewAlign((double)ms / 1000, Session.Alignment.Speed);
            }
        }

        /// <summary>
        /// How fast the notes are played, as a percentage: above 100 they finish sooner. Setting it shows the notes played
        /// at it at once, and puts the change on the undo stack once it's left alone for a moment.
        /// </summary>
        public decimal? AlignSpeedPercent
        {
            get => (decimal)Math.Round(Session.Alignment.Speed * 100, 3);
            set
            {
                if (value is not { } percent || percent == AlignSpeedPercent)
                    return;
                PreviewAlign(Session.Alignment.Offset, (double)percent / 100);
            }
        }

        /// <summary>The slowest speed the field takes, as a percentage.</summary>
        public static decimal AlignMinSpeedPercent => (decimal)(Alignment.MinSpeed * 100);

        /// <summary>The fastest speed the field takes, as a percentage.</summary>
        public static decimal AlignMaxSpeedPercent => (decimal)(Alignment.MaxSpeed * 100);

        /// <summary>Whether every vocal part is aligned together, rather than only the one whose lane has focus.</summary>
        public bool IsAlignAllVocalParts
        {
            get => Session.Alignment.AllVocalParts;
            set
            {
                if (value == Session.Alignment.AllVocalParts)
                    return;
                CommitAlign();
                Session.Alignment.AllVocalParts = value;
            }
        }

        /// <summary>Whether every difficulty of the instrument is aligned together, rather than only the one whose lane has focus.</summary>
        public bool IsAlignAllDifficulties
        {
            get => Session.Alignment.AllDifficulties;
            set
            {
                if (value == Session.Alignment.AllDifficulties)
                    return;
                CommitAlign();
                Session.Alignment.AllDifficulties = value;
            }
        }

        // Shows the offset and speed typed or scrubbed, and starts again the wait before they're put on the undo stack.
        private void PreviewAlign(double offset, double speed)
        {
            _alignPending = true;
            Session.Alignment.Preview(offset, speed, by: this);
            _alignApply ??= new DispatcherTimer(AlignFieldDelay, DispatcherPriority.Background, (_, _) => CommitAlign());
            _alignApply.Stop();
            _alignApply.Start();
        }

        // Puts the offset and speed typed or scrubbed on the undo stack as one step. Once it's been kept some other way, as
        // a press in a lane keeps it, a drag's preview shown since is the drag's to keep, not this.
        private void CommitAlign()
        {
            _alignApply?.Stop();
            if (!_alignPending)
                return;
            _alignPending = false;
            var alignment = Session.Alignment;
            if (!alignment.IsPreviewing || ReferenceEquals(alignment.PreviewedBy, this))
                alignment.Commit();
        }

        private void OnAlignmentChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(AlignOffsetMs));
            OnPropertyChanged(nameof(AlignSpeedPercent));
            OnPropertyChanged(nameof(IsAlignAllVocalParts));
            OnPropertyChanged(nameof(IsAlignAllDifficulties));
        }

        // The tool's button and options, after the tool or the focus changes. A change still waiting is kept as the tool is put down.
        private void RefreshAlign()
        {
            if (!IsAlignTool)
                CommitAlign();
            OnPropertyChanged(nameof(IsAlignTool));
            OnPropertyChanged(nameof(AlignsVocals));
        }
    }
}
