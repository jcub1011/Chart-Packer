namespace ChartShared.Core.Launching;

/// <summary>What Chart Builder opens on when it starts.</summary>
public enum BuilderStart
{
    /// <summary>The home screen.</summary>
    Home,

    /// <summary>A song folder or <c>.sng</c>, in the editor.</summary>
    OpenSong,

    /// <summary>A new chart, in the editor.</summary>
    NewChart,

    /// <summary>The audio/visual calibration tool.</summary>
    Calibrate,
}

/// <summary>Why Chart Builder's arguments can't be used. Chart Builder then opens the home screen and says why.</summary>
public enum BuilderArgsProblemKind
{
    /// <summary>An option Chart Builder doesn't know; the problem's argument is the option.</summary>
    UnknownOption,

    /// <summary>
    /// An argument the form doesn't take, such as a second song path or an empty one; the problem's argument
    /// is the first such argument.
    /// </summary>
    UnexpectedArgument,
}

/// <summary>Why Chart Builder's arguments can't be used.</summary>
/// <param name="Kind">What's wrong.</param>
/// <param name="Argument">The argument at fault.</param>
public sealed record BuilderArgsProblem(BuilderArgsProblemKind Kind, string Argument);

/// <summary>
/// Chart Builder's command line (charter-spec §4.8). Chart Packer builds it with <see cref="ForSong"/>
/// and <see cref="ForCalibrate"/>, and Chart Builder reads it with <see cref="Parse"/>.
/// </summary>
/// <remarks>
/// The forms are: nothing (the home screen); <c>[--] &lt;song path&gt;</c>;
/// <c>--new [--] [&lt;audio file&gt;…]</c>; and <c>--calibrate</c>. <c>--</c> ends the options, so a path
/// that starts with <c>-</c> is still a path. Paths are made absolute against the current folder, so they
/// don't depend on it once read; whether they exist is checked when they're used.
/// </remarks>
public sealed record BuilderArgs
{
    public const string NewOption = "--new";
    public const string CalibrateOption = "--calibrate";
    public const string EndOfOptions = "--";

    /// <summary>What to open.</summary>
    public BuilderStart Start { get; init; }

    /// <summary>For <see cref="BuilderStart.OpenSong"/>, the song folder or <c>.sng</c>, as a full path.</summary>
    public string? SongPath { get; init; }

    /// <summary>For <see cref="BuilderStart.NewChart"/>, the audio files to start the chart with, as full paths.</summary>
    public IReadOnlyList<string> AudioFiles { get; init; } = [];

    /// <summary>Why the arguments can't be used, or null when they can. <see cref="Start"/> is then <see cref="BuilderStart.Home"/>.</summary>
    public BuilderArgsProblem? Problem { get; init; }

    /// <summary>Reads Chart Builder's command-line arguments.</summary>
    public static BuilderArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
            return new BuilderArgs();

        switch (args[0])
        {
            case NewOption:
                return ParseNewChart(args);
            case CalibrateOption:
                return args.Count == 1
                    ? new BuilderArgs { Start = BuilderStart.Calibrate }
                    : Fail(BuilderArgsProblemKind.UnexpectedArgument, args[1]);
            case EndOfOptions:
                return ParseSong(args, 1);
            case var option when IsOption(option):
                return Fail(BuilderArgsProblemKind.UnknownOption, option);
            default:
                return ParseSong(args, 0);
        }
    }

    /// <summary>The arguments that open <paramref name="songPath"/> in the editor.</summary>
    public static IReadOnlyList<string> ForSong(string songPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
        // A full path never looks like an option, so it needs no "--" before it.
        return [Path.GetFullPath(songPath)];
    }

    /// <summary>The arguments that open the calibration tool.</summary>
    public static IReadOnlyList<string> ForCalibrate() => [CalibrateOption];

    // The song form: one path at args[first], and nothing after it.
    private static BuilderArgs ParseSong(IReadOnlyList<string> args, int first)
    {
        if (args.Count <= first)
            return Fail(BuilderArgsProblemKind.UnexpectedArgument, args[^1]);
        if (args.Count > first + 1)
            return Fail(BuilderArgsProblemKind.UnexpectedArgument, args[first + 1]);
        if (string.IsNullOrWhiteSpace(args[first]))
            return Fail(BuilderArgsProblemKind.UnexpectedArgument, args[first]);

        return new BuilderArgs { Start = BuilderStart.OpenSong, SongPath = Path.GetFullPath(args[first]) };
    }

    private static BuilderArgs ParseNewChart(IReadOnlyList<string> args)
    {
        List<string> files = [];
        bool options = true;
        foreach (string arg in args.Skip(1))
        {
            if (options && arg == EndOfOptions)
                options = false;
            else if (options && IsOption(arg))
                return Fail(BuilderArgsProblemKind.UnknownOption, arg);
            else if (string.IsNullOrWhiteSpace(arg))
                return Fail(BuilderArgsProblemKind.UnexpectedArgument, arg);
            else
                files.Add(Path.GetFullPath(arg));
        }

        return new BuilderArgs { Start = BuilderStart.NewChart, AudioFiles = files };
    }

    private static bool IsOption(string arg) => arg.StartsWith("--", StringComparison.Ordinal);

    private static BuilderArgs Fail(BuilderArgsProblemKind kind, string argument) =>
        new() { Problem = new BuilderArgsProblem(kind, argument) };
}
