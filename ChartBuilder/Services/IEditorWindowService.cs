using System.Collections.Generic;

namespace ChartBuilder.Services
{
    public interface IEditorWindowService
    {
        /// <summary>
        /// Opens a new Editor window in place of the launcher, which closes. <paramref name="audioFiles"/> are the
        /// ones New chart was given; the Editor can't use them yet, so it says they weren't added.
        /// </summary>
        void OpenEditor(IReadOnlyList<string> audioFiles);
    }
}
