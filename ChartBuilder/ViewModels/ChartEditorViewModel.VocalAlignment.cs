using System;
using Avalonia.Threading;
using ChartShared.Core.Charting.Editing;

namespace ChartBuilder.ViewModels
{
    // The Vocal Alignment tool's button and options in the toolbar (charter-spec C3 item 7): the button shows while a vocal
    // lane has focus, and the options, the offset, the speed and whether every part is aligned, while the tool is picked.
    public sealed partial class ChartEditorViewModel
    {
        // How long after the offset or speed is last typed or scrubbed it's put on the undo stack, so a scrub is one step.
        private static readonly TimeSpan VocalAlignFieldDelay = TimeSpan.FromMilliseconds(600);

        private DispatcherTimer? _vocalAlignApply;

        // Whether an offset or speed typed or scrubbed is shown but not yet on the undo stack.
        private bool _vocalAlignPending;

        /// <summary>Whether the Vocal Alignment tool is picked. It can only be while a vocal lane has focus.</summary>
        public bool IsVocalAlignTool
        {
            get => Session.Tool == EditorTool.VocalAlign;
            set => SetTool(EditorTool.VocalAlign, value);
        }

        /// <summary>Whether the Vocal Alignment tool's button shows: a vocal lane has focus.</summary>
        public bool ShowsVocalAlignTool => Session.FocusedVocal is not null || IsVocalAlignTool;

        /// <summary>
        /// How far the vocal parts' first note is moved, in milliseconds, later when positive. Setting it shows the parts
        /// moved at once, and puts the change on the undo stack once it's left alone for a moment.
        /// </summary>
        public decimal? VocalAlignOffsetMs
        {
            get => (decimal)Math.Round(Session.VocalAlignment.Offset * 1000, 1);
            set
            {
                if (value is not { } ms || ms == VocalAlignOffsetMs)
                    return;
                PreviewVocalAlign((double)ms / 1000, Session.VocalAlignment.Speed);
            }
        }

        /// <summary>
        /// How fast the vocal parts are played, as a percentage: above 100 they finish sooner. Setting it shows the parts
        /// played at it at once, and puts the change on the undo stack once it's left alone for a moment.
        /// </summary>
        public decimal? VocalAlignSpeedPercent
        {
            get => (decimal)Math.Round(Session.VocalAlignment.Speed * 100, 3);
            set
            {
                if (value is not { } percent || percent == VocalAlignSpeedPercent)
                    return;
                PreviewVocalAlign(Session.VocalAlignment.Offset, (double)percent / 100);
            }
        }

        /// <summary>The slowest speed the field takes, as a percentage.</summary>
        public static decimal VocalAlignMinSpeedPercent => (decimal)(VocalAlignment.MinSpeed * 100);

        /// <summary>The fastest speed the field takes, as a percentage.</summary>
        public static decimal VocalAlignMaxSpeedPercent => (decimal)(VocalAlignment.MaxSpeed * 100);

        /// <summary>Whether every vocal part is aligned together, rather than only the one whose lane has focus.</summary>
        public bool IsVocalAlignAllParts
        {
            get => Session.VocalAlignment.AllParts;
            set
            {
                if (value == Session.VocalAlignment.AllParts)
                    return;
                CommitVocalAlign();
                Session.VocalAlignment.AllParts = value;
            }
        }

        // Shows the offset and speed typed or scrubbed, and starts again the wait before they're put on the undo stack.
        private void PreviewVocalAlign(double offset, double speed)
        {
            _vocalAlignPending = true;
            Session.VocalAlignment.Preview(offset, speed);
            _vocalAlignApply ??= new DispatcherTimer(VocalAlignFieldDelay, DispatcherPriority.Background, (_, _) => CommitVocalAlign());
            _vocalAlignApply.Stop();
            _vocalAlignApply.Start();
        }

        // Puts the offset and speed typed or scrubbed on the undo stack as one step.
        private void CommitVocalAlign()
        {
            _vocalAlignApply?.Stop();
            if (!_vocalAlignPending)
                return;
            _vocalAlignPending = false;
            Session.VocalAlignment.Commit();
        }

        private void OnVocalAlignmentChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(VocalAlignOffsetMs));
            OnPropertyChanged(nameof(VocalAlignSpeedPercent));
            OnPropertyChanged(nameof(IsVocalAlignAllParts));
        }

        // The tool's button and options, after the tool or the focus changes. A change still waiting is kept as the tool is put down.
        private void RefreshVocalAlign()
        {
            if (!IsVocalAlignTool)
                CommitVocalAlign();
            OnPropertyChanged(nameof(IsVocalAlignTool));
            OnPropertyChanged(nameof(ShowsVocalAlignTool));
        }
    }
}
