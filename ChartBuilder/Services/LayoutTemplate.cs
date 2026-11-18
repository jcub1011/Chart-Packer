using ChartShared.Core.Charting.Layout;

namespace ChartBuilder.Services
{
    /// <summary>
    /// A layout template (issue 263): a named arrangement of the Editor's lanes, kept in <see cref="BuilderSettings"/>.
    /// Loading it puts its lanes in place of those open; changing the lanes afterwards never changes it.
    /// </summary>
    public sealed class LayoutTemplate
    {
        /// <summary>The name the View menu and Manage layouts list it by: unique, ignoring case.</summary>
        public string? Name { get; set; }

        /// <summary>The lanes, as <see cref="LaneDockLayout.ToState"/> gives them.</summary>
        public LaneDockState? Lanes { get; set; }
    }
}
