using System.Collections.Generic;

namespace ChartPacker.Services
{
    public interface IChartBuilderLauncher
    {
        /// <summary>
        /// Starts Chart Builder with <paramref name="arguments"/>, built with <see cref="Core.Launching.BuilderArgs"/>
        /// (charter-spec §4.8).
        /// </summary>
        /// <returns>Null once it's started, or why it couldn't be, to show the user.</returns>
        string? Start(IReadOnlyList<string> arguments);
    }
}
