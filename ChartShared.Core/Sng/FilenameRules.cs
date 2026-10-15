using System.Buffers;
using System.Globalization;
using System.Text;
using ChartShared.Core.Resources;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Sng;

/// <summary>Why a stored filename breaks the rules.</summary>
/// <param name="RuleId"><see cref="SngRules.Sng030"/> or <see cref="SngRules.Sng031"/>.</param>
/// <param name="Reason">A short, user-facing explanation.</param>
public sealed record FilenameProblem(string RuleId, string Reason);

/// <summary>The filename rules (sng-spec §7, A1, A12), the filename registry (§8) and the junk list (A14).</summary>
public static class FilenameRules
{
    /// <summary>The <c>filenameLen</c> field is one byte.</summary>
    public const int MaxStoredNameBytes = 255;

    internal static readonly SearchValues<char> ReservedChars = SearchValues.Create("<>:\"/\\|?*");

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", ".DS_Store", "Thumbs.db", "ps.dat", "ch.dat",
    };

    /// <summary>A folder whose whole contents are junk (A14).</summary>
    public const string JunkFolder = "__MACOSX";

    private static readonly HashSet<string> Registry = BuildRegistry();

    /// <summary>Whether <paramref name="name"/> is in the filename registry (sng-spec §8), ignoring case.</summary>
    public static bool IsRegistered(string name) => Registry.Contains(name);

    /// <summary>
    /// Lowercases a registered name (A10). Only top-level names can be registered, because the
    /// registry has no folders; everything else is returned as it is.
    /// </summary>
    public static string NormaliseStoredName(string storedName)
    {
        ArgumentNullException.ThrowIfNull(storedName);
        return !storedName.Contains('/') && IsRegistered(storedName)
            ? storedName.ToLowerInvariant()
            : storedName;
    }

    /// <summary>Whether a file or folder name is OS or tool junk that is never packed (A14), ignoring case.</summary>
    public static bool IsJunk(string segment) =>
        Junk.Contains(segment) || segment.Equals(JunkFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks a stored name, segment by segment (A1). Returns <see langword="null"/> when the
    /// name is safe to store and to extract on every OS.
    /// </summary>
    public static FilenameProblem? FindProblem(string storedName)
    {
        ArgumentNullException.ThrowIfNull(storedName);

        int bytes = Encoding.UTF8.GetByteCount(storedName);
        if (bytes > MaxStoredNameBytes)
            return new FilenameProblem(SngRules.Sng031, Format(FindingMessages.ReasonTooLongFormat, bytes, MaxStoredNameBytes));

        if (storedName.Contains("..", StringComparison.Ordinal))
            return new FilenameProblem(SngRules.Sng030, FindingMessages.ReasonDotDot);

        foreach (string segment in storedName.Split('/'))
        {
            if (segment.Length == 0)
                return new FilenameProblem(SngRules.Sng030, FindingMessages.ReasonEmptySegment);

            foreach (char c in segment)
            {
                if (c < 0x20 || c == 0x7F)
                    return new FilenameProblem(SngRules.Sng030, FindingMessages.ReasonControlCharacter);
            }

            int reserved = segment.AsSpan().IndexOfAny(ReservedChars);
            if (reserved >= 0)
                return new FilenameProblem(SngRules.Sng030, Format(FindingMessages.ReasonReservedCharacterFormat, segment[reserved]));

            if (segment[^1] is '.' or ' ')
                return new FilenameProblem(SngRules.Sng030, FindingMessages.ReasonTrailingCharacter);

            // CON and CON.txt are both reserved; the name before the first dot is what counts.
            int dot = segment.IndexOf('.');
            string stem = dot < 0 ? segment : segment[..dot];
            if (WindowsReservedNames.Contains(stem))
                return new FilenameProblem(SngRules.Sng030, Format(FindingMessages.ReasonReservedNameFormat, stem));
        }

        return null;
    }

    private static HashSet<string> BuildRegistry()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ChartFiles.ChartName, ChartFiles.MidiName };

        void Add(string[] stems, string[] extensions)
        {
            foreach (string stem in stems)
            {
                foreach (string extension in extensions)
                    names.Add(stem + "." + extension);
            }
        }

        Add(["album", SongBackgrounds.ImageStem, "highway"], [.. SongBackgrounds.ImageExtensions.Select(e => e.TrimStart('.'))]);
        Add([SongBackgrounds.VideoStem], [.. SongBackgrounds.VideoExtensions.Select(e => e.TrimStart('.'))]);
        Add([.. AudioStems.Stems], [.. AudioStems.Extensions.Select(e => e.TrimStart('.'))]);
        return names;
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
