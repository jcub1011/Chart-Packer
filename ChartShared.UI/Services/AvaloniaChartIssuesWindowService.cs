using System;
using System.Collections.Generic;
using Avalonia.Controls;
using ChartShared.UI.ViewModels;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services
{
    /// <summary>Shows a <see cref="ChartIssuesWindow"/> owned by the owner window, not modal, so the song can still be worked on.</summary>
    public sealed class AvaloniaChartIssuesWindowService(Func<Window?> owner) : IChartIssuesWindowService
    {
        // The windows open, by what they show.
        private readonly Dictionary<ChartIssuesViewModel, ChartIssuesWindow> _open = [];

        public void Show(ChartIssuesViewModel issues)
        {
            ArgumentNullException.ThrowIfNull(issues);
            if (_open.TryGetValue(issues, out var open))
            {
                open.Activate();
                return;
            }

            if (owner() is not { } window)
                return;

            var shown = new ChartIssuesWindow { DataContext = issues };
            WindowChrome.UseOwnerIcon(shown, window);
            _open[issues] = shown;
            shown.Closed += (_, _) => _open.Remove(issues);
            shown.Show(window);
        }
    }
}
