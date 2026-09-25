using System.Threading.Tasks;

namespace ChartPacker.Services
{
    public interface IConfirmationService
    {
        /// <summary>
        /// Asks the user to confirm an action. Returns true only if they chose
        /// <paramref name="confirmLabel"/>; cancelling or closing the prompt returns false.
        /// </summary>
        Task<bool> ConfirmAsync(string title, string message, string confirmLabel);
    }
}
