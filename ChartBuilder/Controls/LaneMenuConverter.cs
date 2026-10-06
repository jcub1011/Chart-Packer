using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ChartBuilder.ViewModels;
using ChartPacker.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// Makes a lane menu (<see cref="LaneMenuItem"/>s) into menu items for a <c>MenuFlyout</c>'s <c>ItemsSource</c>: a
    /// submenu per group, with the lane's own part and its group ticked with <see cref="Check"/>.
    /// </summary>
    public sealed class LaneMenuConverter : IValueConverter
    {
        public Geometry? Check { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is IReadOnlyList<LaneMenuItem> items ? MenuItems(items) : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private List<MenuItem> MenuItems(IReadOnlyList<LaneMenuItem> items) => [.. items.Select(item => new MenuItem
        {
            Header = item.Header,
            Command = item.Command,
            IsEnabled = item.IsEnabled,
            ItemsSource = item.Items is { } inner ? MenuItems(inner) : null,
            Icon = item.IsChecked ? new LineIcon { Data = Check } : null,
        })];
    }
}
