using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// What a text box scrubbed by <see cref="DeltaScrub"/> changes: whole steps up or down from where the drag began, so
    /// several things of different values can each be moved by the same steps rather than all set to one value.
    /// </summary>
    public interface IDeltaScrubTarget
    {
        /// <summary>How far the pointer is dragged, in pixels, for each step; read as a drag begins.</summary>
        double PixelsPerStep { get; }

        /// <summary>A drag begins; false when there's nothing to scrub, which leaves the drag doing nothing.</summary>
        bool Begin();

        /// <summary>
        /// The drag is <paramref name="steps"/> steps from where it began, right being up. Returns the steps it's taken as,
        /// short of them where going further would change nothing, so dragging back starts at once.
        /// </summary>
        int ScrubTo(int steps);

        /// <summary>The drag is over: what it shows is kept.</summary>
        void End();
    }

    /// <summary>
    /// Lets a <see cref="TextBox"/> be changed by dragging sideways across it, as <see cref="DragScrub"/> lets a number box:
    /// right to go up, left to go down, a step every <see cref="IDeltaScrubTarget.PixelsPerStep"/> pixels. Shift makes it ten
    /// times as fast, Ctrl a tenth as fast. Each step goes to the <see cref="TargetProperty">Target</see>, which moves
    /// whatever it stands for by it, so a field showing several values, or none, can still be scrubbed.
    /// </summary>
    /// <remarks>
    /// <para>A click without a drag edits the text as usual, selecting it. While it's typed in, the box is a text box like any
    /// other, so a press puts the caret and a drag selects; the pointer shows a sideways arrow over the box only while it can
    /// be dragged.</para>
    /// <para>On Windows the pointer is hidden while dragging and held where the drag began, so a drag can go on as far as it
    /// needs without the pointer reaching the edge of the screen (<see cref="FineDrag"/>).</para>
    /// </remarks>
    /// <example><c>&lt;TextBox ctl:DeltaScrub.Target="{Binding PitchScrub}" /&gt;</c></example>
    public sealed class DeltaScrub
    {
        public static readonly AttachedProperty<IDeltaScrubTarget?> TargetProperty =
            AvaloniaProperty.RegisterAttached<DeltaScrub, TextBox, IDeltaScrubTarget?>("Target");

        // How far the pointer moves, in pixels, before a press becomes a drag rather than a click.
        private const double DragThreshold = 4;

        // How many times as fast Shift makes a drag.
        private const int ShiftFactor = 10;

        private static readonly Cursor Sideways = new(StandardCursorType.SizeWestEast);
        private static readonly ConditionalWeakTable<TextBox, Drag> Drags = [];

        static DeltaScrub()
        {
            TargetProperty.Changed.AddClassHandler<TextBox>((box, e) =>
            {
                if (e.OldValue is null && e.NewValue is not null)
                    Attach(box);
                else if (e.OldValue is not null && e.NewValue is null)
                    Detach(box);
            });
        }

        private DeltaScrub()
        {
        }

        public static IDeltaScrubTarget? GetTarget(TextBox box) => box.GetValue(TargetProperty);

        public static void SetTarget(TextBox box, IDeltaScrubTarget? value) => box.SetValue(TargetProperty, value);

        private static void Attach(TextBox box)
        {
            // Tunnelled, so the press reaches this before the text box starts a selection with it.
            box.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            box.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
            box.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
            box.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            box.PropertyChanged += OnBoxPropertyChanged;
            box.TemplateApplied += OnTemplateApplied;
            box.PointerEntered += OnPointerEntered;
            UpdateHoverCursor(box);
        }

        private static void Detach(TextBox box)
        {
            End(box);
            box.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            box.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            box.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            box.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            box.PropertyChanged -= OnBoxPropertyChanged;
            box.TemplateApplied -= OnTemplateApplied;
            box.PointerEntered -= OnPointerEntered;
            SetCursor(box, null);
        }

        // The text box's parts are only all there once it's laid out, so the cursor is put on them again as the pointer arrives.
        private static void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is TextBox box)
                UpdateHoverCursor(box);
        }

        private static void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            if (sender is TextBox box)
                UpdateHoverCursor(box);
        }

        private static void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (sender is TextBox box && (e.Property == InputElement.IsKeyboardFocusWithinProperty || e.Property == InputElement.IsEffectivelyEnabledProperty))
                UpdateHoverCursor(box);
        }

        // Whether a press on the box would start a drag: it's not being typed in, and can be changed.
        private static bool CanScrub(TextBox box) => box.IsEffectivelyEnabled && !box.IsReadOnly && !box.IsKeyboardFocusWithin;

        /// <summary>A sideways arrow over the box says it can be dragged; the text cursor comes back while it's typed in.</summary>
        private static void UpdateHoverCursor(TextBox box)
        {
            if (!Drags.TryGetValue(box, out var drag) || !drag.IsDragging)
                SetCursor(box, CanScrub(box) ? Sideways : null);
        }

        /// <summary>
        /// Shows <paramref name="cursor"/> over the whole box, or with <see langword="null"/> what it shows by itself. Parts of
        /// its template set a text cursor of their own, which would win over one inherited from the box, so each gets it too.
        /// </summary>
        private static void SetCursor(TextBox box, Cursor? cursor)
        {
            foreach (var part in box.GetVisualDescendants().OfType<InputElement>().Prepend(box))
            {
                if (cursor is null)
                    part.ClearValue(InputElement.CursorProperty);
                else
                    part.Cursor = cursor;
            }
        }

        private static void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not TextBox box || GetTarget(box) is not { } target || !CanScrub(box)
                || !e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // Taken so the text box doesn't start a selection with it: a click's edit is given back when the pointer is let
            // go without moving.
            Drags.AddOrUpdate(box, new Drag(e.Pointer, e.GetPosition(box), target, new FineDrag(box)));
            e.Pointer.Capture(box);
            e.Handled = true;
        }

        private static void OnMoved(object? sender, PointerEventArgs e)
        {
            if (sender is not TextBox box || !Drags.TryGetValue(box, out var drag) || drag.Pointer != e.Pointer)
                return;

            e.Handled = true;
            var at = e.GetPosition(box);
            if (!drag.IsDragging)
            {
                if (drag.IsRefused || Math.Abs(at.X - drag.Start.X) < DragThreshold)
                    return;

                if (!drag.Target.Begin())
                {
                    drag.IsRefused = true;
                    return;
                }

                drag.IsDragging = true;
                drag.PixelsPerStep = Math.Max(drag.Target.PixelsPerStep, double.Epsilon);
                if (e.Pointer.Captured != box)
                    e.Pointer.Capture(box);

                // From where it was pressed, so the first step counts the way past the threshold too.
                drag.Fine.Start(drag.Start);
                SetCursor(box, HeldPointer.CanHold ? HeldPointer.Hidden : Sideways);
            }

            double dx = drag.Fine.Move(at).X;
            if (dx == 0)
                return;

            double factor = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? ShiftFactor : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1 : 1;
            drag.Moved += dx * factor;

            int wanted = ScrubMath.Steps(drag.Moved, drag.PixelsPerStep);
            if (wanted == drag.Steps)
                return;

            int steps = drag.Target.ScrubTo(wanted);
            drag.Steps = steps;

            // Held at a limit, the way back starts at once rather than after undoing the overshoot.
            if (steps != wanted)
                drag.Moved = steps * drag.PixelsPerStep;
        }

        private static void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is not TextBox box || !Drags.TryGetValue(box, out var drag) || drag.Pointer != e.Pointer)
                return;

            e.Handled = true;
            bool clicked = !drag.IsDragging && !drag.IsRefused;
            End(box);
            e.Pointer.Capture(null);

            // A click edits the text, as it would without dragging.
            if (clicked)
            {
                box.Focus(NavigationMethod.Pointer);
                box.SelectAll();
            }
        }

        private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (sender is TextBox box)
                End(box);
        }

        // Ends a press on the box, keeping what a drag of it shows.
        private static void End(TextBox box)
        {
            if (!Drags.TryGetValue(box, out var drag))
                return;

            Drags.Remove(box);
            drag.Fine.Stop();
            if (drag.IsDragging)
                drag.Target.End();
            UpdateHoverCursor(box);
        }

        /// <summary>A press on the box, and the drag it becomes.</summary>
        /// <param name="Start">Where it was pressed, in the box, where the pointer is held while dragging.</param>
        /// <param name="Target">What the drag changes, as it was when it was pressed.</param>
        /// <param name="Fine">How far the pointer moves as it's dragged, holding it while it can be.</param>
        private sealed record Drag(IPointer Pointer, Point Start, IDeltaScrubTarget Target, FineDrag Fine)
        {
            public bool IsDragging { get; set; }

            /// <summary>Whether the target had nothing to scrub, so the press does nothing more.</summary>
            public bool IsRefused { get; set; }

            public double PixelsPerStep { get; set; } = 1;

            /// <summary>How far the pointer has been dragged, in pixels, with Shift and Ctrl taken into account.</summary>
            public double Moved { get; set; }

            /// <summary>The steps the target is at.</summary>
            public int Steps { get; set; }
        }
    }
}
