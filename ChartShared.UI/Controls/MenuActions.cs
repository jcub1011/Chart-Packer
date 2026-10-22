using System;
using System.Windows.Input;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.Controls
{
    /// <summary>Menu items built in code, for menus filled as they're asked for.</summary>
    public static class MenuActions
    {
        /// <summary>
        /// An item that runs <paramref name="command"/>, beside the line icon keyed <paramref name="icon"/>, greyed out
        /// unless <paramref name="isEnabled"/>.
        /// </summary>
        public static MenuItem Create(string header, ICommand command, string? icon, bool isEnabled = true) =>
            new() { Header = header, Command = command, Icon = LineIcon.For(icon), IsEnabled = isEnabled };

        /// <summary>An item that runs <paramref name="action"/>; otherwise as the one that runs a command.</summary>
        public static MenuItem Create(string header, Action action, string? icon, bool isEnabled = true) =>
            Create(header, new RelayCommand(action), icon, isEnabled);
    }
}
