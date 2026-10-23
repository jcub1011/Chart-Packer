namespace ChartShared.Core.Updating;

/// <summary>How often the title bar's version badge checks GitHub for newer releases.</summary>
public enum UpdateCheckFrequency
{
    /// <summary>At most once an hour; a launch within the hour reuses the cached result.</summary>
    Hourly,

    /// <summary>At most once a day; a launch within the day reuses the cached result.</summary>
    Daily,

    /// <summary>On every launch, however recently the last check ran.</summary>
    OnStartup,
}
