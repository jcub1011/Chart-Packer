using System.Text.Json.Nodes;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Updating;

/// <summary>A snapshot of <c>update-settings.json</c>, read in one go.</summary>
/// <param name="CheckForUpdates">Whether the update check runs at all. True when unset or unreadable (on by default).</param>
/// <param name="CheckFrequency">How often the update check runs. Daily when unset, unknown or unreadable.</param>
/// <param name="IncludePrereleases">Whether pre-releases count as available updates. False when unset or unreadable.</param>
/// <param name="LastCheckedUtc">When the last successful check ran, or null when none has.</param>
/// <param name="CachedTag">The tag the last check found, or null when it found nothing newer.</param>
/// <param name="CachedPrerelease">Whether <paramref name="CachedTag"/> is a pre-release.</param>
/// <param name="CachedIncludePrereleases">
/// The <paramref name="IncludePrereleases"/> the cached result was found with, or null when unknown. A cache found
/// under the other setting, by this app or the other one, is out of date.
/// </param>
public sealed record UpdateSettings(
    bool CheckForUpdates,
    UpdateCheckFrequency CheckFrequency,
    bool IncludePrereleases,
    DateTimeOffset? LastCheckedUtc,
    string? CachedTag,
    bool CachedPrerelease,
    bool? CachedIncludePrereleases);

/// <summary>
/// <c>update-settings.json</c>: shared update preferences read and written by both apps.
/// Holds whether the update check runs and whether pre-releases count as available updates,
/// plus the last check time and its cached result so the title bar badge shows instantly on launch.
/// <c>{ "checkForUpdates": true, "checkFrequency": "daily", "includePrereleases": false, "lastCheckedUtc": "...", "cachedTag": "v1.2.0", "cachedPrerelease": false, "cachedIncludePrereleases": false }</c>.
/// </summary>
/// <remarks>
/// Several processes may share the file, so a save re-reads it and changes only the keys it owns,
/// keeping any keys it doesn't know. The path is always passed in; tests pass a temp file.
/// </remarks>
public sealed class UpdateSettingsFile
{
    private readonly string _path;

    public UpdateSettingsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Reads every setting and the cached result at once. Missing or unreadable values read as their defaults.</summary>
    public UpdateSettings Read()
    {
        var file = JsonFiles.ReadObject(_path, warn: true);

        bool checkForUpdates = file?["checkForUpdates"] is not JsonValue check
            || !check.TryGetValue<bool>(out var on) || on;

        var frequency = UpdateCheckFrequency.Daily;
        if (file?["checkFrequency"] is JsonValue frequencyValue
            && frequencyValue.TryGetValue<string>(out var frequencyText)
            && Enum.TryParse<UpdateCheckFrequency>(frequencyText, ignoreCase: true, out var parsedFrequency)
            && Enum.IsDefined(parsedFrequency))
            frequency = parsedFrequency;

        bool includePrereleases = file?["includePrereleases"] is JsonValue include
            && include.TryGetValue<bool>(out var included) && included;

        DateTimeOffset? checkedUtc = null;
        if (file?["lastCheckedUtc"] is JsonValue stamp
            && stamp.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, out var parsed))
            checkedUtc = parsed;
        string? tag = file?["cachedTag"] is JsonValue tagValue
            && tagValue.TryGetValue<string>(out var t) && !string.IsNullOrWhiteSpace(t) ? t : null;
        bool prerelease = file?["cachedPrerelease"] is JsonValue pre
            && pre.TryGetValue<bool>(out var p) && p;
        bool? cachedInclude = file?["cachedIncludePrereleases"] is JsonValue cachedIncludeValue
            && cachedIncludeValue.TryGetValue<bool>(out var ci) ? ci : null;

        return new UpdateSettings(checkForUpdates, frequency, includePrereleases, checkedUtc, tag, prerelease, cachedInclude);
    }

    /// <summary>Stores whether the update check runs. Gives false when it couldn't be written.</summary>
    public bool SetCheckForUpdates(bool check) => JsonFiles.Update(_path, file =>
    {
        file ??= [];
        file["checkForUpdates"] = check;
        return file;
    });

    /// <summary>Stores how often the update check runs. Gives false when it couldn't be written.</summary>
    public bool SetCheckFrequency(UpdateCheckFrequency frequency) => JsonFiles.Update(_path, file =>
    {
        file ??= [];
        if (!Enum.IsDefined(frequency))
            frequency = UpdateCheckFrequency.Daily;
        file["checkFrequency"] = frequency.ToString();
        return file;
    });

    /// <summary>Stores whether pre-releases count. Gives false when it couldn't be written.</summary>
    public bool SetIncludePrereleases(bool include) => JsonFiles.Update(_path, file =>
    {
        file ??= [];
        file["includePrereleases"] = include;
        return file;
    });

    /// <summary>
    /// Stores a successful check's result: the tag it found (or null), whether that's a pre-release, and the
    /// <paramref name="includePrereleases"/> it was found with. Gives false when it couldn't be written.
    /// </summary>
    public bool SetCached(DateTimeOffset checkedUtc, string? tag, bool prerelease, bool includePrereleases) => JsonFiles.Update(_path, file =>
    {
        file ??= [];
        file["lastCheckedUtc"] = checkedUtc.UtcDateTime.ToString("o");
        if (string.IsNullOrWhiteSpace(tag))
            file.Remove("cachedTag");
        else
            file["cachedTag"] = tag;
        file["cachedPrerelease"] = prerelease;
        file["cachedIncludePrereleases"] = includePrereleases;
        return file;
    });
}
