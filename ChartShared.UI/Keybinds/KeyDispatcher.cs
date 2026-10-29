using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartShared.Core.Keybinds;

namespace ChartShared.UI.Keybinds;

/// <summary>
/// Turns the key presses of a window into the actions of a <see cref="KeyMap"/> and runs what the window registered for
/// them, so no window carries key bindings, key switches or held-key flags of its own.
/// </summary>
/// <remarks>
/// <para>
/// It listens as the event tunnels down, before the focused control, as the editor's Space handler did, so a focused button or
/// list doesn't take a key the window means. What it takes is therefore limited: while a text box has focus, a key that types
/// (no modifier, or Shift only, except the function keys) is left to the box, and so is any action that
/// <see cref="KeyAction.YieldsToTextInput"/>, such as Undo, which the box handles itself.
/// </para>
/// <para>
/// A press that ran an action is swallowed through its auto-repeats and its key up, so the focused control never sees half of
/// it, and it doesn't run again unless the action is <see cref="KeyAction.Repeatable"/>. A press with Alt that ran an action
/// also swallows the Alt key up, which Windows would take as a click on the menu bar.
/// </para>
/// <para>
/// Handlers are registered by action id with <c>On</c>; one that returns false (a command that can't run now) leaves the
/// key for the next action on it, and then for the focused control. Dispose what <c>On</c> returns to unregister.
/// </para>
/// </remarks>
public sealed class KeyDispatcher
{
    private static readonly ConditionalWeakTable<TopLevel, KeyDispatcher> Attached = new();

    private readonly KeyMap _map;
    private readonly Dictionary<string, List<Registration>> _handlers = new(StringComparer.Ordinal);

    // The keys whose press ran an action and haven't come up: what an auto-repeat or a key up is recognised by.
    private readonly Dictionary<HeldKey, Pressed> _pressed = [];

    private TopLevel? _top;
    private bool _altUsed;

    /// <param name="map">The keys.</param>
    /// <param name="scope">The innermost scope live in the window unless <see cref="ScopeProvider"/> says otherwise.</param>
    public KeyDispatcher(KeyMap map, KeyScope scope)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(scope);
        _map = map;
        Scope = scope;
    }

    /// <summary>The innermost scope live now, its ancestors being live too.</summary>
    public KeyScope Scope { get; set; }

    /// <summary>Gives the innermost scope live for a press, where it depends on focus (a lane canvas inside the editor); null for <see cref="Scope"/>.</summary>
    public Func<KeyScope>? ScopeProvider { get; set; }

    /// <summary>Whether no key should be taken now (a song is opening); null for never.</summary>
    public Func<bool>? IsBlocked { get; set; }

    /// <summary>
    /// Runs when the window is deactivated, before the keys held are let go of, for what a window lets go of that a held key's
    /// release depends on; null for nothing. Here rather than on the window's own <c>Deactivated</c>, so the order isn't that of
    /// the subscriptions.
    /// </summary>
    public Action? OnDeactivated { get; set; }

    /// <summary>The dispatcher listening to <paramref name="top"/>, or null when none is.</summary>
    public static KeyDispatcher? Of(TopLevel? top) => top is not null && Attached.TryGetValue(top, out var dispatcher) ? dispatcher : null;

    /// <summary>Starts listening to <paramref name="top"/>'s keys. A window is only given one dispatcher.</summary>
    public void Attach(TopLevel top)
    {
        ArgumentNullException.ThrowIfNull(top);
        Attached.Add(top, this);
        _top = top;
        top.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        top.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        if (top is Window window)
            window.Deactivated += (_, _) => ReleaseAll();
    }

    /// <summary>Runs <paramref name="run"/> when the action's key is pressed; it gives whether it took the key.</summary>
    public IDisposable On(string actionId, Func<bool> run) => Add(actionId, new Registration(run, null));

    /// <summary>Runs <paramref name="command"/> when the action's key is pressed and it can execute.</summary>
    public IDisposable On(string actionId, ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return On(actionId, () =>
        {
            if (!command.CanExecute(null))
                return false;
            command.Execute(null);
            return true;
        });
    }

    /// <summary>Runs <paramref name="down"/> when the key is pressed and <paramref name="up"/> when it's let go, or the window loses focus.</summary>
    public IDisposable OnHold(string actionId, Func<bool> down, Action up) => Add(actionId, new Registration(down, up));

    private IDisposable Add(string actionId, Registration registration)
    {
        if (!_handlers.TryGetValue(actionId, out var list))
            _handlers[actionId] = list = [];
        list.Add(registration);
        return new Removal(list, registration);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || KeyInput.HasMeta(e.KeyModifiers) || IsBlocked?.Invoke() == true)
            return;

        // A press whose character isn't a key (an IME's, or Alt+digit where the platform says System) can still be a key where
        // it is, which a physical scope reads.
        var logical = KeyInput.Logical(e);
        var physical = KeyInput.Physical(e);
        if (logical.IsModifierOnly && physical.IsModifierOnly)
            return;

        // The repeat of a press that ran something isn't a new press: swallowed, or run again if its action repeats. One with
        // other modifiers than the press had (Ctrl joined a held Space) is a press of its own chord, so it's resolved below.
        var key = HeldKey.Of(e);
        bool held = _pressed.TryGetValue(key, out var prior);
        bool swallow = held && !prior!.Action.Repeatable;
        if (swallow && prior!.Mods == logical.Mods)
        {
            e.Handled = true;
            return;
        }

        var actions = _map.Resolve(ScopeProvider?.Invoke() ?? Scope, logical, physical);
        bool typing = e.Source is TextBox || _top?.FocusManager?.GetFocusedElement() is TextBox;
        foreach (var action in actions)
        {
            if (!_handlers.TryGetValue(action.Id, out var registrations) || (typing && LeavesToTextBox(action, logical)))
                continue;

            foreach (var registration in registrations.ToArray())
            {
                if (!registration.Run())
                    continue;

                e.Handled = true;
                // The press this replaces is let go of, or a hold on it would never end.
                if (held && !ReferenceEquals(prior!.Up, registration.Up))
                    prior.Up?.Invoke();
                _pressed[key] = new Pressed(action, registration.Up, logical.Mods);
                if (logical.Mods.HasFlag(KeyMods.Alt))
                    _altUsed = true;
                return;
            }
        }

        // Nothing took the changed press, so it's still the held key's repeat.
        if (swallow)
            e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_pressed.Remove(HeldKey.Of(e), out var held))
        {
            held.Up?.Invoke();
            e.Handled = true;
        }
        else if (_altUsed && e.Key is Key.LeftAlt or Key.RightAlt)
        {
            // An Alt that was part of a chord isn't a tap on the menu bar.
            _altUsed = false;
            e.Handled = true;
        }
    }

    // Lets go of everything held, as when the window is switched away from with a key down, so nothing stays on.
    private void ReleaseAll()
    {
        OnDeactivated?.Invoke();
        foreach (var held in _pressed.Values)
            held.Up?.Invoke();
        _pressed.Clear();
        _altUsed = false;
    }

    private static bool LeavesToTextBox(KeyAction action, KeyChord logical)
    {
        if (action.FiresInTextInput)
            return false;
        if (action.YieldsToTextInput)
            return true;
        return logical.Mods is KeyMods.None or KeyMods.Shift && !KeyInput.IsFunctionKey(logical.Key);
    }

    private sealed record Registration(Func<bool> Run, Action? Up);

    private sealed record Pressed(KeyAction Action, Action? Up, KeyMods Mods);

    // A key as a press and its key up agree on it: where it is, or, for a platform that doesn't say, what it types, so
    // such presses don't all count as one key. Not the modifiers, which may be let go of first.
    private readonly record struct HeldKey(PhysicalKey Physical, Key Logical)
    {
        public static HeldKey Of(KeyEventArgs e) =>
            e.PhysicalKey == PhysicalKey.None ? new HeldKey(PhysicalKey.None, e.Key) : new HeldKey(e.PhysicalKey, Key.None);
    }

    private sealed class Removal(List<Registration> list, Registration registration) : IDisposable
    {
        public void Dispose() => list.Remove(registration);
    }
}
