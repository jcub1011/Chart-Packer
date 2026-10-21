using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ChartBuilder.ViewModels;
using ChartShared.UI.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// Makes a lane menu (<see cref="LaneMenuItem"/>s) into menu items for a <c>MenuFlyout</c>'s <c>ItemsSource</c>: a
    /// submenu per group, with the lane's own part and its group ticked with <see cref="Check"/>, and a line for each
    /// <see cref="LaneMenuItem.Separator"/>. An item that isn't ticked shows its <see cref="LaneMenuItem.Icon"/>, a line
    /// icon looked up in the app's resources.
    /// </summary>
    public sealed class LaneMenuConverter : IValueConverter
    {
        public Geometry? Check { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is IReadOnlyList<LaneMenuItem> items ? MenuItems(items) : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        /// <summary>The menu items of <paramref name="items"/>, for a <c>MenuFlyout</c>'s <c>ItemsSource</c>.</summary>
        public List<Control> MenuItems(IReadOnlyList<LaneMenuItem> items) => [.. items.Select(item => item.IsSeparator ? (Control)new Separator() : new MenuItem
        {
            Header = item.Header,
            Command = item.Command,
            IsEnabled = item.IsEnabled,
            ItemsSource = item.Items is { } inner ? MenuItems(inner) : null,
            Icon = item.IsChecked ? new LineIcon { Data = Check } : IconOf(item.Icon),
        })];

        private static LineIcon? IconOf(string? key) =>
            key is not null && Application.Current?.TryGetResource(key, null, out var data) == true && data is Geometry geometry
                ? new LineIcon { Data = geometry }
                : null;
    }
}
