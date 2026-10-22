using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using ChartShared.Core;
using ChartShared.UI.Resources;
using Serilog;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// The program version in a window's title bar, e.g. <c>v1.2.0</c>, as a link that opens the project's releases on
    /// GitHub (<see cref="AppVersion.ReleasesUrl"/>) in the browser. When a newer release is available, a
    /// <c>circle-arrow-up</c> badge shows beside it: green for a stable release, orange for a pre-release.
    /// </summary>
    /// <remarks>
    /// In a title bar drawn by the window (<c>ExtendClientAreaToDecorationsHint</c>), give it
    /// <c>chrome:WindowDecorationProperties.ElementRole="User"</c>, or clicking it drags the window instead.
    /// Each instance starts an <see cref="UpdateCheck"/> on attach (run only when due, shared across apps via
    /// <c>update-settings.json</c>) and refreshes whenever any check ends.
    /// </remarks>
    public sealed class VersionLink : Button
    {
        /// <summary>The latest available version tag, e.g. <c>v1.2.0</c>, or null when up to date.</summary>
        public static readonly StyledProperty<string?> LatestVersionProperty =
            AvaloniaProperty.Register<VersionLink, string?>(nameof(LatestVersion));

        /// <summary>Whether <see cref="LatestVersion"/> is a pre-release. Only applies while it isn't null.</summary>
        public static readonly StyledProperty<bool> IsPrereleaseProperty =
            AvaloniaProperty.Register<VersionLink, bool>(nameof(IsPrerelease));

        private readonly TextBlock _text;
        private readonly LineIcon _badge;
        private string? _badgeBrushKey;
        private IDisposable? _badgeBrush;

        public VersionLink()
        {
            Classes.Add("title-link");
            string version = string.Format(CultureInfo.CurrentCulture, SharedStrings.VersionFormat, AppVersion.Semantic);
            _text = new TextBlock
            {
                FontSize = 12,
                Text = version,
            };
            _text.Classes.Add("muted");
            _badge = new LineIcon
            {
                Width = 12,
                Height = 12,
                IsVisible = false,
            };
            var panel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                Children = { _text, _badge },
            };
            Content = panel;
            RefreshBadge();
        }

        /// <summary>The latest available version tag, or null when up to date.</summary>
        public string? LatestVersion
        {
            get => GetValue(LatestVersionProperty);
            set => SetValue(LatestVersionProperty, value);
        }

        /// <summary>Whether <see cref="LatestVersion"/> is a pre-release.</summary>
        public bool IsPrerelease
        {
            get => GetValue(IsPrereleaseProperty);
            set => SetValue(IsPrereleaseProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(Button);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            UpdateCheck.Checked += ShowAvailable;
            ShowAvailable();
            UpdateCheck.Start(force: false);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            UpdateCheck.Checked -= ShowAvailable;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == LatestVersionProperty || change.Property == IsPrereleaseProperty)
                RefreshBadge();
        }

        protected override void OnClick()
        {
            base.OnClick();
            _ = OpenReleasesAsync();
        }

        private void ShowAvailable()
        {
            try
            {
                (LatestVersion, IsPrerelease) = UpdateCheck.Available();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Couldn't read the cached update check");
                (LatestVersion, IsPrerelease) = (null, false);
            }
        }

        private void RefreshBadge()
        {
            string version = string.Format(CultureInfo.CurrentCulture, SharedStrings.VersionFormat, AppVersion.Semantic);
            string tip;
            if (LatestVersion is { } latest)
            {
                _badge.IsVisible = true;
                _badge.Data = LineIcon.Find("UpdateAvailableStroke");
                BindBadgeBrush(IsPrerelease ? "WarningTextBrush" : "DifficultyEasyBrush");
                tip = string.Format(
                    CultureInfo.CurrentCulture,
                    IsPrerelease ? SharedStrings.VersionUpdatePrereleaseToolTipFormat : SharedStrings.VersionUpdateAvailableToolTipFormat,
                    latest);
            }
            else
            {
                _badge.IsVisible = false;
                tip = SharedStrings.VersionLinkToolTip;
            }

            ToolTip.SetTip(this, tip);
            AutomationProperties.SetName(this, LatestVersion is null ? version : $"{version}, {tip}");
            AutomationProperties.SetHelpText(this, tip);
        }

        /// <summary>Colours the badge from <paramref name="key"/> as a dynamic resource, so it follows the theme.</summary>
        private void BindBadgeBrush(string key)
        {
            if (key == _badgeBrushKey)
                return;
            _badgeBrush?.Dispose();
            _badgeBrushKey = key;
            _badgeBrush = _badge.Bind(LineIcon.ForegroundProperty, _badge.GetResourceObservable(key));
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
