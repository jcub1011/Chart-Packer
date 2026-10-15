using System.Threading.Tasks;

namespace ChartShared.UI.Services
{
    public interface IImagePickerService
    {
        /// <summary>Shows a picker for a png, jpg or jpeg image and returns the chosen local path, or <see langword="null"/> if cancelled.</summary>
        Task<string?> PickImageAsync(string title);
    }
}
