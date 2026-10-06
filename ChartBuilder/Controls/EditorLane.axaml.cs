using Avalonia.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>One of the Editor's lanes, for an <see cref="ViewModels.EditorLaneViewModel"/>. Its look is in EditorLane.axaml.</summary>
    public partial class EditorLane : UserControl
    {
        public EditorLane()
        {
            InitializeComponent();
        }

        /// <summary>The header, which <see cref="LaneDockHost"/> drags the lane by.</summary>
        public Control DragHandle => Header;
    }
}
