using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ChartShared.Core.Help;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// The <c>?</c> in a window's title bar, which opens the window's help page
    /// (<see cref="HelpPages"/>, at this version's tag) in the browser. The <see cref="SharedKeys.Help"/> key (F1 by default) does the same from
    /// anywhere in the window while the button is in it.
    /// </summary>
    /// <remarks>
    /// A <see cref="TitleBar"/> shows one when it has a <see cref="TitleBar.HelpPage"/>, so a window doesn't make its own.
    /// A window that has no <see cref="KeyDispatcher"/> of its own is given one for the key as the button is shown, so a window
    /// that wants its own must attach it before it's shown (in its constructor), as a window has only one.
    /// </remarks>
    public sealed class HelpButton : Button
    {
        public static readonly StyledProperty<string?> PageProperty =
            AvaloniaProperty.Register<HelpButton, string?>(nameof(Page));

        private IDisposable? _key;

        public HelpButton()
        {
            Classes.Add("title-link");
            WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.User);
            VerticalAlignment = VerticalAlignment.Center;
            Margin = new Thickness(0, 0, 4, 0);
            Focusable = false;
            var mark = new TextBlock
            {
                Text = "?",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                MinWidth = 12,
                TextAlignment = TextAlignment.Center,
            };
            mark.Classes.Add("muted");
            Content = mark;
            ToolTip.SetTip(this, SharedStrings.HelpButtonToolTip);
            AutomationProperties.SetName(this, SharedStrings.HelpButtonToolTip);
        }

        /// <summary>The help page to open, one of <see cref="HelpPages"/>; the front page when null.</summary>
        public string? Page
        {
            get => GetValue(PageProperty);
            set => SetValue(PageProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(Button);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (TopLevel.GetTopLevel(this) is not { } top)
                return;

            var keys = KeyDispatcher.Of(top);
            if (keys is null)
            {
                keys = new KeyDispatcher(AppKeys.Map, KeyScope.Global);
                keys.Attach(top);
            }

            _key = keys.On(SharedKeys.Help, () =>
            {
                _ = OpenAsync();
                return true;
            });
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _key?.Dispose();
            _key = null;
        }

        protected override void OnClick()
        {
            base.OnClick();
            _ = OpenAsync();
        }

        private Task OpenAsync() => HelpLauncher.OpenPageAsync(TopLevel.GetTopLevel(this), Page);
    }
}
