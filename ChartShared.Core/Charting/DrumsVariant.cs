namespace ChartShared.Core.Charting;

/// <summary>How a song's drums are charted, which <c>song.ini</c>'s <c>pro_drums</c> and <c>five_lane_drums</c> record.</summary>
public enum DrumsVariant
{
    /// <summary>Four pads and a kick, without cymbals.</summary>
    FourLane,

    /// <summary>Four pads and a kick; yellow, blue and green can be cymbals.</summary>
    Pro,

    /// <summary>Five pads and a kick.</summary>
    FiveLane,
}
