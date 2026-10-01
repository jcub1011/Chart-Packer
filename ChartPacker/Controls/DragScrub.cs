using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Lets a <see cref="NumericUpDown"/> be changed by dragging sideways across it: right to raise the
    /// value, left to lower it, by <see cref="StepProperty">Step</see> per pixel. Shift makes each pixel ten
    /// steps, Ctrl a tenth of one. The pointer shows a sideways arrow over the box to say so.
    /// </summary>
    /// <remarks>
    /// <para>A click without a drag edits the text as usual, and while the box is being typed in it doesn't drag
    /// at all. Enter keeps what was typed and stops editing: focus moves to the nearest focusable
    /// ancestor, so a box in a flyout can be left without closing the flyout.</para>
    /// <para>While it's typed in, Up and Down and the mouse wheel step the value by its increment, as the box does by
    /// itself; with Shift they step ten times as far. Ctrl does nothing to them: they step in whole increments. Over a
    /// box that isn't being typed in, the wheel scrolls what the box is in, so scrolling past it changes nothing.</para>
    /// <para>On Windows the pointer is hidden while dragging and held where the drag began, so a drag can go on
    /// as far as it needs without the pointer reaching the edge of the screen; it's left where it started.
    /// Elsewhere the pointer moves as usual and shows as a sideways arrow.</para>
    /// </remarks>
    /// <example><c>&lt;NumericUpDown ctl:DragScrub.IsEnabled="True" /&gt;</c></example>
    public sealed class DragScrub
    {
        public static readonly AttachedProperty<bool> IsEnabledProperty =
            AvaloniaProperty.RegisterAttached<DragScrub, NumericUpDown, bool>("IsEnabled");

        public static readonly AttachedProperty<double> StepProperty =
            AvaloniaProperty.RegisterAttached<DragScrub, NumericUpDown, double>("Step", 1);

        // How far the pointer moves, in pixels, before a press becomes a drag rather than a click.
        private const double DragThreshold = 4;

        // How many increments Shift makes a key press or a wheel notch, and a dragged pixel how many steps.
        private const int ShiftFactor = 10;

        private static readonly Cursor Hidden = new(StandardCursorType.None);
        private static readonly Cursor Sideways = new(StandardCursorType.SizeWestEast);
        private static readonly ConditionalWeakTable<NumericUpDown, Drag> Drags = [];

        // Each box's text box, from its template: it's the content of the spinner, so it isn't in the visual tree until the box is laid out.
        private static readonly ConditionalWeakTable<NumericUpDown, TextBox> TextBoxes = [];

        static DragScrub()
        {
            IsEnabledProperty.Changed.AddClassHandler<NumericUpDown>((box, e) =>
            {
                if (e.NewValue is true)
                    Attach(box);
                else
                    Detach(box);
            });
        }

        private DragScrub()
        {
        }

        public static bool GetIsEnabled(NumericUpDown box) => box.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(NumericUpDown box, bool value) => box.SetValue(IsEnabledProperty, value);

        public static double GetStep(NumericUpDown box) => box.GetValue(StepProperty);

        public static void SetStep(NumericUpDown box, double value) => box.SetValue(StepProperty, value);

        private static void Attach(NumericUpDown box)
        {
            // Tunnelled, so the press reaches this before the text box starts a selection with it.
            box.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            box.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
            box.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
            box.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

            // Tunnelled, so a Shift step is taken before the box takes the key or the notch as a single one.
            box.AddHandler(InputElement.KeyDownEvent, OnStepKeyDown, RoutingStrategies.Tunnel);
            box.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            box.PropertyChanged += OnBoxPropertyChanged;
            box.TemplateApplied += OnTemplateApplied;
            box.PointerEntered += OnPointerEntered;
            UpdateHoverCursor(box);
        }

        private static void Detach(NumericUpDown box)
        {
            box.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            box.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            box.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            box.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            box.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            box.RemoveHandler(InputElement.KeyDownEvent, OnStepKeyDown);
            box.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            box.PropertyChanged -= OnBoxPropertyChanged;
            box.TemplateApplied -= OnTemplateApplied;
            box.PointerEntered -= OnPointerEntered;
            SetCursor(box, null);
        }

        // The text box's parts are only all there once it's laid out, so the cursor is put on them again as the pointer arrives.
        private static void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is NumericUpDown box)
                UpdateHoverCursor(box);
        }

        private static void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            if (sender is not NumericUpDown box)
                return;

            if (e.NameScope.Find<TextBox>("PART_TextBox") is { } text)
                TextBoxes.AddOrUpdate(box, text);
            UpdateHoverCursor(box);
        }

        /// <summary>Enter stops editing, keeping what was typed, without closing a flyout the box is in.</summary>
        private static void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not NumericUpDown box || e.Key is not (Key.Enter or Key.Return) || !box.IsKeyboardFocusWithin)
                return;

            for (var ancestor = box.GetVisualParent(); ancestor is not null; ancestor = ancestor.GetVisualParent())
            {
                if (ancestor is InputElement { Focusable: true, IsEffectivelyEnabled: true } target && target.Focus(NavigationMethod.Pointer))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        /// <summary>Shift+Up and Shift+Down step ten increments; without Shift the box steps one by itself.</summary>
        private static void OnStepKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not NumericUpDown box || e.Key is not (Key.Up or Key.Down) || !e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !CanStep(box))
                return;

            StepBy(box, (e.Key == Key.Up ? 1 : -1) * ShiftFactor * box.Increment);
            e.Handled = true;
        }

        /// <summary>
        /// With Shift a wheel notch steps ten increments; without, the box steps one by itself. Some platforms turn a
        /// wheel turned with Shift held into a sideways scroll, so that counts too.
        /// </summary>
        private static void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (sender is not NumericUpDown box || !e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !CanStep(box))
                return;

            double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (delta == 0)
                return;

            StepBy(box, Math.Sign(delta) * ShiftFactor * box.Increment);
            e.Handled = true;
        }

        // Keys and the wheel step only a box being typed in, as the box steps by itself.
        private static bool CanStep(NumericUpDown box) => box.IsEffectivelyEnabled && box.IsKeyboardFocusWithin && !box.IsReadOnly;

        /// <summary>Adds <paramref name="amount"/> to the value, from what's typed if that's a number, within the box's limits.</summary>
        private static void StepBy(NumericUpDown box, decimal amount)
        {
            decimal from = TextBoxOf(box)?.Text is { } text
                && decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal typed)
                ? typed
                : box.Value ?? 0;
            decimal value = Math.Clamp(from + amount, box.Minimum, box.Maximum);
            if (box.Value != value)
                box.Value = value;
        }

        private static void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (sender is NumericUpDown box && e.Property == InputElement.IsKeyboardFocusWithinProperty)
                UpdateHoverCursor(box);
        }

        /// <summary>A sideways arrow over the box says it can be dragged; the text cursor comes back while it's typed in.</summary>
        private static void UpdateHoverCursor(NumericUpDown box)
        {
            if (!Drags.TryGetValue(box, out var drag) || !drag.IsDragging)
                SetCursor(box, box.IsKeyboardFocusWithin ? null : Sideways);
        }

        /// <summary>
        /// Shows <paramref name="cursor"/> over the whole box, or with <see langword="null"/> what it shows by
        /// itself. The text box and parts of its template set a text cursor of their own, which would win over
        /// one inherited from the box, so each of them gets it too.
        /// </summary>
        private static void SetCursor(NumericUpDown box, Cursor? cursor)
        {
            var parts = new List<InputElement> { box };
            if (TextBoxOf(box) is { } text)
            {
                parts.Add(text);
                parts.AddRange(text.GetVisualDescendants().OfType<InputElement>());
            }

            foreach (var part in parts)
            {
                if (cursor is null)
                    part.ClearValue(InputElement.CursorProperty);
                else
                    part.Cursor = cursor;
            }
        }

        private static void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not NumericUpDown box || !box.IsEffectivelyEnabled || box.IsKeyboardFocusWithin
                || !e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
                return;

            var start = e.GetPosition(box);
            Drags.AddOrUpdate(box, new Drag(e.Pointer, start, box.PointToScreen(start), box.Value ?? 0));
            e.Pointer.Capture(box);
            e.Handled = true;
        }

        private static void OnMoved(object? sender, PointerEventArgs e)
        {
            if (sender is not NumericUpDown box || !Drags.TryGetValue(box, out var drag) || drag.Pointer != e.Pointer)
                return;

            e.Handled = true;
            var at = e.GetPosition(box);
            if (!drag.IsDragging)
            {
                if (Math.Abs(at.X - drag.Start.X) < DragThreshold)
                    return;

                drag.IsDragging = true;
                drag.Last = drag.Start;
                SetCursor(box, CanHoldPointer ? Hidden : Sideways);
            }

            double dx = at.X - drag.Last.X;
            if (CanHoldPointer)
            {
                // Put the pointer back where the drag began; the move that makes comes back with no distance.
                _ = SetCursorPos(drag.ScreenStart.X, drag.ScreenStart.Y);
                drag.Last = drag.Start;
            }
            else
            {
                drag.Last = at;
            }

            if (dx == 0)
                return;

            double perPixel = GetStep(box) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? ShiftFactor : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1 : 1);
            drag.Moved += dx * perPixel;

            decimal wanted = Math.Round(drag.StartValue + (decimal)drag.Moved, MidpointRounding.AwayFromZero);
            decimal value = Math.Clamp(wanted, box.Minimum, box.Maximum);

            // Held at a limit, the way back starts at once rather than after undoing the overshoot.
            if (value != wanted)
                drag.Moved = (double)(value - drag.StartValue);
            if (box.Value != value)
                box.Value = value;
        }

        private static void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is not NumericUpDown box || !Drags.TryGetValue(box, out var drag) || drag.Pointer != e.Pointer)
                return;

            e.Handled = true;
            bool dragged = drag.IsDragging;
            End(box);
            e.Pointer.Capture(null);

            // A click edits the number, as it would without dragging.
            if (!dragged && TextBoxOf(box) is { } text)
            {
                text.Focus(NavigationMethod.Pointer);
                text.SelectAll();
            }
        }

        private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (sender is NumericUpDown box)
                End(box);
        }

        private static void End(NumericUpDown box)
        {
            if (!Drags.TryGetValue(box, out _))
                return;

            Drags.Remove(box);
            UpdateHoverCursor(box);
        }

        private static TextBox? TextBoxOf(NumericUpDown box) =>
            TextBoxes.TryGetValue(box, out var text) ? text : box.FindDescendantOfType<TextBox>();

        private static bool CanHoldPointer => OperatingSystem.IsWindows();

        [DllImport("user32.dll")]
        private static extern int SetCursorPos(int x, int y);

        /// <summary>A press on the box, and the drag it becomes.</summary>
        /// <param name="Start">Where it was pressed, in the box.</param>
        /// <param name="ScreenStart">The same place on the screen, in pixels, where the pointer is held while dragging.</param>
        /// <param name="StartValue">The value when it was pressed.</param>
        private sealed record Drag(IPointer Pointer, Point Start, PixelPoint ScreenStart, decimal StartValue)
        {
            public bool IsDragging { get; set; }

            public Point Last { get; set; }

            /// <summary>How far the value has been dragged from <see cref="StartValue"/>, unrounded.</summary>
            public double Moved { get; set; }
        }
    }
}
