using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;

namespace ChartShared.UI.Keybinds;

/// <summary>
/// Shows an action's key on a control, kept up to date as the key is rebound: <c>Shortcut.Action</c> names the action, and
/// <c>Shortcut.Tip</c> is the tooltip's text, which gets the key appended, <c>Undo the last change (Ctrl+Z)</c>. On a menu
/// item the action's primary key is also its shortcut text. Nothing is appended for an action with no key.
/// </summary>
/// <remarks>
/// Resource strings therefore don't carry their key: the tooltip is the text, and the key is whatever the user has made it.
/// </remarks>
public static class Shortcut
{
    public static readonly AttachedProperty<string?> ActionProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, string?>("Action");

    public static readonly AttachedProperty<string?> TipProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, string?>("Tip");

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, string?>("Text");

    // The controls on screen that show a key, each with its own subscription to a rebind. A subscription lasts from the
    // control's attaching to its detaching, and holds the control weakly, so a rebind never keeps a control alive.
    private static readonly ConditionalWeakTable<Control, Subscription> Subscriptions = new();

    static Shortcut()
    {
        ActionProperty.Changed.AddClassHandler<Control>((c, _) => Refresh(c));
        TipProperty.Changed.AddClassHandler<Control>((c, _) => Refresh(c));
        TextProperty.Changed.AddClassHandler<Control>((c, _) => Refresh(c));
    }

    public static string? GetAction(Control control) => control.GetValue(ActionProperty);

    public static void SetAction(Control control, string? value) => control.SetValue(ActionProperty, value);

    public static string? GetTip(Control control) => control.GetValue(TipProperty);

    public static void SetTip(Control control, string? value) => control.SetValue(TipProperty, value);

    public static string? GetText(Control control) => control.GetValue(TextProperty);

    public static void SetText(Control control, string? value) => control.SetValue(TextProperty, value);

    /// <summary><paramref name="text"/> with the action's primary key after it in brackets; just the text when the action has none.</summary>
    public static string WithKey(string text, string actionId)
    {
        string key = AppKeys.Map.Display(actionId);
        return key.Length == 0 ? text : $"{text} ({key})";
    }

    // Draws the key now, attached or not, so a control that is measured offscreen or never attached has its text; the
    // subscription then keeps it up to date once it is on screen.
    private static void Refresh(Control control)
    {
        Track(control);
        Update(control);
    }

    private static void Track(Control control)
    {
        // Static handlers, and removed before added, so a property set twice leaves one of each and nothing holds the control.
        control.AttachedToVisualTree -= OnAttached;
        control.AttachedToVisualTree += OnAttached;
        control.DetachedFromVisualTree -= OnDetached;
        control.DetachedFromVisualTree += OnDetached;
        if (TopLevel.GetTopLevel(control) is not null)
            Subscribe(control);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
            Subscribe(control);
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
            Unsubscribe(control);
    }

    // Draws the control's key now, as it attaches, and again whenever the keys change until it detaches.
    private static void Subscribe(Control control)
    {
        Unsubscribe(control);
        Subscriptions.Add(control, new Subscription(control));
        Update(control);
    }

    private static void Unsubscribe(Control control)
    {
        if (!Subscriptions.TryGetValue(control, out var subscription))
            return;
        subscription.Dispose();
        Subscriptions.Remove(control);
    }

    /// <summary>
    /// One control's listening to <see cref="AppKeys.Changed"/>, which outlives every window. The event holds this and this
    /// holds the control only weakly; detaching ends it, and one that is missed ends itself on the next change, once the
    /// control has been collected.
    /// </summary>
    private sealed class Subscription : IDisposable
    {
        private readonly WeakReference<Control> _control;

        public Subscription(Control control)
        {
            _control = new WeakReference<Control>(control);
            AppKeys.Changed += OnChanged;
        }

        public void Dispose() => AppKeys.Changed -= OnChanged;

        private void OnChanged(object? sender, EventArgs e)
        {
            if (_control.TryGetTarget(out var control))
                Update(control);
            else
                Dispose();
        }
    }

    private static void Update(Control control)
    {
        // With no action (never set, or cleared) the tip and text are shown without a key, so a cleared action leaves none behind.
        string id = GetAction(control) ?? "";

        if (GetTip(control) is { } text)
            ToolTip.SetTip(control, WithKey(text, id));

        // A heading that names its key: the text is the name, and the key is whatever the user has made it.
        if (control is TextBlock block && GetText(control) is { } name)
            block.Text = WithKey(name, id);

        if (id.Length == 0)
        {
            control.ClearValue(AutomationProperties.AcceleratorKeyProperty);
            if (control is MenuItem cleared)
                cleared.ClearValue(MenuItem.InputGestureProperty);
            return;
        }

        // What a screen reader says the control's key is.
        string key = AppKeys.Map.Display(id);
        if (key.Length == 0)
            control.ClearValue(AutomationProperties.AcceleratorKeyProperty);
        else
            AutomationProperties.SetAcceleratorKey(control, key);

        if (control is MenuItem item)
            item.InputGesture = MenuGesture(id);
    }

    // The primary key as a menu item's gesture, which Avalonia prints itself: so only a key it prints as the key is named
    // (Ctrl+Z, F5), never one it would print as a code (Ctrl+OemComma, Alt+D1). That one is in the tooltip instead.
    private static KeyGesture? MenuGesture(string id)
    {
        var chords = AppKeys.Map.ChordsOf(id);
        if (chords.Count == 0 || KeyInput.ToGesture(chords[0]) is not { } gesture)
            return null;
        return gesture.ToString() == chords[0].ToDisplayString() ? gesture : null;
    }
}
