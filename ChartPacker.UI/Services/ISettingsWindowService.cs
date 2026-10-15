using System.Threading.Tasks;

namespace ChartPacker.UI.Services
{
    public interface ISettingsWindowService
    {
        /// <summary>Shows the Settings window and completes when it closes.</summary>
        Task ShowAsync();
    }
}
