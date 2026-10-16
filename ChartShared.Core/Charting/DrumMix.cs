using System.Globalization;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>
/// A drum mix event on one difficulty, <c>[mix &lt;difficulty&gt; drums&lt;configuration&gt;&lt;setting&gt;]</c>; the
/// difficulty is its track's.
/// </summary>
/// <param name="Configuration">Which stems the drums are mixed into, 0 to 5.</param>
/// <param name="Setting">What follows the configuration, letters and digits only: <c>d</c> for disco flip, or another setting, or none.</param>
public readonly record struct DrumMix(long Tick, int Configuration, string Setting)
{
    /// <summary>The setting that turns disco flip on.</summary>
    public const string DiscoFlipSetting = "d";

    /// <summary>Whether it turns disco flip on (any other mix turns it off).</summary>
    public bool DiscoFlip => Setting == DiscoFlipSetting;

    /// <summary>The text event that writes it on <paramref name="difficulty"/>, such as <c>[mix 3 drums0d]</c>.</summary>
    public string TextFor(ChartDifficulty difficulty) =>
        string.Create(CultureInfo.InvariantCulture, $"[mix {(int)difficulty} drums{Configuration}{Setting}]");

    /// <summary>
    /// Reads a mix event written exactly as <see cref="TextFor"/> writes one, so that it's written back the same. Other
    /// spellings the games accept (underscores, spaces, no brackets) are left to be kept as text events.
    /// </summary>
    internal static bool TryParse(string text, long tick, out ChartDifficulty difficulty, out DrumMix mix)
    {
        difficulty = default;
        mix = default;
        const string Head = "[mix ";
        const string Middle = " drums";
        var rest = text.AsSpan();
        if (!rest.StartsWith(Head, StringComparison.Ordinal) || !rest.EndsWith("]", StringComparison.Ordinal))
            return false;
        rest = rest[Head.Length..^1];
        if (rest.Length < 1 + Middle.Length + 1 || rest[0] is < '0' or > '3' || !rest[1..].StartsWith(Middle, StringComparison.Ordinal))
            return false;
        char configuration = rest[1 + Middle.Length];
        var setting = rest[(2 + Middle.Length)..];
        if (configuration is < '0' or > '5' || !IsSetting(setting))
            return false;

        difficulty = (ChartDifficulty)(rest[0] - '0');
        mix = new DrumMix(tick, configuration - '0', setting.ToString());
        return true;
    }

    /// <summary>Whether <paramref name="setting"/> can be a mix's setting: letters and digits only.</summary>
    internal static bool IsSetting(ReadOnlySpan<char> setting)
    {
        foreach (char c in setting)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }

        return true;
    }
}
