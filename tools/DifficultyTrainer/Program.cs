namespace DifficultyTrainer;

/// <summary>Neural pack trainer (spec §6.1). Commands: prepare, train, evaluate, inspect.</summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: DifficultyTrainer <prepare|train|evaluate|inspect|selftest|draft> [options]");
            return 2;
        }

        return args[0].ToLowerInvariant() switch
        {
            "prepare" => Prepare.Run(args[1..]),
            "train" => Train.Run(args[1..]),
            "evaluate" => Evaluate.Run(args[1..]),
            "inspect" => Inspect.Run(args[1..]),
            "selftest" => SelfTest.Run(args[1..]),
            "draft" => Draft.Run(args[1..]),
            _ => Fail(args[0]),
        };
    }

    private static int Fail(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Use prepare, train, evaluate, inspect, selftest or draft.");
        return 2;
    }
}
