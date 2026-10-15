using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

namespace ChartPacker.UI.Controls
{
    /// <summary>
    /// The title bar a window draws for itself (<c>ExtendClientAreaToDecorationsHint</c>): the program's icon, its
    /// <see cref="Menu"/>, then <see cref="Content"/>, such as the version. Dragging anywhere else on it moves the window.
    /// </summary>
    /// <remarks>
    /// Avalonia draws the caption buttons over its right end, which the last column keeps clear: minimize, maximize and
    /// close, 45 wide with 2 between, inside the 1px frame. It's styled as <c>Border.title-bar</c> (Theme.axaml), 30 high
    /// as the caption buttons are. Give the menu and anything clickable in the content
    /// <c>chrome:WindowDecorationProperties.ElementRole="User"</c>, or clicking them drags the window instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// &lt;ctl:TitleBar Icon="/Assets/avalonia-logo.ico" AppName="{x:Static res:Strings.AppName}"&gt;
    ///     &lt;ctl:TitleBar.Menu&gt;&lt;Menu chrome:WindowDecorationProperties.ElementRole="User"&gt;…&lt;/Menu&gt;&lt;/ctl:TitleBar.Menu&gt;
    ///     &lt;ctl:VersionLink chrome:WindowDecorationProperties.ElementRole="User" /&gt;
    /// &lt;/ctl:TitleBar&gt;
    /// </code>
    /// </example>
    public sealed class TitleBar : Border
    {
        // What the caption buttons cover: three 45 wide, 2 between, and the frame.
        private const double CaptionButtonsWidth = 140;

        public static readonly StyledProperty<IImage?> IconProperty =
            AvaloniaProperty.Register<TitleBar, IImage?>(nameof(Icon));

        public static readonly StyledProperty<string?> AppNameProperty =
            AvaloniaProperty.Register<TitleBar, string?>(nameof(AppName));

        public static readonly StyledProperty<Control?> MenuProperty =
            AvaloniaProperty.Register<TitleBar, Control?>(nameof(Menu));

        public static readonly StyledProperty<Control?> ContentProperty =
            AvaloniaProperty.Register<TitleBar, Control?>(nameof(Content));

        private readonly Grid _grid;
        private readonly Image _icon;

        public TitleBar()
        {
            Classes.Add("title-bar");
            WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.TitleBar);
            _icon = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(12, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(CaptionButtonsWidth, GridUnitType.Pixel),
                },
            };
            _grid.Children.Add(_icon);
            Child = _grid;
        }

        // Styled as a Border, so Border.title-bar applies.
        protected override Type StyleKeyOverride => typeof(Border);

        /// <summary>The program's icon, at the left end.</summary>
        public IImage? Icon
        {
            get => GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        /// <summary>The program's name, which screen readers give the icon.</summary>
        public string? AppName
        {
            get => GetValue(AppNameProperty);
            set => SetValue(AppNameProperty, value);
        }

        /// <summary>The window's menus, after the icon.</summary>
        public Control? Menu
        {
            get => GetValue(MenuProperty);
            set => SetValue(MenuProperty, value);
        }

        /// <summary>What follows the menus, such as the window's title and the version, up to the caption buttons.</summary>
        [Content]
        public Control? Content
        {
            get => GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IconProperty)
                _icon.Source = Icon;
            else if (change.Property == AppNameProperty)
                AutomationProperties.SetName(_icon, AppName);
            else if (change.Property == MenuProperty)
                Place(change.GetOldValue<Control?>(), Menu, 1);
            else if (change.Property == ContentProperty)
                Place(change.GetOldValue<Control?>(), Content, 2);
        }

        private void Place(Control? old, Control? control, int column)
        {
            if (old is not null)
                _grid.Children.Remove(old);
            if (control is null)
                return;

            Grid.SetColumn(control, column);
            _grid.Children.Add(control);
        }
    }
}
