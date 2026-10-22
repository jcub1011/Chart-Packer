using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartShared.UI.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// Gives a button's <c>MenuFlyout</c>, or a submenu, a lane menu (<see cref="LaneMenuItem"/>s) through
    /// <c>LaneMenu.Items</c>: a submenu per group, with the lane's own part and its group ticked, and a line for each
    /// <see cref="LaneMenuItem.Separator"/>. An item that isn't ticked shows its <see cref="LaneMenuItem.Icon"/>, a line
    /// icon looked up in the app's resources.
    /// </summary>
    /// <remarks>
    /// A button's menu is made anew each time it opens, from the list as it is then, in the one collection its flyout is
    /// given. A flyout's presenter keeps the list it was made with, seeing only changes made in that list: a new list set
    /// later was never shown, so after a lane's part changed its actions still went to the old part.
    /// </remarks>
    public static class LaneMenu
    {
        public static readonly AttachedProperty<IReadOnlyList<LaneMenuItem>?> ItemsProperty =
            AvaloniaProperty.RegisterAttached<Control, IReadOnlyList<LaneMenuItem>?>("Items", typeof(LaneMenu));

        // The button each followed flyout belongs to, for its items when it opens.
        private static readonly ConditionalWeakTable<MenuFlyout, Button> Owners = new();

        static LaneMenu()
        {
            ItemsProperty.Changed.AddClassHandler<Control>(OnItemsChanged);
            Button.FlyoutProperty.Changed.AddClassHandler<Button>(OnFlyoutChanged);
        }

        public static IReadOnlyList<LaneMenuItem>? GetItems(Control control) => control.GetValue(ItemsProperty);

        public static void SetItems(Control control, IReadOnlyList<LaneMenuItem>? items) => control.SetValue(ItemsProperty, items);

        /// <summary>The menu items of <paramref name="items"/>, for a <c>MenuFlyout</c>'s <c>ItemsSource</c>.</summary>
        public static List<Control> MenuItems(IReadOnlyList<LaneMenuItem> items) => [.. items.Select(item => item.IsSeparator ? (Control)new Separator() : new MenuItem
        {
            Header = item.Header,
            Command = item.Command,
            IsEnabled = item.IsEnabled,
            ItemsSource = item.Items is { } inner ? MenuItems(inner) : null,
            Icon = LineIcon.For(item.IsChecked ? "CheckStroke" : item.Icon),
        })];

        // A submenu has to have its items before it opens, to show it has any; a button's are made as its menu opens.
        private static void OnItemsChanged(Control control, AvaloniaPropertyChangedEventArgs e)
        {
            if (control is MenuItem item)
                item.ItemsSource = GetItems(item) is { } items ? MenuItems(items) : null;
            else if (control is Button { Flyout: MenuFlyout flyout } button)
                Follow(flyout, button);
        }

        // XAML sets a button's items before its flyout, so the flyout is followed once it's there.
        private static void OnFlyoutChanged(Button button, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.OldValue is MenuFlyout old)
            {
                old.Opening -= OnOpening;
                Owners.Remove(old);
            }
            if (e.NewValue is MenuFlyout flyout && button.IsSet(ItemsProperty))
                Follow(flyout, button);
        }

        private static void Follow(MenuFlyout flyout, Button button)
        {
            Owners.AddOrUpdate(flyout, button);
            if (flyout.ItemsSource is not ObservableCollection<Control>)
                flyout.ItemsSource = new ObservableCollection<Control>();
            flyout.Opening -= OnOpening;
            flyout.Opening += OnOpening;
        }

        private static void OnOpening(object? sender, EventArgs e)
        {
            if (sender is not MenuFlyout { ItemsSource: ObservableCollection<Control> shown } flyout
                || !Owners.TryGetValue(flyout, out var button))
                return;
            shown.Clear();
            if (GetItems(button) is { } items)
            {
                foreach (var item in MenuItems(items))
                    shown.Add(item);
            }
        }
    }
}
