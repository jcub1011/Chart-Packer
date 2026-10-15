using ChartPacker.UI.ViewModels;

namespace ChartPacker.UI.Services
{
    public interface IChartIssuesWindowService
    {
        /// <summary>
        /// Shows <paramref name="issues"/> in a window of its own beside the owner, which keeps up with them as they change;
        /// one already showing them is brought to the front.
        /// </summary>
        void Show(ChartIssuesViewModel issues);
    }
}
