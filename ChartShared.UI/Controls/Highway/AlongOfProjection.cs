using System;
using ChartShared.Core.Charting.Editing;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// Where a second is along a lane's time axis, as a painter takes it: one delegate, of whichever projection it was last pointed
    /// at, rather than one made for each lane drawn, each frame.
    /// </summary>
    /// <remarks>A painter reads it as it begins drawing, so it's pointed at the projection just before it's handed over.</remarks>
    public sealed class AlongOfProjection
    {
        private readonly Func<double, double> _alongOf;
        private LaneProjection? _projection;

        public AlongOfProjection()
        {
            _alongOf = seconds => _projection!.AlongOf(seconds);
        }

        /// <summary>The delegate, pointed at <paramref name="projection"/> until it's next asked for.</summary>
        public Func<double, double> For(LaneProjection projection)
        {
            _projection = projection;
            return _alongOf;
        }
    }
}
