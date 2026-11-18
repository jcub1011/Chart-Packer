using System;
using System.Globalization;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Layout;
using ChartShared.UI.Services;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The layout templates' prompts (issue 263) that both the View menu and Manage Layouts ask, so the two ask alike.
    /// </summary>
    internal static class LayoutPrompts
    {
        /// <summary>
        /// Puts the lanes <paramref name="current"/> gives in the template named <paramref name="name"/>, once the user
        /// confirms it.
        /// </summary>
        /// <returns>Whether the template was overwritten.</returns>
        public static async Task<bool> OverwriteAsync(IConfirmationService confirmations, LayoutTemplateStore store, string name, Func<LaneDockState> current) =>
            await confirmations.ConfirmAsync(
                Strings.LayoutOverwriteTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.LayoutOverwriteMessageFormat, name),
                Strings.LayoutOverwriteButton,
                "OverwriteStroke")
            && store.Overwrite(name, current());

        /// <summary>
        /// Whether to load the template named <paramref name="name"/> over the lanes <paramref name="open"/>: without
        /// asking when they have none, or a template already is them, and otherwise once the user confirms it, as loading
        /// loses them for good.
        /// </summary>
        public static async Task<bool> ConfirmLoadAsync(IConfirmationService confirmations, LayoutTemplateStore store, string name, LaneDockState open) =>
            open.Root is null
            || store.HasLanes(open)
            || await confirmations.ConfirmAsync(
                Strings.LayoutLoadTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.LayoutLoadMessageFormat, name),
                Strings.LayoutLoadButton,
                "ViewStroke");
    }
}
