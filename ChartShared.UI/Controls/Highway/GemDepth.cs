using System;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// How deep a highway's gems are along its time axis, for every highway that draws them: Chart Builder's editor lanes and its
    /// preview, flat and in perspective. A gem is as deep as its highway is long, in proportion, whatever the highway's width, so a
    /// taller highway has deeper notes and a wider one has wider ones.
    /// </summary>
    public static class GemDepth
    {
        // How deep a gem is, as a share of how long its highway is. Flat, the highway's length is its height; in perspective it is
        // the host's, which the plane is several times as tall as, so the share is of the plane's pixels, which the tilt foreshortens, and larger.
        private const double ShareOfFlatLength = 0.024;
        private const double ShareOfPerspectiveLength = 0.04;

        // The least and the most, in pixels of the plane. The most stops a tall highway's gems growing into blocks: in perspective a gem
        // stands as tall as it is deep, so this is also its height. 24 is as deep as a gem is on a lane 600 pixels tall, flat or in perspective.
        private const double Least = 8;
        private const double MostFlat = 24;
        private const double MostPerspective = 24;

        /// <summary>
        /// How deep a gem is, in pixels of its plane, on a highway <paramref name="length"/> pixels long: its height flat, and in
        /// perspective the host's.
        /// </summary>
        public static double For(double length, bool perspective) => perspective
            ? Math.Clamp(length * ShareOfPerspectiveLength, Least, MostPerspective)
            : Math.Clamp(length * ShareOfFlatLength, Least, MostFlat);
    }
}
