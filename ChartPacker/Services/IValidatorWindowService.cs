using System.Collections.Generic;
using System.Threading.Tasks;
using ChartShared.Core.Scanning;

namespace ChartPacker.Services
{
    public interface IValidatorWindowService
    {
        /// <summary>
        /// Shows the Validator window for <paramref name="entries"/> and completes when it closes. Edits made in
        /// it are drafts of the songs like any other, so unsaved ones are still there afterwards.
        /// </summary>
        Task ShowAsync(IReadOnlyList<SongEntry> entries);
    }
}
