using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartShared.Core.Help;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// The About menu, with the links to the documentation: the Getting Started guide and the third-party notices. Each opens its
    /// file in the browser at this version's tag (<see cref="HelpLauncher"/>), as the help button does, and says so in an alert
    /// when it couldn't, as a click that does nothing looks broken.
    /// </summary>
    /// <remarks>Both apps put one in their title bar's menu, so the items are defined here, once.</remarks>
    public sealed class AboutMenuItem : MenuItem
    {
        public AboutMenuItem()
        {
            Header = SharedStrings.MenuAbout;
            Items.Add(Link(SharedStrings.MenuGettingStarted, SharedStrings.MenuGettingStartedToolTip,
                top => HelpLauncher.OpenPageAsync(top, HelpPages.GettingStarted)));
            Items.Add(Link(SharedStrings.MenuThirdPartyNotices, SharedStrings.MenuThirdPartyNoticesToolTip,
                HelpLauncher.OpenNoticesAsync));
        }

        protected override Type StyleKeyOverride => typeof(MenuItem);

        private MenuItem Link(string header, string toolTip, Func<TopLevel?, Task<bool>> open)
        {
            var item = new MenuItem { Header = header };
            ToolTip.SetTip(item, toolTip);
            // The window is found from this item, not the link's: a link is in the submenu's popup, which has no launcher.
            item.Click += async (_, _) => await OpenAsync(open);
            return item;
        }

        private async Task OpenAsync(Func<TopLevel?, Task<bool>> open)
        {
            var top = TopLevel.GetTopLevel(this);
            if (await open(top))
                return;

            await new AvaloniaConfirmationService(() => top as Window)
                .AlertAsync(SharedStrings.HelpOpenFailedTitle, SharedStrings.HelpOpenFailed);
        }
    }
}
