using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace ChartShared.UI.Services
{
    public static class StorageStartFolder
    {
        /// <summary>
        /// <paramref name="folder"/> as a picker start location, or <see langword="null"/> (the system's
        /// default) when there is none or it no longer exists.
        /// </summary>
        public static async Task<IStorageFolder?> TryGetAsync(IStorageProvider storage, string? folder)
        {
            try
            {
                return Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }
}
