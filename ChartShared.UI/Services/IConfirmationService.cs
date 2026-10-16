using System.Threading.Tasks;

namespace ChartShared.UI.Services
{
    /// <summary>Which of a prompt's two actions the user chose, or that they cancelled it.</summary>
    public enum ConfirmChoice
    {
        Cancel,
        First,
        Second,
    }

    public interface IConfirmationService
    {
        /// <summary>
        /// Asks the user to confirm an action. Returns true only if they chose
        /// <paramref name="confirmLabel"/>; cancelling or closing the prompt returns false.
        /// </summary>
        Task<bool> ConfirmAsync(string title, string message, string confirmLabel);

        /// <summary>
        /// Asks the user to choose between two actions, such as Save or Discard, or to cancel. Cancelling or closing the
        /// prompt is <see cref="ConfirmChoice.Cancel"/>.
        /// </summary>
        Task<ConfirmChoice> ChooseAsync(string title, string message, string firstLabel, string secondLabel);
    }
}
