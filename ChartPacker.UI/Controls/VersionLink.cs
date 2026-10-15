using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using ChartPacker.Resources;
using Serilog;

namespace ChartPacker.UI.Controls
{
    /// <summary>
    /// The program version in a window's title bar, e.g. <c>v1.2.0</c>, as a link that opens the project's releases on
    /// GitHub (<see cref="AppVersion.ReleasesUrl"/>) in the browser.
    /// </summary>
    /// <remarks>
    /// In a title bar drawn by the window (<c>ExtendClientAreaToDecorationsHint</c>), give it
    /// <c>chrome:WindowDecorationProperties.ElementRole="User"</c>, or clicking it drags the window instead.
    /// </remarks>
    public sealed class VersionLink : Button
    {
        public VersionLink()
        {
            Classes.Add("title-link");
            string version = string.Format(CultureInfo.CurrentCulture, SharedStrings.VersionFormat, AppVersion.Semantic);
            string tip = SharedStrings.VersionLinkToolTip;
            ToolTip.SetTip(this, tip);

            // Screen readers announce the version it shows, then what clicking it does.
            AutomationProperties.SetName(this, version);
            AutomationProperties.SetHelpText(this, tip);
            var text = new TextBlock
            {
                FontSize = 12,
                Text = version,
            };
            text.Classes.Add("muted");
            Content = text;
        }

        protected override Type StyleKeyOverride => typeof(Button);

        protected override void OnClick()
        {
            base.OnClick();
            _ = OpenReleasesAsync();
        }

        private async Task OpenReleasesAsync()
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher || !await launcher.LaunchUriAsync(new Uri(AppVersion.ReleasesUrl)))
                    Log.Warning("Couldn't open the releases page {Url}", AppVersion.ReleasesUrl);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Couldn't open the releases page {Url}", AppVersion.ReleasesUrl);
            }
        }
    }
}
