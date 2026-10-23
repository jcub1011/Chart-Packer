using System.Globalization;

namespace ChartShared.Core.Launching;

/// <summary>
/// Chart Builder's difficulty report command line: <c>--difficulty-report &lt;songs folder&gt; [--out &lt;file.csv&gt;]
/// [--depth &lt;n&gt;]</c>. It's run without a window: every song under the folder is rated by the difficulty model, and a
/// CSV written with each part's <c>song.ini</c> rating beside the model's (<see cref="Charting.Difficulty.DifficultyReport"/>).
/// </summary>
/// <remarks>
/// Options come in any order after the command. Paths are made absolute against the current folder. Without
/// <c>--out</c>, the CSV is <see cref="DefaultFileName"/> in the current folder; without <c>--depth</c>, folders are looked
/// in down to <see cref="DefaultDepth"/> levels below the songs folder.
/// </remarks>
public sealed record DifficultyReportArgs
{
    public const string Command = "--difficulty-report";
    public const string OutOption = "--out";
    public const string DepthOption = "--depth";

    /// <summary>The CSV's name when <c>--out</c> isn't given.</summary>
    public const string DefaultFileName = "difficulty-report.csv";

    /// <summary>How many folder levels below the songs folder are looked in when <c>--depth</c> isn't given.</summary>
    public const int DefaultDepth = 10;

    /// <summary>How the command is used, for its error messages.</summary>
    public const string Usage = "ChartBuilder --difficulty-report <songs folder> [--out <file.csv>] [--depth <levels>]";

    /// <summary>The folder searched for songs, as a full path.</summary>
    public string SongsFolder { get; init; } = "";

    /// <summary>Where the CSV is written, as a full path.</summary>
    public string OutputFile { get; init; } = "";

    /// <summary>How many folder levels below <see cref="SongsFolder"/> are looked in for songs.</summary>
    public int Depth { get; init; } = DefaultDepth;

    /// <summary>Why the arguments can't be used, or null when they can.</summary>
    public string? Problem { get; init; }

    /// <summary>Whether <paramref name="args"/> ask for the report, rather than for the editor.</summary>
    public static bool IsReport(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 && args[0] == Command;
    }

    /// <summary>Reads the report's arguments, the first of which is <see cref="Command"/>.</summary>
    public static DifficultyReportArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsReport(args))
            return Fail($"The first argument isn't {Command}.");

        string? folder = null, output = null;
        int depth = DefaultDepth;
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case OutOption or DepthOption when i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]):
                    return Fail($"{arg} needs a value.");
                case OutOption:
                    output = args[++i];
                    break;
                case DepthOption:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out depth))
                        return Fail($"{DepthOption} needs a whole number of folder levels, 0 or more, not \"{args[i]}\".");
                    break;
                case var option when option.StartsWith("--", StringComparison.Ordinal):
                    return Fail($"Unknown option {option}.");
                case var _ when folder is not null:
                    return Fail($"Only one songs folder can be given; \"{arg}\" is a second.");
                case var _ when string.IsNullOrWhiteSpace(arg):
                    return Fail("The songs folder is empty.");
                default:
                    folder = arg;
                    break;
            }
        }

        if (folder is null)
            return Fail("No songs folder was given.");
        return new DifficultyReportArgs
        {
            SongsFolder = Path.GetFullPath(folder),
            OutputFile = Path.GetFullPath(output ?? DefaultFileName),
            Depth = depth,
        };
    }

    private static DifficultyReportArgs Fail(string problem) => new() { Problem = problem };
}
