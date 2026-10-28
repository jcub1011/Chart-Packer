namespace ChartShared.Core.Diagnostics;

/// <summary>
/// The <c>--crash</c> option either app takes: once it has started, an exception is thrown on its UI thread, so the
/// crash dialog can be seen without a real fault. It goes with the app's other arguments, e.g.
/// <c>--crash --new song.ogg</c>, and is taken out before they're read.
/// </summary>
public static class SimulatedCrash
{
    public const string Option = "--crash";

    /// <summary>
    /// <paramref name="args"/> without <see cref="Option"/>, and whether it was there. Only options count: after
    /// <c>--</c>, which ends them, <c>--crash</c> is a path like any other argument and is kept.
    /// </summary>
    public static IReadOnlyList<string> Take(IReadOnlyList<string> args, out bool requested)
    {
        ArgumentNullException.ThrowIfNull(args);
        requested = false;
        List<string> rest = new(args.Count);
        bool options = true;
        foreach (string arg in args)
        {
            if (options && arg == Option)
            {
                requested = true;
                continue;
            }

            if (arg == "--")
                options = false;
            rest.Add(arg);
        }

        return rest;
    }
}

/// <summary>What <see cref="SimulatedCrash.Option"/> throws.</summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base($"A simulated crash, asked for with {SimulatedCrash.Option}.")
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
