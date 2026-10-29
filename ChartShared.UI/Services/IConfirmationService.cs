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
        /// <param name="confirmIcon">The line icon beside the confirming action, by its key, such as <c>TrashStroke</c>; null for none.</param>
        Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? confirmIcon = null);

        /// <summary>
        /// Asks the user to choose between two actions, such as Save or Discard, or to cancel. Cancelling or closing the
        /// prompt is <see cref="ConfirmChoice.Cancel"/>.
        /// </summary>
        /// <param name="firstIcon">The line icon beside the first action, by its key, such as <c>SaveStroke</c>; null for none.</param>
        /// <param name="secondIcon">The line icon beside the second action, by its key; null for none.</param>
        Task<ConfirmChoice> ChooseAsync(string title, string message, string firstLabel, string secondLabel, string? firstIcon = null, string? secondIcon = null);

        /// <summary>Tells the user something they must see before going on, with only OK to close it.</summary>
        Task AlertAsync(string title, string message);
    }
}
