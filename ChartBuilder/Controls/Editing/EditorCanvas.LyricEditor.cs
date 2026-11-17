using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;

namespace ChartBuilder.Controls.Editing
{
    // Editing a vocal note's lyric in its lane (charter-spec C3 item 7): the box it's typed in, over the note, and the
    // bubbles a press finds a note by. The edits themselves are the tools' (EditorTools.SetVocalLyric).
    public abstract partial class EditorCanvas
    {
        /// <summary>A lyric bubble as last drawn: where, the note it belongs to, and whether it holds a lyric or is the faint + of a note with none.</summary>
        private readonly record struct LyricBubbleSpot(Rect Bubble, VocalNotePlace Place, bool HasLyric);

        // The bubbles as last drawn, in the order drawn, so the last is the one on top.
        private readonly List<LyricBubbleSpot> _bubbles = [];

        // The note a double click on it opens the lyric box for, once its press is released.
        private long? _lyricOnRelease;

        // The box a note's lyric is edited in, over the note it belongs to, while it's open; and the note.
        private Popup? _lyricPopup;
        private TextBox? _lyricBox;
        private bool _lyricOpen;
        private long _lyricTick;

        // The # or ^ that make the note spoken, which the box doesn't show, so they can't be changed by mistake; put back as it's set.
        private string _lyricMarks = "";

        /// <summary>The most room the lyric box may take along the lane, which it's kept in view by: a little over 32 characters.</summary>
        private const double LyricBoxWidth = 320;

        /// <summary>How many characters wide the lyric box may grow before what's typed wraps onto another line.</summary>
        private const int LyricBoxWrapChars = 32;

        // The box's ground: the app's dark surface, so what's typed is plain over the lane.
        private static readonly Avalonia.Media.IBrush LyricBoxBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0x1B, 0x1D, 0x2A));

        /// <summary>The bubble under <paramref name="position"/>, the topmost of those that cover it, or null.</summary>
        private LyricBubbleSpot? BubbleAt(Point position)
        {
            for (int i = _bubbles.Count - 1; i >= 0; i--)
            {
                if (_bubbles[i].Bubble.Contains(position))
                    return _bubbles[i];
            }

            return null;
        }

        /// <summary>
        /// A press on a lyric bubble selects its note, and with a double click, or on the faint + of a note with no lyric, opens
        /// the box to edit its lyric in. Whether the press was on one.
        /// </summary>
        private bool TakeLyricBubble(EditorSession session, Point position, int clicks)
        {
            if (_lane is not { Vocal: { } track } || BubbleAt(position) is not { } spot)
                return false;

            Focus();
            if (!session.Selection.Contains(track, spot.Place))
                session.Selection.SelectVocalNotes(track, [spot.Place]);
            if (clicks >= 2 || !spot.HasLyric)
                OpenLyricEditor(spot.Place.Tick);
            _overlay.Redraw();
            return true;
        }

        /// <summary>
        /// Opens the lyric box over the first selected vocal note; whether it opened. With none selected, that's said in the
        /// status.
        /// </summary>
        internal bool OpenLyricEditor()
        {
            if (_lane is not { Vocal: not null })
                return false;
            if (Session is { } session && _lane is { Vocal: { } track } && session.Selection.VocalNotes is { Count: > 0 } notes
                && session.VocalContentOf(track) is { } content)
            {
                // The first selected note that has a lyric to edit: a slide has none.
                foreach (var note in notes)
                {
                    if (!VocalEdits.IsSlide(content, note.Tick))
                    {
                        OpenLyricEditor(note.Tick);
                        return true;
                    }
                }

                session.Status = Strings.VocalSlideNoLyric;
                return false;
            }

            if (Session is { } none)
                none.Status = Strings.VocalEditLyricNone;
            return false;
        }

        /// <summary>
        /// Opens the lyric box over the vocal note starting at <paramref name="tick"/> (charter-spec C3 item 7), with its
        /// lyric in it, and selects the note. Enter, or leaving the box, sets what it says, blank taking the lyric away; Esc
        /// leaves the note as it was; Tab goes on to the next note's lyric, Shift+Tab back to the one before. Already open,
        /// it keeps what it says and goes to this note.
        /// </summary>
        internal void OpenLyricEditor(long tick)
        {
            if (Session is not { } session || _lane is not { Vocal: { } track } lane || session.VocalContentOf(track) is not { } content
                || session.VocalsOf(track) is not { } vocals
                || ProjectVocals(Bounds.Size, VocalSlotsOf(lane), session.Viewport, lane.Length) is not { } screen)
            {
                return;
            }

            int index = -1;
            for (int i = 0; i < vocals.Notes.Count; i++)
            {
                if (vocals.Notes[i].Tick == tick)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return;

            // A slide's + isn't a lyric: editing it would make the note sung.
            if (vocals.Notes[index].IsSlide)
            {
                session.Status = Strings.VocalSlideNoLyric;
                return;
            }

            // What the box says so far is kept as it moves on.
            ApplyLyricEditor();

            var note = vocals.Notes[index];
            var place = new VocalNotePlace(note.Tick, note.Pitch);
            if (!session.Selection.Contains(track, place))
                session.Selection.SelectVocalNotes(track, [place]);

            // The box is under the note, so the bubble over it, which shows what's typed as it's typed, isn't covered.
            var bar = VocalNoteRect(session, vocals, screen, note, VocalNoteThickness(screen));
            EnsureLyricEditor();
            _lyricTick = tick;
            (string shown, _lyricMarks) = VocalEdits.SplitSpokenMarks(VocalEdits.LyricAt(content, tick) ?? "");
            _lyricBox!.Text = shown;
            _lyricPopup!.PlacementRect = new Rect(Math.Clamp(bar.Left, 0, Math.Max(0, Bounds.Width - LyricBoxWidth)), bar.Bottom + 4, 1, 1);
            bool wasOpen = _lyricOpen;
            _lyricOpen = true;
            _lyricPopup.IsOpen = true;
            FitLyricBox();
            _overlay.Redraw();
            if (!wasOpen)
                Dispatcher.UIThread.Post(FocusLyricBox, DispatcherPriority.Input);
            else
                FocusLyricBox();
        }

        private void FocusLyricBox()
        {
            if (!_lyricOpen || _lyricBox is not { } box)
                return;
            box.Focus();
            box.SelectAll();
        }

        /// <summary>Makes the lyric box and its popup, once: the box keeps its place between notes.</summary>
        private void EnsureLyricEditor()
        {
            if (_lyricPopup is not null)
                return;

            _lyricBox = new TextBox
            {
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                PlaceholderText = Strings.LyricEditWatermark,
                Background = LyricBoxBrush,
                BorderBrush = AccentBrush ?? DefaultAccentBrush,
                BorderThickness = new Thickness(2),
            };
            _lyricBox.TextChanged += OnLyricBoxTextChanged;
            _lyricBox.KeyDown += OnLyricBoxKeyDown;
            _lyricBox.LostFocus += OnLyricBoxLostFocus;
            _lyricPopup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.AnchorAndGravity,
                PlacementAnchor = PopupAnchor.TopLeft,
                PlacementGravity = PopupGravity.BottomRight,
                IsLightDismissEnabled = true,
                Child = _lyricBox,
            };
            _lyricPopup.Closed += OnLyricPopupClosed;

            // In the canvas's trees, so the box has the theme's styles; out of them it's drawn bare, and can't be seen.
            LogicalChildren.Add(_lyricPopup);
            VisualChildren.Add(_lyricPopup);
        }

        // What's typed shows in the lyric's bubble as it's typed, and the box grows to hold it.
        private void OnLyricBoxTextChanged(object? sender, TextChangedEventArgs e)
        {
            FitLyricBox();
            if (_lyricOpen)
                _overlay.Redraw();
        }

        /// <summary>
        /// Sizes the lyric box to what's in it: as wide as the text, but never narrower than its placeholder, which has to fit,
        /// nor wider than <see cref="LyricBoxWrapChars"/> characters, past which the text wraps onto another line.
        /// </summary>
        private void FitLyricBox()
        {
            if (_lyricBox is not { } box)
                return;

            // The text's room either side of it, and a little for the caret.
            double room = box.Padding.Left + box.Padding.Right + box.BorderThickness.Left + box.BorderThickness.Right + 10;
            var face = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
            double WidthOf(string text) => text.Length == 0
                ? 0
                : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, box.FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;

            double least = WidthOf(box.PlaceholderText ?? "") + room;
            double most = Math.Max(least, WidthOf(new string('n', LyricBoxWrapChars)) + room);
            box.Width = Math.Clamp(WidthOf(box.Text ?? "") + room, least, most);
        }

        private void OnLyricBoxKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    CloseLyricEditor(apply: true);
                    break;
                case Key.Escape:
                    e.Handled = true;
                    CloseLyricEditor(apply: false);
                    break;
                case Key.Tab:
                    e.Handled = true;
                    StepLyricEditor((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
                    break;

                // Space is the box's, so typing a syllable doesn't play or pause the song.
                case Key.Space:
                    e.Handled = true;
                    break;
            }
        }

        private void OnLyricBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            // Leaving the box, as clicking elsewhere does, keeps what it says.
            if (_lyricOpen && _lyricBox is { IsKeyboardFocusWithin: false })
                CloseLyricEditor(apply: true);
        }

        private void OnLyricPopupClosed(object? sender, EventArgs e)
        {
            // Closed by a click elsewhere or the window losing it: keep what the box says.
            if (_lyricOpen)
                CloseLyricEditor(apply: true);
        }

        /// <summary>Sets the open box's note's lyric to what it says, as one step, or none when it's as it was.</summary>
        private void ApplyLyricEditor()
        {
            if (_lyricOpen && Session is { } session && _lane is { Vocal: { } track } && _lyricBox is { } box
                && session.Tools.SetVocalLyric(track, _lyricTick, VocalEdits.WithSpokenMarks(box.Text ?? "", _lyricMarks)))
            {
                _overlay.Redraw();
            }
        }

        /// <summary>
        /// Takes what the box says for its note and opens it on the next note of the part after it, or the one before with
        /// <paramref name="direction"/> negative, the song moved there when it isn't in view; the box closes after the last.
        /// </summary>
        private void StepLyricEditor(int direction)
        {
            if (!_lyricOpen || Session is not { } session || _lane is not { Vocal: { } track } lane || session.VocalsOf(track) is not { } vocals)
                return;

            long? target = null;
            var notes = vocals.Notes;
            if (direction > 0)
            {
                for (int i = 0; i < notes.Count; i++)
                {
                    if (notes[i].Tick > _lyricTick && !notes[i].IsSlide)
                    {
                        target = notes[i].Tick;
                        break;
                    }
                }
            }
            else
            {
                for (int i = notes.Count - 1; i >= 0; i--)
                {
                    if (notes[i].Tick < _lyricTick && !notes[i].IsSlide)
                    {
                        target = notes[i].Tick;
                        break;
                    }
                }
            }

            ApplyLyricEditor();
            if (target is not { } tick)
            {
                CloseLyricEditor(apply: false);
                return;
            }

            if (ProjectVocals(Bounds.Size, VocalSlotsOf(lane), session.Viewport, lane.Length) is { } screen)
            {
                double along = screen.AlongOf(session.AudioAt(tick));
                if (along < screen.TimeFrom || along > screen.TimeFrom + screen.TimeLength - LyricBoxWidth)
                    session.MoveCursorTo(tick);
            }

            OpenLyricEditor(tick);
        }

        /// <summary>Closes the lyric box, setting its note's lyric to what it says when <paramref name="apply"/>.</summary>
        private void CloseLyricEditor(bool apply)
        {
            if (!_lyricOpen)
                return;
            if (apply)
                ApplyLyricEditor();
            _lyricOpen = false;
            if (_lyricPopup is { } popup)
                popup.IsOpen = false;
            _overlay.Redraw();

            // The keys the lane's own, the box gone.
            if (TopLevel.GetTopLevel(this) is not null)
                Focus();
        }

        /// <summary>The note menu's Edit Lyric… was picked, for the note at <paramref name="tick"/>.</summary>
        private void OnLyricEditRequested(long tick) => OpenLyricEditor(tick);
    }
}
