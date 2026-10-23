using Avalonia;
using ChartBuilder.Services;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// Where the Editor's side panels and their sections (<see cref="EditorSidePanel"/>, <see cref="PanelSectionStack"/>)
    /// keep how the user arranged them. Set once on a window, it's inherited by everything in it; a panel or a tab without
    /// it can still be arranged, but forgets it when it's closed.
    /// </summary>
    public static class PanelLayout
    {
        public static readonly AttachedProperty<PanelLayoutStore?> StoreProperty =
            AvaloniaProperty.RegisterAttached<AvaloniaObject, PanelLayoutStore?>("Store", typeof(PanelLayout), inherits: true);

        public static PanelLayoutStore? GetStore(AvaloniaObject element) => element.GetValue(StoreProperty);

        public static void SetStore(AvaloniaObject element, PanelLayoutStore? value) => element.SetValue(StoreProperty, value);
    }
}
