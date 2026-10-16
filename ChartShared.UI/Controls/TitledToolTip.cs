using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// A tooltip's content: <see cref="Title"/>, such as a toolbar button's name, in bold over a short
    /// <see cref="Description"/>, so an icon-only button still says what it is.
    /// </summary>
    /// <example>
    /// <c>&lt;ToolTip.Tip&gt;&lt;ctl:TitledToolTip Title="{x:Static res:Strings.SaveButton}" Description="{x:Static res:Strings.SaveToolTip}" /&gt;&lt;/ToolTip.Tip&gt;</c>
    /// </example>
    public sealed class TitledToolTip : StackPanel
    {
        public static readonly StyledProperty<string?> TitleProperty =
            AvaloniaProperty.Register<TitledToolTip, string?>(nameof(Title));

        public static readonly StyledProperty<string?> DescriptionProperty =
            AvaloniaProperty.Register<TitledToolTip, string?>(nameof(Description));

        private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };

        public TitledToolTip()
        {
            MaxWidth = 320;
            Spacing = 2;
            Children.Add(_title);
            Children.Add(_description);
        }

        public string? Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string? Description
        {
            get => GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == TitleProperty)
                _title.Text = Title;
            else if (change.Property == DescriptionProperty)
                _description.Text = Description;
        }
    }
}
