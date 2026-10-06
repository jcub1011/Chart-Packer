using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Metadata;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// One of the Editor's side panels: <see cref="Body"/> in a single tab headed <see cref="Title"/>, with a button
    /// that collapses it to a narrow strip along the window's edge, like a Visual Studio tool window set to
    /// auto-hide. Clicking the strip's title brings the panel back. Its look is in EditorSidePanel.axaml.
    /// </summary>
    [PseudoClasses(":collapsed", ":right")]
    public sealed class EditorSidePanel : TemplatedControl
    {
        /// <summary>How wide the panel is while collapsed. The Editor sizes its columns with it.</summary>
        public const double StripWidth = 28;

        public static readonly StyledProperty<string?> TitleProperty =
            AvaloniaProperty.Register<EditorSidePanel, string?>(nameof(Title));

        public static readonly StyledProperty<object?> BodyProperty =
            AvaloniaProperty.Register<EditorSidePanel, object?>(nameof(Body));

        public static readonly StyledProperty<bool> IsCollapsedProperty =
            AvaloniaProperty.Register<EditorSidePanel, bool>(nameof(IsCollapsed));

        public static readonly StyledProperty<ICommand?> ToggleCommandProperty =
            AvaloniaProperty.Register<EditorSidePanel, ICommand?>(nameof(ToggleCommand));

        public static readonly StyledProperty<bool> IsRightEdgeProperty =
            AvaloniaProperty.Register<EditorSidePanel, bool>(nameof(IsRightEdge));

        /// <summary>The tab's header, and the strip's title while collapsed.</summary>
        public string? Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>What the tab shows.</summary>
        /// <remarks>Not a logical child of the panel: the tab in its template holds it.</remarks>
        [Content]
        public object? Body
        {
            get => GetValue(BodyProperty);
            set => SetValue(BodyProperty, value);
        }

        /// <summary>Whether the panel is only its strip.</summary>
        public bool IsCollapsed
        {
            get => GetValue(IsCollapsedProperty);
            set => SetValue(IsCollapsedProperty, value);
        }

        /// <summary>Collapses or expands the panel, from its collapse button or its strip.</summary>
        public ICommand? ToggleCommand
        {
            get => GetValue(ToggleCommandProperty);
            set => SetValue(ToggleCommandProperty, value);
        }

        /// <summary>Whether the panel is along the window's right edge, which mirrors its chevron and strip.</summary>
        public bool IsRightEdge
        {
            get => GetValue(IsRightEdgeProperty);
            set => SetValue(IsRightEdgeProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsCollapsedProperty)
                PseudoClasses.Set(":collapsed", change.GetNewValue<bool>());
            else if (change.Property == IsRightEdgeProperty)
                PseudoClasses.Set(":right", change.GetNewValue<bool>());
        }
    }
}
