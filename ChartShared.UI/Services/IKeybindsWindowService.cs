using System.Threading.Tasks;

namespace ChartShared.UI.Services
{
    public interface IKeybindsWindowService
    {
        /// <summary>Shows the keyboard shortcuts window and completes when it closes.</summary>
        Task ShowAsync();
    }
}
